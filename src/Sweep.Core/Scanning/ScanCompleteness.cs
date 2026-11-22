namespace Sweep.Core.Scanning;

/// <summary>
/// 扫描完整性说明：把「哪些内容没被统计、为什么、影响多大」翻译成人话。
///
/// 为什么需要它：扫描必然有覆盖不到的地方（权限、畸形路径、整盘打不开），
/// 但用户看到的「共 12.3 GB」是个确定的数字，很容易被当成"这就是全部"。
/// 只在状态栏给一个「权限拒绝 3」的计数，用户既不知道漏的是什么，也不知道该不该担心。
/// 这里统一产出可展示的条目，且**只列"内容真的没统计到"的情况**——
/// 「明细只保留最大的 N 项」「重复检测按预算收尾」属于清单裁剪而非统计缺失，不混进来。
/// </summary>
public static class ScanCompleteness
{
    /// <summary>
    /// 明确由 Windows 自身机制占用、且**本就不该被清理工具删除**的目录段。
    /// 把它们与「其它 ACL 受限目录」分开说明，用户才知道哪些缺口无需担心。
    /// </summary>
    private static readonly string[] SystemProtectedSegments =
    {
        @"\System Volume Information",   // 系统还原点
        @"\Recovery",                     // WinRE 恢复环境
        @"\Config.Msi",                   // MSI 安装回滚
        @"\$Recycle.Bin",                 // 系统回收站（本工具不处理）
        @"\WindowsApps",                  // 商店应用（TrustedInstaller 所有）
        @"\Windows\WinSxS",               // 组件存储（硬链接）
        @"\WindowsHolographicDevices",
        @"\Windows Master Store"
    };

    public static bool IsSystemProtectedPath(string path)
    {
        foreach (var segment in SystemProtectedSegments)
            if (path.Contains(segment, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>本次扫描是否存在内容未统计到的情况（供 UI 决定是否显示提示条）。</summary>
    public static bool HasGaps(ScanResult result) => Describe(result).Count > 0;

    public static IReadOnlyList<string> Describe(ScanResult result)
    {
        var gaps = new List<string>(4);
        var stats = result.Stats;

        if (result.SkippedRoots is { Count: > 0 } roots)
            gaps.Add(string.Join("、", roots) + "——该目标完全未统计");

        if (stats.Denied > 0)
        {
            var sample = stats.DeniedPaths.Count > 0
                ? "，例如 " + string.Join("、", stats.DeniedPaths.Take(2))
                : "";
            gaps.Add($"{stats.Denied} 个目录无访问权限，其下内容未计入{sample}");
            if (stats.DeniedSystemProtected > 0)
                gaps.Add($"其中 {stats.DeniedSystemProtected} 个是系统保护目录（系统还原、恢复环境、商店应用、回收站等），" +
                         "其所有者是 SYSTEM / TrustedInstaller，本就不应删除，跳过不影响清理价值");
            if (stats.DeniedOther > 0)
                gaps.Add($"其余 {stats.DeniedOther} 个目录受 ACL 限制，以管理员身份运行可读取其中一部分；" +
                         "即使管理员也无法读取 TrustedInstaller 专属目录，不要为此修改系统目录所有权");
            if (stats.DeniedSystemProtected == 0 && stats.DeniedOther == 0)
                gaps.Add("需要完整统计时，请以管理员身份重新扫描");
        }

        if (stats.UnresolvablePaths > 0)
            gaps.Add($"{stats.UnresolvablePaths:N0} 个文件的完整路径无法重建，已排除（不参与分析，也不参与删除）");

        if (stats.AttributeFailures > 0)
            gaps.Add($"{stats.AttributeFailures:N0} 个文件未能校验唯一身份，可能与其他条目重复计入体积");

        return gaps;
    }

    /// <summary>给状态栏用的一句话摘要；无缺口时返回 null。</summary>
    public static string? Summarize(ScanResult result)
    {
        var parts = new List<string>(3);
        var stats = result.Stats;
        if (result.SkippedRoots is { Count: > 0 } roots) parts.Add($"{roots.Count} 个扫描目标不可访问");
        if (stats.Denied > 0) parts.Add($"{stats.Denied} 个目录无权限");
        if (stats.UnresolvablePaths > 0) parts.Add($"{stats.UnresolvablePaths:N0} 项路径无法重建");
        if (stats.AttributeFailures > 0) parts.Add($"{stats.AttributeFailures:N0} 项身份未校验");
        return parts.Count == 0 ? null : string.Join("、", parts);
    }
}
