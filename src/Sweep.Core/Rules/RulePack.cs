using System.Security.Cryptography;
using System.Text.Json;
using Sweep.Core.Safety;

namespace Sweep.Core.Rules;

/// <summary>规则包格式（文档 4.2.2），外置 JSON，改 JSON 即生效。</summary>
public sealed class RuleDefinition
{
    public string Id { get; set; } = "";
    public string Category { get; set; } = "其他";
    public List<string> Paths { get; set; } = new();
    public string Action { get; set; } = "delete";          // delete | keep | review（keep/review 用于保护规则）
    public string Risk { get; set; } = "low";
    public bool DefaultSelected { get; set; }
    public bool RequiresAdmin { get; set; }
    public int MinAgeDays { get; set; }
    public string Handling { get; set; } = "glob-delete";
    public string Description { get; set; } = "";
    public string ReclaimNote { get; set; } = "";
    public int Priority { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public List<GlobMatcher> Matchers { get; set; } = new();

    [System.Text.Json.Serialization.JsonIgnore]
    public RiskLevel RiskLevel => Risk?.ToLowerInvariant() switch
    {
        "critical" => RiskLevel.Critical,
        "high" => RiskLevel.High,
        "medium" => RiskLevel.Medium,
        _ => RiskLevel.Low
    };

    [System.Text.Json.Serialization.JsonIgnore]
    public RecommendationAction TargetAction => Action?.ToLowerInvariant() switch
    {
        "keep" => RecommendationAction.Keep,
        "review" => RecommendationAction.Review,
        _ => RecommendationAction.Delete
    };
}

public sealed class RulePack
{
    public int SchemaVersion { get; set; } = 2;
    public string PackId { get; set; } = "";
    public string PackVersion { get; set; } = "";
    public List<RuleDefinition> Rules { get; set; } = new();
}

public sealed record RulePackLoadResult(RulePack Pack, bool IntegrityVerified, IReadOnlyList<string> Warnings);

public static class RulePackLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>8.1 红线 5：远程热更新未实现；本地分发包如带 .sha256 则校验完整性。</summary>
    public static RulePackLoadResult Load(string path, int priority, bool verifyIntegrity = true)
    {
        var warnings = new List<string>();
        if (!File.Exists(path))
            return new RulePackLoadResult(new RulePack { SchemaVersion = 2, PackId = "empty", PackVersion = "0" }, false,
                new[] { "规则包不存在：" + path });

        bool verified = false;
        if (verifyIntegrity)
        {
            var sigPath = path + ".sha256";
            if (File.Exists(sigPath))
            {
                var expected = File.ReadAllText(sigPath).Trim();
                var actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
                verified = string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
                if (!verified) warnings.Add("内置规则包完整性校验失败，已拒绝加载");
                if (!verified) return new RulePackLoadResult(new RulePack(), false, warnings);
            }
            else warnings.Add("内置规则包未附带签名文件（本地分发，未启用远程热更新）");
        }

        var pack = JsonSerializer.Deserialize<RulePack>(File.ReadAllText(path), JsonOptions) ?? new RulePack();
        if (pack.SchemaVersion > 2)
        {
            warnings.Add($"规则包 schemaVersion={pack.SchemaVersion} 高于支持的版本，已拒绝加载");
            return new RulePackLoadResult(new RulePack(), verified, warnings);
        }

        foreach (var rule in pack.Rules)
        {
            rule.Priority = priority;
            // risk >= high 时默认勾选必须为 false（文档 4.2.2）
            if (rule.RiskLevel >= RiskLevel.High) rule.DefaultSelected = false;
            foreach (var pattern in rule.Paths)
            {
                var normalized = PathNormalizer.NormalizePattern(pattern);
                if (GlobMatcher.TryCreate(normalized, out var matcher, out var error) && matcher is not null)
                    rule.Matchers.Add(matcher);
                else
                    warnings.Add($"规则 {rule.Id} 的路径模式无效：{pattern}（{error}）");
            }
        }
        return new RulePackLoadResult(pack, verified, warnings);
    }

    public static void SaveUserOverrides(string path, IEnumerable<RuleDefinition> rules)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var pack = new RulePack { SchemaVersion = 2, PackId = "user-overrides", PackVersion = "1.0", Rules = rules.ToList() };
        var json = JsonSerializer.Serialize(pack, new JsonSerializerOptions { WriteIndented = true, PropertyNameCaseInsensitive = true });
        File.WriteAllText(path, json);
    }

    public static IReadOnlyList<RuleDefinition> LoadUserOverrides(string path)
    {
        if (!File.Exists(path)) return Array.Empty<RuleDefinition>();
        try
        {
            var result = Load(path, priority: 100, verifyIntegrity: false);
            return result.Pack.Rules;
        }
        catch { return Array.Empty<RuleDefinition>(); }
    }
}
