using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Sweep.Core.Executors;

public sealed record RecycleBinQuota(bool Available, long MaxBytes, long UsedBytes, string Message, bool NukeOnDelete);

/// <summary>SHFileOperation 批量删除 + 回收站配额预检（文档 4.5）。</summary>
public static class Win32Shell
{
    /// <summary>回收站配额预检：配额不足或系统设为「不移入回收站」时必须中止整批，绝不隐式降级为永久删除。</summary>
    public static RecycleBinQuota CheckQuota(string root, long estimatedBytes)
    {
        long used = 0;
        try
        {
            var info = new NativeMethods.ShQueryRbInfo
            {
                cbSize = (uint)Marshal.SizeOf<NativeMethods.ShQueryRbInfo>()
            };
            int hr = NativeMethods.SHQueryRecycleBin(root, ref info);
            if (hr == 0) used = info.i64Size;
        }
        catch { /* 查询失败按"无法确认"处理 */ }

        bool nuke = false;
        long maxBytes = 0;
        try
        {
            using var volumeKey = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket\Volume");
            if (volumeKey is not null)
            {
                foreach (var name in volumeKey.GetSubKeyNames())
                {
                    using var sub = volumeKey.OpenSubKey(name);
                    if (sub is null) continue;
                    if (sub.GetValue("NukeOnDelete") is int n && n != 0) nuke = true;
                    if (sub.GetValue("MaxCapacity") is int mb && mb > 0)
                        maxBytes = Math.Max(maxBytes, (long)mb * 1024 * 1024);
                }
            }
        }
        catch { /* 注册表不可读时退回到卷容量估算 */ }

        if (maxBytes <= 0)
        {
            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(root) ?? root);
                maxBytes = Math.Min(drive.TotalSize / 20, 50L * 1024 * 1024 * 1024); // 默认约 5% 卷容量
            }
            catch { maxBytes = 10L * 1024 * 1024 * 1024; }
        }

        if (nuke)
            return new RecycleBinQuota(false, maxBytes, used,
                "系统已设置为「删除时不移入回收站」。为遵守默认可恢复承诺，已中止整批，请在 Windows 回收站属性中重新启用后重试。", true);

        if (estimatedBytes > 0 && used + estimatedBytes > maxBytes)
            return new RecycleBinQuota(false, maxBytes, used,
                $"回收站配额不足（已用 {PlanItemFormat(used)} / 上限 {PlanItemFormat(maxBytes)}，本批预计 {PlanItemFormat(estimatedBytes)}）。请选择分批删除、先清空回收站或取消，本批不会执行。", false);

        return new RecycleBinQuota(true, maxBytes, used, "回收站配额检查通过", false);
    }

    private static string PlanItemFormat(long bytes) =>
        Sweep.Core.Recommendation.PlanItem.FormatSize(bytes);

    /// <summary>
    /// 一次调用处理整批；allowUndo=false 仅在永久删除显式确认后使用。
    /// SHFileOperation 要求 STA：调用方通常在线程池线程（MTA）上，直接调用在装了
    /// 第三方 shell 扩展的机器上会静默失败甚至死锁，因此在专用 STA 线程上执行。
    /// </summary>
    public static (int HResult, bool Aborted) DeleteBatch(IReadOnlyList<string> paths, bool allowUndo)
    {
        if (paths.Count == 0) return (0, false);
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
            return DeleteBatchCore(paths, allowUndo);

        (int HResult, bool Aborted) result = (0, false);
        var thread = new Thread(() => result = DeleteBatchCore(paths, allowUndo))
        {
            IsBackground = true,
            Name = "Sweep.StaDelete"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return result;
    }

    private static (int HResult, bool Aborted) DeleteBatchCore(IReadOnlyList<string> paths, bool allowUndo)
    {
        var from = string.Join("\0", paths) + "\0\0";
        var op = new NativeMethods.ShFileOpStruct
        {
            hwnd = IntPtr.Zero,
            wFunc = NativeMethods.FO_DELETE,
            pFrom = from,
            pTo = string.Empty,
            fFlags = (ushort)(NativeMethods.FOF_NO_UI | (allowUndo ? NativeMethods.FOF_ALLOWUNDO : 0)),
            fAnyOperationsAborted = 0,
            hNameMappings = IntPtr.Zero,
            lpszProgressTitle = string.Empty
        };
        int rc = NativeMethods.SHFileOperation(ref op);
        return (rc, op.fAnyOperationsAborted != 0);
    }
}

internal static class ShellExtraNative
{
    internal const uint SHERB_NOCONFIRMATION = 0x00000001;
    internal const uint SHERB_NOPROGRESSUI = 0x00000002;
    internal const uint SHERB_NOSOUND = 0x00000004;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHEmptyRecycleBinW")]
    internal static extern int SHEmptyRecycleBin(IntPtr hwnd, string? rootPath, uint flags);
}

/// <summary>Restart Manager：定位占用文件的进程名（文档 4.5 / 7.2）。</summary>
public static class OccupancyDetector
{
    [StructLayout(LayoutKind.Sequential)]
    private struct RmUniqueProcess
    {
        public uint dwProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RmProcessInfo
    {
        public RmUniqueProcess Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strAppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string strServiceShortName;
        public int ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool bRestartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint session, int flags, string sessionKey);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint session, uint nFiles, string[]? files,
        uint nApplications, IntPtr applications, uint nServices, string[]? services);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmGetList(uint session, out uint needed, ref uint count,
        [In, Out] RmProcessInfo[]? info, ref uint rebootReasons);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint session);

    public static string? FindBlockingProcess(string path)
    {
        uint session = 0;
        try
        {
            if (RmStartSession(out session, 0, Guid.NewGuid().ToString("N")) != 0) return null;
            if (RmRegisterResources(session, 1, new[] { path }, 0, IntPtr.Zero, 0, null) != 0) return null;

            uint needed = 0, count = 0, reasons = 0;
            int rc = RmGetList(session, out needed, ref count, null, ref reasons);
            if (needed == 0) return null;
            var buffer = new RmProcessInfo[Math.Min(needed, 32)];
            count = (uint)buffer.Length;
            rc = RmGetList(session, out needed, ref count, buffer, ref reasons);
            if (rc != 0 || count == 0) return null;

            var first = buffer[0];
            if (!string.IsNullOrWhiteSpace(first.strAppName)) return first.strAppName;
            try { return System.Diagnostics.Process.GetProcessById((int)first.Process.dwProcessId).ProcessName + ".exe"; }
            catch { return "PID " + first.Process.dwProcessId; }
        }
        catch { return null; }   // 占用检测失败不影响主流程
        finally { if (session != 0) RmEndSession(session); }
    }
}
