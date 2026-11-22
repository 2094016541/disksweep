using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DiskSweep.Core.Safety;

/// <summary>
/// 闸门第 0 步。规范化失败一律返回 null，调用方必须转为 Keep（文档 4.4 / 10.3）。
/// 覆盖：环境变量、设备前缀、. / ..、尾随空格与点、8.3 短名、junction 最终目标、保留设备名。
/// </summary>
public static class PathNormalizer
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1","COM2","COM3","COM4","COM5","COM6","COM7","COM8","COM9",
        "LPT1","LPT2","LPT3","LPT4","LPT5","LPT6","LPT7","LPT8","LPT9"
    };

    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();
        s = Environment.ExpandEnvironmentVariables(s);

        if (s.StartsWith(@"\\?\", StringComparison.Ordinal) || s.StartsWith(@"\\.\", StringComparison.Ordinal))
            s = s[4..];
        // 远程卷 / UNC 一律拒绝，避免跨主机删除
        if (s.StartsWith(@"\\", StringComparison.Ordinal)) return null;

        s = s.Replace('/', '\\');
        s = TrimSegments(s);
        if (s.Length == 0) return null;

        string full;
        try { full = Path.GetFullPath(s); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        { return null; }

        if (HasReservedDeviceName(full)) return null;

        full = ToLongPath(full);
        full = StripDevicePrefix(full);
        full = TrimSegments(full);

        // 解析 junction / mount point / 符号链接的最终目标；失败即 Keep
        var final = ResolveFinalTarget(full);
        if (final is null) return null;
        return final;
    }

    /// <summary>供白名单/规则模式使用：只做词法规范化，不要求路径存在。</summary>
    public static string NormalizePattern(string pattern)
    {
        var s = Environment.ExpandEnvironmentVariables(pattern.Trim()).Replace('/', '\\');
        if (s.StartsWith(@"\\?\", StringComparison.Ordinal) || s.StartsWith(@"\\.\", StringComparison.Ordinal))
            s = s[4..];
        if (s.Length >= 2 && s[1] == ':') s = char.ToUpperInvariant(s[0]) + s[1..];
        return TrimSegments(s);
    }

    private static string TrimSegments(string path)
    {
        var parts = path.Split('\\');
        for (int i = 0; i < parts.Length; i++)
        {
            if (i == 0 && parts[i].Length == 2 && parts[i][1] == ':') continue;
            if (parts[i] == "." ) { parts[i] = ""; continue; }
            parts[i] = parts[i].TrimEnd(' ', '.');
        }
        var kept = parts.Where((p, i) => i == 0 || p.Length > 0).ToArray();
        return string.Join('\\', kept);
    }

    private static bool HasReservedDeviceName(string fullPath)
    {
        foreach (var segment in fullPath.Split('\\'))
        {
            var name = segment.Split('.')[0];
            if (ReservedNames.Contains(name)) return true;
        }
        return false;
    }

    private static string ToLongPath(string path)
    {
        try
        {
            var buffer = new StringBuilder(1024);
            uint len = NativeMethods.GetLongPathName(path, buffer, (uint)buffer.Capacity);
            if (len == 0) return path;
            if (len > buffer.Capacity)
            {
                buffer.EnsureCapacity((int)len + 1);
                len = NativeMethods.GetLongPathName(path, buffer, (uint)buffer.Capacity);
                if (len == 0) return path;
            }
            return buffer.ToString();
        }
        catch { return path; }
    }

    private static string? ResolveFinalTarget(string fullPath)
    {
        SafeFileHandle? handle = null;
        try
        {
            handle = NativeMethods.CreateFile(fullPath, NativeMethods.FILE_READ_ATTRIBUTES,
                NativeMethods.FILE_SHARE_ALL, IntPtr.Zero, NativeMethods.OPEN_EXISTING,
                NativeMethods.FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
            if (handle.IsInvalid) return null;

            var sb = new StringBuilder(1024);
            uint len = NativeMethods.GetFinalPathNameByHandle(handle, sb, (uint)sb.Capacity,
                NativeMethods.FILE_NAME_NORMALIZED | NativeMethods.VOLUME_NAME_DOS);
            if (len == 0) return null;
            if (len > sb.Capacity)
            {
                sb.EnsureCapacity((int)len + 1);
                len = NativeMethods.GetFinalPathNameByHandle(handle, sb, (uint)sb.Capacity,
                    NativeMethods.FILE_NAME_NORMALIZED | NativeMethods.VOLUME_NAME_DOS);
                if (len == 0) return null;
            }
            var result = StripDevicePrefix(sb.ToString());
            if (result.StartsWith(@"\\", StringComparison.Ordinal)) return null;
            return TrimSegments(result);
        }
        catch { return null; }
        finally { handle?.Dispose(); }
    }

    private static string StripDevicePrefix(string s) =>
        s.StartsWith(@"\\?\", StringComparison.Ordinal) || s.StartsWith(@"\\.\", StringComparison.Ordinal)
            ? s[4..] : s;
}
