using System.Security.Cryptography;
using System.Text;
using DiskSweep.Ai.Abstractions;

namespace DiskSweep.Core.Recommendation;

/// <summary>
/// 6.7 三层脱敏：路径模板化、文件名抹除、数值分桶。
/// 本类只做本地字符串/哈希处理，不含任何网络或厂商概念；盐由主程序从 secrets.dat 提供且不出本机。
/// </summary>
public static class Redactor
{
    private static readonly string[] KnownSegments =
    {
        "users", "appdata", "local", "roaming", "temp", "cache", "downloads", "documents",
        "desktop", "programdata", "windows", "software", "microsoft", "program files",
        "logs", "data", "bin", "obj", "packages", "backup", "tmp", "public"
    };

    /// <summary>路径片段的本地假名化（HMAC-SHA256 前 4 字节），不含任何厂商/凭据概念。</summary>
    public static string Pseudonymize(string pathSegment, string salt)
    {
        var key = Encoding.UTF8.GetBytes(salt);
        using var hmac = new HMACSHA256(key);
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(pathSegment.ToLowerInvariant()));
        return Convert.ToHexString(hash.AsSpan(0, 4)).ToLowerInvariant();
    }

    /// <summary>C:\Users\alice\AppData\Local\Foo\cache → &lt;USER&gt;/AppData/Local/&lt;DIR:9a11&gt;/cache</summary>
    public static string Template(string fullPath, string userProfile, string salt)
    {
        var p = fullPath.Replace('/', '\\');
        var segments = p.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var parts = new List<string>();
        bool skippedDrive = false;
        bool skippedUser = false;
        var userLeaf = userProfile.TrimEnd('\\').Split('\\').LastOrDefault() ?? "";

        foreach (var seg in segments)
        {
            if (!skippedDrive && seg.Length == 2 && seg[1] == ':') { skippedDrive = true; continue; }
            if (!skippedUser && string.Equals(seg, "Users", StringComparison.OrdinalIgnoreCase)) { skippedUser = true; continue; }
            if (!skippedDrive && string.Equals(seg, userLeaf, StringComparison.OrdinalIgnoreCase)) { continue; }
            if (skippedUser && !string.IsNullOrEmpty(userLeaf) && string.Equals(seg, userLeaf, StringComparison.OrdinalIgnoreCase))
            { parts.Add("<USER>"); continue; }
            if (KnownSegments.Contains(seg, StringComparer.OrdinalIgnoreCase))
            { parts.Add(seg); continue; }
            parts.Add($"<DIR:{Pseudonymize(seg, salt)}>");
        }
        return string.Join('/', parts);
    }

    /// <summary>数值按 log10 分桶，不暴露真实字节数。</summary>
    public static long BucketSize(long bytes)
    {
        if (bytes <= 0) return 0;
        var magnitude = (int)Math.Floor(Math.Log10(bytes));
        return (long)Math.Pow(10, Math.Clamp(magnitude, 0, 12));
    }

    /// <summary>时间按天/周/月/年分桶。</summary>
    public static int BucketAge(int days) => days switch
    {
        <= 7 => 7,
        <= 30 => 30,
        <= 90 => 90,
        <= 365 => 365,
        _ => 1000
    };

    public static CandidateDigest ToDigest(string referenceId, string path, string category, string extension,
        long sizeBytes, int ageDays, bool hasExecutable, bool insideWhitelist,
        string userProfile, string salt, bool privacyMode)
    {
        return new CandidateDigest(
            referenceId,
            Template(Path.GetDirectoryName(path) ?? path, userProfile, salt),
            extension,
            BucketSize(sizeBytes),
            BucketAge(ageDays),
            1,                     // 单条摘要恒为 1，聚合在网关完成
            hasExecutable,
            insideWhitelist,
            privacyMode ? null : Template(path, userProfile, salt));
    }

    public static string SanitizeForLog(string path, string userProfile, string salt) =>
        Template(path, userProfile, salt);
}
