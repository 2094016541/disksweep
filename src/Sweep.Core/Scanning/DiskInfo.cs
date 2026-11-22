namespace Sweep.Core.Scanning;

/// <summary>GetDiskFreeSpaceEx 包装与已就绪卷枚举（文档 7.1）。</summary>
public static class DiskInfo
{
    public static DiskUsage Get(string root)
    {
        try
        {
            if (NativeMethods.GetDiskFreeSpaceEx(root, out ulong free, out ulong total, out _))
                return new DiskUsage(root, total, free);
        }
        catch { /* 回退到 DriveInfo */ }
        try
        {
            var drive = new DriveInfo(root);
            return new DiskUsage(root, (ulong)drive.TotalSize, (ulong)drive.AvailableFreeSpace);
        }
        catch { return new DiskUsage(root, 0, 0); }
    }

    /// <summary>
    /// 枚举所有就绪卷。未插盘光驱、断开的网络映射、拒绝访问的卷会被跳过，
    /// 单个卷失败不得影响其它卷；容量仍走 Get 的 GetDiskFreeSpaceEx → DriveInfo 双通道。
    /// </summary>
    public static IReadOnlyList<DriveDescriptor> GetAll()
    {
        DriveInfo[] drives;
        try { drives = DriveInfo.GetDrives(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Array.Empty<DriveDescriptor>();
        }

        var result = new List<DriveDescriptor>();
        foreach (var drive in drives)
        {
            string label;
            string type;
            try
            {
                if (!drive.IsReady) continue;
                type = Describe(drive.DriveType);
                label = drive.VolumeLabel;
            }
            catch
            {
                // 卷在枚举过程中被拔出 / 权限变化：跳过这一项，不影响其它卷。
                continue;
            }

            var usage = Get(drive.Name);
            result.Add(new DriveDescriptor(
                drive.Name,
                string.IsNullOrWhiteSpace(label) ? type : label.Trim(),
                type,
                usage.TotalBytes,
                usage.FreeBytes));
        }
        return result.OrderBy(d => d.Root, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string Describe(DriveType type) => type switch
    {
        DriveType.Fixed => "本地磁盘",
        DriveType.Removable => "可移动磁盘",
        DriveType.Network => "网络驱动器",
        DriveType.CDRom => "光驱",
        DriveType.Ram => "内存盘",
        _ => "其他"
    };
}
