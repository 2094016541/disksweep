using Sweep.Core.Safety;
using Sweep.Core.Scanning;

namespace Sweep.Core.Recommendation;

/// <summary>
/// 空文件 / 空目录：零字节不占空间，价值在于整理目录结构，因此一律 Review + 显式确认，
/// 永不默认勾选、永不进入静默清理；删除仍走通用回收站链路（可从系统回收站还原）。
/// 与规则候选相同，逐项经过 SafetyGate 复核（白名单 / 受保护路径 / 跨用户一律否决）。
/// </summary>
public static class EmptyItemDetector
{
    public static IReadOnlyList<PlanItem> Build(ScanResult scan, SafetyContext ctx)
    {
        if (scan.EmptyCandidates is null || scan.EmptyCandidates.Count == 0)
            return Array.Empty<PlanItem>();

        // SafetyGate → PathNormalizer 对每个空项都要做两次 Win32 打开（长路径转换 +
        // junction 最终目标解析），大盘上近万空目录串行要走 5 秒以上。这些判定全是
        // 只读的纯函数，按索引并行写入互不相交的槽位，最后按原顺序收集。
        var slots = new PlanItem?[scan.EmptyCandidates.Count];
        Parallel.For(0, slots.Length,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 8) * 2 },
            i =>
        {
            var item = scan.EmptyCandidates[i];
            bool isDir = item.IsDirectory && item.Attributes.HasFlag(FileAttributes.Directory);
            string ruleId = isDir ? "empty-directory" : "empty-file";
            string reason = isDir ? "空目录：不含任何子项，删除仅整理目录结构" : "零字节文件：不占用空间，删除仅整理目录结构";
            var rec = new Recommendation(item.Path, RecommendationSource.Rule,
                RecommendationAction.Review, RiskLevel.Low, 1.0, reason, ruleId,
                RequiresExplicitConfirm: true);
            var gated = SafetyGate.Apply(rec, ctx);
            // 闸门否决项（Keep/跨用户/白名单）不进入清单，避免把受保护目录误报为空项
            if (gated.Action == RecommendationAction.Keep) return;

            slots[i] = new PlanItem
            {
                Path = gated.Path,
                Recommendation = gated,
                Category = isDir ? "空目录" : "空文件",
                AllocationSize = 0,
                VolumeSerial = item.VolumeSerial,
                FileId = item.FileId,
                IsDirectory = isDir,
                Handling = "glob-delete",
                RequiresAdmin = false,
                Description = reason,
                ReclaimNote = "删除后先进入回收站，可从系统回收站还原；不释放可测量空间，仅用于整理",
                IsSystemOperation = false,
                LowReclaimEstimate = true,
                AgeDays = item.AgeDays,
                DefaultSelected = false,
                IsSelected = false
            };
        });

        var result = new List<PlanItem>(slots.Length);
        foreach (var slot in slots)
            if (slot is not null) result.Add(slot);
        return result;
    }
}
