using System.Text.Json.Serialization;

namespace Sweep.Ai.Abstractions;

/// <summary>发往模型的唯一数据形态：不含完整路径、盘符与用户名。</summary>
public sealed record CandidateDigest(
    string Token,
    string Category,
    string Extension,
    long SizeBytes,
    int AgeDays,
    int FileCount,
    bool HasExecutable,
    bool IsInsideWhitelist,
    string? RelativePath = null);

public sealed record AiClassifyResponse(
    IReadOnlyList<AiVerdict> Verdicts,
    int InputTokens,
    int OutputTokens);

public sealed record AiVerdict(
    string Token,
    string Recommendation,
    string Category,
    string Risk,
    double Confidence,
    string Reason,
    bool NeedsUserConfirm);

public interface IAiProvider
{
    string Id { get; }
    string DisplayName { get; }
    bool SupportsJsonMode { get; }
    Task<AiClassifyResponse> ClassifyAsync(IReadOnlyList<CandidateDigest> items, CancellationToken ct);
}

/// <summary>Core 的唯一 AI 接入点，允许为 null（文档 4.3）。</summary>
public interface IRecommendationEnricher
{
    Task<IReadOnlyList<EnrichmentResult>> EnrichAsync(
        IReadOnlyList<CandidateDigest> unknowns, CancellationToken ct);
}

public sealed record EnrichmentResult(
    string Token,
    string Action,
    string Risk,
    double Confidence,
    string Reason,
    string Category);

public sealed record ProviderDescriptor(
    string Id,
    string Label,
    string Kind,
    string BaseUrl,
    string DefaultModel,
    string JsonMode,
    bool Builtin,
    double PricePer1MInputTokens = 0,
    double PricePer1MOutputTokens = 0);

/// <summary>Providers 只依赖本程序集；缓存/用量/凭据由主程序适配到 Core 的 SQLite。</summary>
public interface IAiStorage
{
    string? GetCachedResult(string fingerprint, TimeSpan maxAge);
    void PutCachedResult(string fingerprint, string provider, string model, string promptVersion, string resultJson, DateTime createdUtc);
    double GetMonthUsageUsd(DateTime utcNow);
    int GetMonthRequestCount(DateTime utcNow);
    void AddUsage(DateTime utcNow, string provider, int requests, int inputTokens, int outputTokens, double estCostUsd);
    void PruneCache(int maxEntries, TimeSpan maxAge);
    string? GetSecret(string providerId);
    void SetSecret(string providerId, string? value);
    string GetOrCreateSalt();
}

public sealed record AiOptions(
    string ProviderId,
    string Model,
    string PromptVersion,
    double MonthlyBudgetUsd,
    int MaxCandidatesPerScan,
    int MaxRequestsPerMonth,
    bool PrivacyMode,
    TimeSpan CacheTtl,
    TimeSpan Timeout);

/// <summary>6.9 自然语言 → 规则 DSL（仅翻译 + 预览，不直接执行）。</summary>
public sealed record RuleDsl(
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("extensions")] IReadOnlyList<string> Extensions,
    [property: JsonPropertyName("olderThanDays")] int OlderThanDays,
    [property: JsonPropertyName("excludeDrives")] IReadOnlyList<string> ExcludeDrives);
