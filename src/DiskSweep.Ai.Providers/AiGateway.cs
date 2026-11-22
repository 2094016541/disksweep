using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DiskSweep.Ai.Abstractions;

namespace DiskSweep.Ai.Providers;

/// <summary>
/// 唯一与 provider 打交道的组件：预算闸门 → 缓存 → 调用与重试 → 输出校验 → 用量记账（文档 6.5）。
/// 任何失败都返回空结果，不抛异常、不阻塞扫描。
/// </summary>
public sealed class AiGateway : IRecommendationEnricher
{
    private readonly IAiProvider _provider;
    private readonly IAiStorage _storage;
    private readonly AiOptions _options;
    private readonly Func<bool> _isEnabled;
    private readonly ProviderDescriptor _descriptor;
    private CancellationTokenSource? _inFlight;
    private static readonly IReadOnlyList<EnrichmentResult> Empty = Array.Empty<EnrichmentResult>();

    public string Status { get; private set; } = "待命";

    public AiGateway(IAiProvider provider, IAiStorage storage, AiOptions options,
        ProviderDescriptor descriptor, Func<bool> isEnabled)
    {
        _provider = provider;
        _storage = storage;
        _options = options;
        _descriptor = descriptor;
        _isEnabled = isEnabled;
    }

    /// <summary>运行中关闭开关：取消 in-flight 请求且不等待其返回，扫描主线程继续纯规则收敛。</summary>
    public void Disable()
    {
        try { _inFlight?.Cancel(); } catch { /* 已释放 */ }
        Status = "已关闭";
    }

    public async Task<IReadOnlyList<EnrichmentResult>> EnrichAsync(
        IReadOnlyList<CandidateDigest> unknowns, CancellationToken ct)
    {
        if (!_isEnabled() || unknowns.Count == 0) { Status = "已关闭"; return Empty; }

        var items = unknowns.Take(Math.Max(1, _options.MaxCandidatesPerScan)).ToList();
        var digestJson = JsonSerializer.Serialize(items);
        var fingerprint = Fingerprint(digestJson);

        // 1. 预算闸门：调用前预估，超限直接返回空且不发起请求
        var (usedCost, usedRequests) = (_storage.GetMonthUsageUsd(DateTime.UtcNow),
                                        _storage.GetMonthRequestCount(DateTime.UtcNow));
        if (usedRequests >= _options.MaxRequestsPerMonth)
        { Status = "本月请求数已达上限"; return Empty; }
        var estimate = EstimateCost(digestJson.Length, items.Count);
        if (usedCost + estimate > _options.MonthlyBudgetUsd)
        { Status = $"本月预算不足（已用 ${usedCost:F4} / 上限 ${_options.MonthlyBudgetUsd:F2}）"; return Empty; }

        // 2. 缓存（fingerprint 纳入 provider / model / promptVersion）
        var cached = _storage.GetCachedResult(fingerprint, _options.CacheTtl);
        if (cached is not null)
        {
            Status = "命中缓存";
            return Deserialize(cached);
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(_options.Timeout);                 // 默认 15 s，断网不拖死扫描
        _inFlight = linked;
        try
        {
            AiClassifyResponse? response = null;
            bool parseFailed = false;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    response = await _provider.ClassifyAsync(items, linked.Token);
                    break;
                }
                catch (FormatException) when (attempt < 1)
                {
                    parseFailed = true;                     // 反序列化失败重试一次
                    await Task.Delay(150, linked.Token);
                }
                catch (HttpRequestException ex) when (attempt < 2 && IsRetryable(ex))
                {
                    await Task.Delay(200 * (1 << attempt), linked.Token);   // 429 / 5xx 指数退避
                }
            }

            if (response is null)
            {
                Status = "AI 不可用（超时 / 限流 / 输出无法解析）";
                return parseFailed ? DegradeToReview(items) : Empty;
            }

            // 4. 输出校验：枚举白名单、Token 必须属于本批次、长度上限、控制字符剥离
            var results = Validate(response, items);
            Status = $"完成（{results.Count} 条建议）";

            // 5. 用量记账（按天）
            double cost = ActualCost(response);
            _storage.AddUsage(DateTime.UtcNow, _descriptor.Id, 1,
                response.InputTokens, response.OutputTokens, cost);
            _storage.PutCachedResult(fingerprint, _descriptor.Id, _options.Model,
                _options.PromptVersion, JsonSerializer.Serialize(results), DateTime.UtcNow);
            _storage.PruneCache(100_000, TimeSpan.FromDays(30));
            return results;
        }
        catch (OperationCanceledException)
        {
            // 被取消的请求：不写缓存、不记用量
            Status = "已取消";
            return Empty;
        }
        catch (Exception)
        {
            Status = "AI 不可用，已降级为纯规则";
            return Empty;                                  // 6.6 降级矩阵：不弹错误框
        }
        finally { _inFlight = null; }
    }

    private string Fingerprint(string digestJson)
    {
        var input = string.Join('\0',
            _descriptor.Id, _options.Model, _options.PromptVersion, digestJson);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
    }

    private double EstimateCost(int jsonLength, int count)
    {
        long inputTokens = jsonLength / 4 + 400;
        long outputTokens = count * 90L;
        return inputTokens / 1_000_000d * _descriptor.PricePer1MInputTokens +
               outputTokens / 1_000_000d * _descriptor.PricePer1MOutputTokens;
    }

    private double ActualCost(AiClassifyResponse response) =>
        response.InputTokens / 1_000_000d * _descriptor.PricePer1MInputTokens +
        response.OutputTokens / 1_000_000d * _descriptor.PricePer1MOutputTokens;

    private static bool IsRetryable(HttpRequestException ex)
    {
        if (ex.StatusCode is null) return true;              // DNS / 连接失败（离线）重试一次
        int code = (int)ex.StatusCode.Value;
        return code == 429 || code >= 500;                   // 4xx 一律不重试
    }

    private static IReadOnlyList<EnrichmentResult> Validate(
        AiClassifyResponse response, IReadOnlyList<CandidateDigest> items)
    {
        var validTokens = items.Select(i => i.Token).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var results = new List<EnrichmentResult>();
        foreach (var v in response.Verdicts)
        {
            if (string.IsNullOrEmpty(v.Token) || !validTokens.Contains(v.Token)) continue;
            if (!seen.Add(v.Token)) continue;                 // 重复 token 丢弃
            var action = v.Recommendation?.Trim().ToLowerInvariant();
            var risk = v.Risk?.Trim().ToLowerInvariant();
            if (action is not ("delete" or "keep" or "review")) continue;
            if (risk is not ("low" or "medium" or "high" or "critical")) continue;
            double confidence = double.IsFinite(v.Confidence) ? Math.Clamp(v.Confidence, 0, 1) : 0;
            results.Add(new EnrichmentResult(v.Token, action, risk, confidence,
                Clean(v.Reason), Clean(v.Category)));
        }
        return results;
    }

    private static IReadOnlyList<EnrichmentResult> DegradeToReview(IReadOnlyList<CandidateDigest> items) =>
        items.Select(i => new EnrichmentResult(i.Token, "review", "medium", 0,
            "模型输出无法解析，已降级为人工复核", "unknown")).ToArray();

    private static IReadOnlyList<EnrichmentResult> Deserialize(string json)
    {
        try { return JsonSerializer.Deserialize<List<EnrichmentResult>>(json) ?? new List<EnrichmentResult>(); }
        catch (JsonException) { return Empty; }
    }

    private static string Clean(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var sb = new StringBuilder(Math.Min(value.Length, 200));
        foreach (var c in value)
        {
            if (char.IsControl(c)) continue;
            sb.Append(c);
            if (sb.Length >= 200) break;
        }
        return sb.ToString();
    }
}
