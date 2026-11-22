using System.Text;
using Sweep.Ai.Abstractions;
using Sweep.Core.Rules;
using Sweep.Core.Safety;
using Sweep.Core.Scanning;

namespace Sweep.Core.Recommendation;

public sealed class AiBatch
{
    public required IReadOnlyList<CandidateDigest> Digests { get; init; }
    public required IReadOnlyDictionary<string, PlanItem> Lookup { get; init; }
}

/// <summary>规则与 AI 汇入同一 Recommendation 类型，全部经闸门收敛（文档 4.3 / 4.4）。</summary>
public static class RecommendationEngine
{
    private const uint SparseOrCompressed = 0x200 | 0x800;
    private const uint CloudPlaceholder = 0x40000 | 0x400000;

    public static IReadOnlyList<PlanItem> BuildRulePlan(ScanResult scan, RuleEngine rules, SafetyContext ctx)
    {
        var duplicateOriginal = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in scan.Duplicates)
            foreach (var p in group.Paths.Skip(1))
                duplicateOriginal[p] = group.Paths[0];

        var result = new List<PlanItem>(scan.Items.Count);
        foreach (var item in scan.Items)
        {
            Recommendation rec;
            string handling = "glob-delete";
            bool requiresAdmin = false;
            bool defaultSelected = false;
            string description;
            string reclaimNote = "";
            var category = item.Category;

            if (scan.RuleMatches.TryGetValue(item.Path, out var match))
            {
                rec = new Recommendation(item.Path, RecommendationSource.Rule, match.Action,
                    match.Risk, 1.0, match.Description, match.RuleId);
                handling = match.Handling;
                requiresAdmin = match.RequiresAdmin;
                defaultSelected = match.DefaultSelected;
                description = match.Description;
                reclaimNote = match.ReclaimNote;
                category = match.Category;
            }
            else if (category == "大文件")
            {
                rec = new Recommendation(item.Path, RecommendationSource.Rule, RecommendationAction.Review,
                    RiskLevel.Medium, 1.0, $"单个文件超过阈值（{PlanItem.FormatSize(item.AllocationSize)}，{item.AgeDays} 天未修改）", "large-file");
                description = "大文件，需人工确认用途";
                reclaimNote = "删除前请确认不是虚拟机磁盘、安装包或备份";
            }
            else if (category == "重复文件")
            {
                var original = duplicateOriginal.TryGetValue(item.Path, out var o) ? o : "另一份相同内容的文件";
                rec = new Recommendation(item.Path, RecommendationSource.Rule, RecommendationAction.Review,
                    RiskLevel.Medium, 1.0, $"内容哈希与 {original} 相同", "duplicate");
                description = "重复文件，保留其中一份";
                reclaimNote = "已自动保留首个原件，其余列为候选";
            }
            else
            {
                rec = new Recommendation(item.Path, RecommendationSource.Rule, RecommendationAction.Review,
                    RiskLevel.Medium, 1.0, "未被内置规则覆盖的候选", null);
                description = "未知项目，需人工判断";
                reclaimNote = "可加入白名单永久排除";
            }

            var gated = SafetyGate.Apply(rec, ctx);
            bool lowReclaim = ((uint)item.Attributes & (SparseOrCompressed | CloudPlaceholder)) != 0;
            if (lowReclaim)
                gated = gated with { Reason = gated.Reason + "；占用为共享/占位，实际释放量可能偏低" };

            var plan = new PlanItem
            {
                Path = gated.Path,
                Recommendation = gated,
                Category = category,
                AllocationSize = item.AllocationSize,
                VolumeSerial = item.VolumeSerial,
                FileId = item.FileId,
                IsDirectory = item.IsDirectory,
                Handling = handling,
                RequiresAdmin = requiresAdmin,
                Description = description,
                ReclaimNote = string.IsNullOrEmpty(reclaimNote) ? gated.Reason : reclaimNote,
                IsSystemOperation = false,
                LowReclaimEstimate = lowReclaim,
                AgeDays = item.AgeDays,
                DefaultSelected = defaultSelected
            };
            // 高风险、AI、critical、需管理员项一律默认不勾选
            plan.IsSelected = gated.Action == RecommendationAction.Delete &&
                              defaultSelected &&
                              !gated.RequiresExplicitConfirm &&
                              gated.Risk <= RiskLevel.Medium;
            result.Add(plan);
        }
        return result;
    }

    /// <summary>系统维护项不走通用删除路径（文档 7.2），由闸门专用分支强制人工复核。</summary>
    public static IReadOnlyList<PlanItem> BuildSystemOperations(RuleEngine rules, SafetyContext ctx)
    {
        var result = new List<PlanItem>();
        foreach (var rule in rules.SystemOperations)
        {
            var rawPath = rule.Paths.FirstOrDefault() ?? ctx.SystemDrive;
            var rec = new Recommendation(rawPath, RecommendationSource.Rule, RecommendationAction.Review,
                rule.RiskLevel, 1.0, rule.Description, rule.Id, RequiresExplicitConfirm: true);
            var gated = SafetyGate.ApplySystemOperation(rec, ctx);
            result.Add(new PlanItem
            {
                Path = gated.Path,
                Recommendation = gated,
                Category = "系统维护（不可回退）",
                AllocationSize = 0,
                VolumeSerial = 0,
                FileId = 0,
                IsDirectory = true,
                Handling = rule.Handling,
                RequiresAdmin = rule.RequiresAdmin,
                Description = rule.Description,
                ReclaimNote = rule.ReclaimNote,
                IsSystemOperation = true,
                LowReclaimEstimate = false,
                AgeDays = 0,
                DefaultSelected = false,
                IsSelected = false
            });
        }
        return result;
    }

    public static AiBatch? CreateAiBatch(IReadOnlyList<PlanItem> plan, SafetyContext ctx,
        string salt, bool privacyMode, int maxCandidates = 200)
    {
        var unknowns = plan
            .Where(p => !p.IsSystemOperation &&
                        p.Action != RecommendationAction.Keep &&
                        (p.Category == "大文件" || p.Category == "重复文件" || p.Category == "其他" || p.Category == "未分类"))
            .Take(maxCandidates)
            .ToList();
        if (unknowns.Count == 0) return null;

        var digests = new List<CandidateDigest>(unknowns.Count);
        var lookup = new Dictionary<string, PlanItem>(StringComparer.Ordinal);
        for (int i = 0; i < unknowns.Count; i++)
        {
            var p = unknowns[i];
            var referenceId = $"b1-{i:D3}";
            var ext = Path.GetExtension(p.Path);
            bool executable = ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
                              ext.Equals(".dll", StringComparison.OrdinalIgnoreCase) ||
                              ext.Equals(".msi", StringComparison.OrdinalIgnoreCase) ||
                              ext.Equals(".sys", StringComparison.OrdinalIgnoreCase);
            var normalized = PathNormalizer.Normalize(p.Path) ?? p.Path;
            digests.Add(Redactor.ToDigest(referenceId, p.Path, p.Category, ext, p.AllocationSize, p.AgeDays,
                executable, ctx.Whitelist.Matches(normalized), ctx.CurrentUserProfile, salt, privacyMode));
            lookup[referenceId] = p;
        }
        return new AiBatch { Digests = digests, Lookup = lookup };
    }

    public static IReadOnlyList<PlanItem> MergeAiRecommendations(
        AiBatch batch, IReadOnlyList<EnrichmentResult> verdicts, SafetyContext ctx)
    {
        var result = new List<PlanItem>();
        foreach (var v in verdicts)
        {
            if (string.IsNullOrWhiteSpace(v.Token) || !batch.Lookup.TryGetValue(v.Token, out var src)) continue;
            var action = ParseAction(v.Action);
            var risk = ParseRisk(v.Risk);
            if (action is null || risk is null) continue;

            var rec = new Recommendation(src.Path, RecommendationSource.Ai, action.Value, risk.Value,
                Math.Clamp(v.Confidence, 0, 1), Clean(v.Reason, 200), null, RequiresExplicitConfirm: true);
            var gated = SafetyGate.Apply(rec, ctx);   // 强制显式确认 / 低置信转人工
            result.Add(new PlanItem
            {
                Path = gated.Path,
                Recommendation = gated,
                Category = "AI 建议（逐项确认）",
                AllocationSize = src.AllocationSize,
                VolumeSerial = src.VolumeSerial,
                FileId = src.FileId,
                IsDirectory = src.IsDirectory,
                Handling = "glob-delete",
                RequiresAdmin = false,
                Description = Clean(v.Reason, 200),
                ReclaimNote = $"AI 分类：{Clean(v.Category, 60)}；该建议永不进入定时静默清理",
                IsSystemOperation = false,
                LowReclaimEstimate = src.LowReclaimEstimate,
                AgeDays = src.AgeDays,
                DefaultSelected = false,
                IsSelected = false
            });
        }
        return result;
    }

    private static RecommendationAction? ParseAction(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "delete" => RecommendationAction.Delete,
        "keep" => RecommendationAction.Keep,
        "review" => RecommendationAction.Review,
        _ => null
    };

    private static RiskLevel? ParseRisk(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "low" => RiskLevel.Low,
        "medium" => RiskLevel.Medium,
        "high" => RiskLevel.High,
        "critical" => RiskLevel.Critical,
        _ => null
    };

    /// <summary>控制字符剥离 + 长度上限（6.8 提示注入防御，本地再兜一层）。</summary>
    public static string Clean(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (char.IsControl(c)) continue;
            sb.Append(c);
            if (sb.Length >= maxLength) break;
        }
        return sb.ToString();
    }
}
