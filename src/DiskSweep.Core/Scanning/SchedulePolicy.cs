namespace DiskSweep.Core.Scanning;

/// <summary>
/// 文档 4.7：静默清理只在磁盘剩余空间比例低于阈值时触发。
/// 容量读取失败（total=0）一律按「不触发」处理，避免误清理。
/// </summary>
public static class SchedulePolicy
{
    public static bool ShouldRun(DiskUsage usage, int minFreePercent)
    {
        if (usage.TotalBytes == 0 || usage.FreeBytes > usage.TotalBytes) return false;
        double freePercent = usage.FreeBytes * 100d / usage.TotalBytes;
        return freePercent < Math.Clamp(minFreePercent, 0, 100);
    }
}
