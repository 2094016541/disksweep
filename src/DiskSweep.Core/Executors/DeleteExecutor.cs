using DiskSweep.Core.Recommendation;
using DiskSweep.Core.Safety;
using DiskSweep.Core.Scanning;
using Rec = DiskSweep.Core.Recommendation.Recommendation;

namespace DiskSweep.Core.Executors;

public sealed record DeleteOptions(bool Permanent, string? ConfirmWord, bool ExplicitlyConfirmed, string RootForRecycleBin)
{
    public static DeleteOptions Recycle(string root) => new(false, null, true, root);
}

/// <summary>批量删除的进度（已完成项数 / 总数）；按批上报，供界面显示「正在清理 128/512 项」。</summary>
public sealed record DeleteProgress(int Done, int Total);

/// <summary>
/// 唯一删除出口。顺序：闸门复核 → TOCTOU 主进程侧校验 → 回收站配额预检 →
/// 分批删除（FOF_ALLOWUNDO 不可降级）→ 逐项核验 → 结果回传。
/// 本类不写操作日志、不保存撤销记录：删除仍进 Windows 回收站，用户可在系统层面手动还原。
/// </summary>
public sealed class DeleteExecutor
{
    public const string PermanentConfirmWord = "永久删除";

    /// <summary>
    /// 单批交给 Shell 的路径数。原来一次性把上千条路径塞给 SHFileOperation：
    /// 中途任何一条失败都会让整批停下来，用户只能看到"失败了"，其余项还得手工重试。
    /// 分批后某批失败只影响该批，且能按批上报进度。
    /// </summary>
    private const int DeleteChunkSize = 64;

    /// <summary>占用探测（Restart Manager）每次要建会话，失败项多时逐个探测本身就是新的卡顿。</summary>
    private const int MaxOccupancyProbes = 5;

    private readonly SafetyContext _ctx;
    private readonly Func<string, long, RecycleBinQuota> _quota;
    private readonly Func<IReadOnlyList<string>, bool, (int Hr, bool Aborted)> _deleteBatch;

    public DeleteExecutor(SafetyContext ctx,
        Func<string, long, RecycleBinQuota>? quotaChecker = null,
        Func<IReadOnlyList<string>, bool, (int Hr, bool Aborted)>? deleteBatch = null)
    {
        _ctx = ctx;
        _quota = quotaChecker ?? Win32Shell.CheckQuota;
        _deleteBatch = deleteBatch ?? ((paths, allowUndo) => Win32Shell.DeleteBatch(paths, allowUndo));
    }

    public DeleteOutcome Execute(IReadOnlyList<PlanItem> selected, string? scanId, DeleteOptions options,
        IProgress<DeleteProgress>? progress = null)
    {
        var sessionId = Guid.NewGuid().ToString("N");
        var failures = new List<DeleteFailure>();
        if (selected.Count == 0)
            return new DeleteOutcome(false, sessionId, 0, 0, 0, "没有选中任何可清理项", failures);

        if (options.Permanent)
        {
            if (!string.Equals(options.ConfirmWord, PermanentConfirmWord, StringComparison.Ordinal))
                return new DeleteOutcome(false, sessionId, 0, 0, 0,
                    $"永久删除需要显式勾选并在预览中输入确认词「{PermanentConfirmWord}」；本次未执行任何删除。", failures);
            if (selected.Any(p => p.IsSystemOperation))
                return new DeleteOutcome(false, sessionId, 0, 0, 0, "系统维护项不支持永久删除模式。", failures);
            if (selected.Any(p => p.Source == RecommendationSource.Ai))
                return new DeleteOutcome(false, sessionId, 0, 0, 0, "永久删除模式下 AI 建议一律禁用，请取消这些选项。", failures);
        }

        var accepted = new List<(PlanItem Item, string NormalizedPath)>();
        foreach (var item in selected)
        {
            if (item.IsSystemOperation)
            {
                failures.Add(new DeleteFailure(item.Path, 0, "系统维护项需走系统接口入口，已跳过", null));
                continue;
            }
            var rec = new Rec(item.Path, item.Source, item.Action, item.Risk,
                item.Recommendation.Confidence, item.Recommendation.Reason,
                item.Recommendation.RuleId, item.RequiresExplicitConfirm);
            var gated = SafetyGate.Apply(rec, _ctx);
            if (gated.Action == RecommendationAction.Keep || gated.Risk == RiskLevel.Critical)
            {
                failures.Add(new DeleteFailure(item.Path, 0, "安全闸门否决：" + gated.Reason, null));
                continue;
            }
            if ((gated.Action == RecommendationAction.Review || gated.RequiresExplicitConfirm) && !options.ExplicitlyConfirmed)
            {
                failures.Add(new DeleteFailure(item.Path, 0, "需人工复核/显式确认，未进入执行清单", null));
                continue;
            }
            if (!ProtectedPaths.Owns(gated.Path, _ctx.CurrentUserProfile))
            {
                failures.Add(new DeleteFailure(item.Path, 0, "执行器二次拒绝：属于其他用户目录", null));
                continue;
            }
            // TOCTOU：主进程侧校验 (volume, fileId) 与扫描快照一致
            if (item.FileId != 0)
            {
                var identity = ScanEngine.GetIdentity(item.Path);
                if (identity is null || identity.VolumeSerial != item.VolumeSerial || identity.FileId != item.FileId)
                {
                    failures.Add(new DeleteFailure(item.Path, 0, "TOCTOU 校验未通过：对象已被替换或无法确认", null));
                    continue;
                }
            }
            accepted.Add((item, gated.Path));
        }

        if (accepted.Count == 0)
            return new DeleteOutcome(false, sessionId, 0, 0, 0, "所有候选项均被安全闸门或一致性校验拦下，未执行任何删除。", failures);

        // 嵌套折叠：父子同时入选时只把最外层交给 Shell。否则先删父再删子必然报"不存在"，
        // 反而把明明已经删掉的项记成失败（空目录链就是典型场景）。
        var acceptedPaths = new HashSet<string>(accepted.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in accepted) acceptedPaths.Add(entry.NormalizedPath);
        var targets = new List<(PlanItem Item, string Path)>(accepted.Count);
        foreach (var entry in accepted)
        {
            if (!HasAncestorIn(entry.NormalizedPath, acceptedPaths)) targets.Add(entry);
        }

        long estimated = accepted.Sum(a => a.Item.AllocationSize);

        // 按卷分组：多盘（"所有磁盘"模式）下配额必须逐卷预检，否则非首个卷会绕过检查。
        var byVolume = new Dictionary<string, List<(PlanItem Item, string NormalizedPath)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in accepted)
        {
            var volume = VolumeOf(entry.NormalizedPath, options.RootForRecycleBin);
            if (!byVolume.TryGetValue(volume, out var list)) byVolume[volume] = list = new List<(PlanItem, string)>();
            list.Add(entry);
        }

        // 8.1 红线 6：配额不足 / 系统设为 NukeOnDelete 时中止整批，零永久删除
        if (!options.Permanent)
        {
            foreach (var pair in byVolume)
            {
                long volumeBytes = pair.Value.Sum(a => a.Item.AllocationSize);
                var quota = _quota(pair.Key, volumeBytes);
                if (quota.Available) continue;
                return new DeleteOutcome(false, sessionId, 0, 0, 0, quota.Message, failures);
            }
        }

        int firstHr = 0;
        bool anyAborted = false;
        for (int offset = 0; offset < targets.Count; offset += DeleteChunkSize)
        {
            int take = Math.Min(DeleteChunkSize, targets.Count - offset);
            var chunk = new string[take];
            for (int i = 0; i < take; i++) chunk[i] = targets[offset + i].Path;
            var (hr, aborted) = _deleteBatch(chunk, !options.Permanent);
            if (hr != 0 && firstHr == 0) firstHr = hr;
            anyAborted |= aborted;
            progress?.Report(new DeleteProgress(offset + take, targets.Count));
        }

        var succeeded = new List<(PlanItem Item, string Path)>();
        var failedPaths = new List<string>();
        long reclaimed = 0;
        int probes = 0;
        foreach (var (item, path) in accepted)
        {
            bool gone = !File.Exists(path) && !Directory.Exists(path);
            if (gone)
            {
                succeeded.Add((item, path));
                reclaimed += item.AllocationSize;
            }
            else
            {
                // 占用探测只对前若干项做：Restart Manager 每次都要建会话，几百个失败项
                // 逐个探测会让"清理"在最后阶段再卡十几秒，而用户只需要知道有东西占着。
                string? blocker = probes < MaxOccupancyProbes
                    ? OccupancyDetector.FindBlockingProcess(path)
                    : null;
                probes++;
                failures.Add(new DeleteFailure(item.Path, firstHr,
                    blocker is null ? "文件仍存在（可能被占用或权限不足）" : $"被 {blocker} 占用", blocker));
                failedPaths.Add(item.Path);
            }
        }

        string sizeText = PlanItem.FormatSize(reclaimed);
        string message;
        if (firstHr != 0 || anyAborted)
            message = $"批次未完全成功（HRESULT 0x{firstHr:X8}），已跳过失败批次继续处理其余项；未去掉 FOF_ALLOWUNDO 重试，也未转为永久删除。" +
                      $"成功 {succeeded.Count} 项 / 失败 {failedPaths.Count} 项，实际释放 {sizeText}。";
        else
            message = options.Permanent
                ? $"永久删除完成：成功 {succeeded.Count} 项 / 失败 {failedPaths.Count} 项，实际释放 {sizeText}。此模式不可恢复。"
                : $"已移入回收站：成功 {succeeded.Count} 项 / 失败 {failedPaths.Count} 项，实际释放 {sizeText}，可从 Windows 回收站手动还原。";

        // 回传成功项的原始路径（item.Path 与扫描清单一致），供 UI 精确移除已删项；
        // 失败/被闸门拦下的路径不在其中，仍留在清单里供用户重试。
        var succeededPaths = succeeded.Select(s => s.Item.Path).ToArray();
        return new DeleteOutcome(true, sessionId, succeeded.Count, failedPaths.Count, reclaimed, message,
            failures, succeededPaths);
    }

    private static string VolumeOf(string path, string fallback)
    {
        try { return Path.GetPathRoot(Path.GetFullPath(path)) ?? fallback; }
        catch { return fallback; }
    }

    /// <summary>路径的某一级祖先是否也在待删集合里（只比到盘根，避免把 `C:` 当成祖先）。</summary>
    private static bool HasAncestorIn(string path, HashSet<string> candidates)
    {
        int index = path.LastIndexOf('\\');
        while (index > 2)
        {
            var parent = path[..index];
            if (candidates.Contains(parent)) return true;
            index = parent.LastIndexOf('\\');
        }
        return false;
    }

    public async Task<(bool Ok, string Message)> ExecuteSystemOperationAsync(
        PlanItem operation, string? scanId, bool acknowledged, CancellationToken ct = default)
    {
        if (!operation.IsSystemOperation || !operation.RequiresExplicitConfirm || !acknowledged)
            return (false, "系统维护项必须逐项阅读后果并显式确认后才会执行");
        if (operation.Source == RecommendationSource.Ai)
            return (false, "AI 建议不得触发系统维护操作");

        var result = await SystemCleanup.ExecuteAsync(operation.Handling, _ctx.SystemDrive, ct).ConfigureAwait(false);
        return (result.Ok, result.Message);
    }
}
