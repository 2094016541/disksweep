using DiskSweep.Core.Safety;

namespace DiskSweep.Core.Rules;

public sealed record RuleMatch(
    RecommendationAction Action,
    RiskLevel Risk,
    bool DefaultSelected,
    string RuleId,
    string Category,
    string Handling,
    bool RequiresAdmin,
    int MinAgeDays,
    string Description,
    string ReclaimNote);

/// <summary>优先级：用户自定义 > 内置；同一路径多规则命中取更保守结论（文档 4.2.3）。</summary>
public sealed class RuleEngine
{
    private readonly IReadOnlyList<RuleDefinition> _pathRules;

    public IReadOnlyList<RuleDefinition> Rules { get; }
    public IReadOnlyList<RuleDefinition> SystemOperations { get; }
    public IReadOnlyList<string> Warnings { get; }
    public bool IntegrityVerified { get; }

    private RuleEngine(IReadOnlyList<RuleDefinition> rules, IReadOnlyList<string> warnings, bool verified)
    {
        Rules = rules;
        _pathRules = rules.Where(r => string.Equals(r.Handling, "glob-delete", StringComparison.OrdinalIgnoreCase)).ToArray();
        SystemOperations = rules.Where(r => !string.Equals(r.Handling, "glob-delete", StringComparison.OrdinalIgnoreCase)).ToArray();
        Warnings = warnings;
        IntegrityVerified = verified;
    }

    public static RuleEngine Create(string builtinPath, string? userOverridesPath = null)
    {
        var builtin = RulePackLoader.Load(builtinPath, priority: 0);
        var warnings = new List<string>(builtin.Warnings);
        var byId = new Dictionary<string, RuleDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in builtin.Pack.Rules) byId[r.Id] = r;

        if (!string.IsNullOrEmpty(userOverridesPath))
        {
            foreach (var u in RulePackLoader.LoadUserOverrides(userOverridesPath))
            {
                u.Priority = 100;
                if (u.RiskLevel >= RiskLevel.High) u.DefaultSelected = false;
                foreach (var p in u.Paths)
                {
                    if (GlobMatcher.TryCreate(PathNormalizer.NormalizePattern(p), out var m, out var err) && m is not null)
                        u.Matchers.Add(m);
                    else warnings.Add($"用户规则 {u.Id} 的模式无效：{p}（{err}）");
                }
                byId[u.Id] = u;   // 同 id 用户规则覆盖内置
            }
        }

        var rules = byId.Values
            .OrderByDescending(r => r.Priority)
            .ThenBy(r => r.Id, StringComparer.Ordinal)
            .ToArray();
        return new RuleEngine(rules, warnings, builtin.IntegrityVerified);
    }

    /// <summary>
    /// 单次遍历完成「命中 → 取最保守结论」。原实现用 LINQ 多次枚举命中集，
    /// 每个对象都要分配一个 List + 若干闭包；扫描热路径上每个对象都会调用一次，
    /// 改成零分配的循环后，百万级扫描的 GC 压力与耗时都明显下降。
    ///
    /// 为什么不做「路径前缀 → 规则」索引：实测 47 万条真实路径、32 条规则下，
    /// 逐条遍历约 1.4s，加索引反而 1.5~1.7s。匹配器的首片段 IndexOf 在第一个字符
    /// 不匹配时约 10ns，比「走分隔符 + 哈希查表」还便宜；索引只在规则数上百时才有意义。
    /// 真正贵的是命中后的正则，已由 GlobMatcher 的「目录\**」前缀快通道消掉。
    /// </summary>
    public RuleMatch? Match(string normalizedPath, int ageDays)
    {
        RuleDefinition? primary = null;
        int hits = 0;
        bool keep = false, review = false, requiresAdmin = false, allDefaultSelected = true;
        int risk = 0, minAge = 0;

        for (int i = 0; i < _pathRules.Count; i++)
        {
            var rule = _pathRules[i];
            if (!MatchesAny(rule, normalizedPath)) continue;

            if (hits == 0)
            {
                primary = rule;
                risk = (int)rule.RiskLevel;
                minAge = rule.MinAgeDays;
                allDefaultSelected = rule.DefaultSelected;
            }
            else
            {
                if ((int)rule.RiskLevel > risk) risk = (int)rule.RiskLevel;
                if (rule.MinAgeDays > minAge) minAge = rule.MinAgeDays;
                allDefaultSelected &= rule.DefaultSelected;
            }
            if (rule.TargetAction == RecommendationAction.Keep) keep = true;
            if (rule.TargetAction == RecommendationAction.Review) review = true;
            requiresAdmin |= rule.RequiresAdmin;
            hits++;
        }
        if (primary is null) return null;   // Rules 已按优先级排序，首个命中即主规则

        var action = keep ? RecommendationAction.Keep
                   : review ? RecommendationAction.Review
                   : RecommendationAction.Delete;
        bool selected = !keep && !review && risk <= (int)RiskLevel.Medium && allDefaultSelected;

        if (ageDays < minAge)
        {
            action = RecommendationAction.Keep;
            selected = false;
        }

        return new RuleMatch(action, (RiskLevel)risk, selected, primary.Id, primary.Category,
            primary.Handling, requiresAdmin, minAge, primary.Description, primary.ReclaimNote);
    }

    private static bool MatchesAny(RuleDefinition rule, string path)
    {
        var matchers = rule.Matchers;
        for (int i = 0; i < matchers.Count; i++)
            if (matchers[i].Matches(path)) return true;
        return false;
    }

    public IReadOnlyList<RuleDefinition> AllPathRules => _pathRules;
}
