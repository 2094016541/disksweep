using System.Text.RegularExpressions;

namespace Sweep.Core.Software;

/// <summary>
/// 面向「显示与排序」的宽松版本比较器：能处理 1.2 / 1.2.3.4 / 2026.3 / 8.0 Beta 10 等常见形态。
/// 只用于标注哪个版本更新、把版本列表从新到旧排列；真正的安装可行性由 winget 自行判定。
/// 无法解析的文本按字符串比较，绝不抛异常。
/// </summary>
public sealed class SoftwareVersion : IComparable<SoftwareVersion>
{
    private static readonly Regex NumericCore =
        new(@"^v?(\d+(?:\.\d+)*)(.*)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly long[] _numbers;
    private readonly string _suffix;
    private readonly bool _hasNumeric;

    public string Raw { get; }

    private SoftwareVersion(string raw, long[] numbers, string suffix, bool hasNumeric)
    {
        Raw = raw;
        _numbers = numbers;
        _suffix = suffix;
        _hasNumeric = hasNumeric;
    }

    public static SoftwareVersion Parse(string? raw)
    {
        string text = (raw ?? "").Trim();
        var match = NumericCore.Match(text);
        if (!match.Success)
            return new SoftwareVersion(text, Array.Empty<long>(), text, false);

        var parts = match.Groups[1].Value.Split('.');
        var numbers = new long[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            numbers[i] = long.TryParse(parts[i], out long value) ? value : 0L;

        // 稳定版（无后缀）应排在 beta / rc 之后；后缀只做确定性排序，不猜测其语义。
        string suffix = match.Groups[2].Value.Trim().Trim('-', '_', ' ', '+');
        return new SoftwareVersion(text, numbers, suffix, true);
    }

    public int CompareTo(SoftwareVersion? other)
    {
        if (other is null) return 1;
        if (_hasNumeric != other._hasNumeric) return _hasNumeric ? 1 : -1;

        if (_hasNumeric)
        {
            int length = Math.Max(_numbers.Length, other._numbers.Length);
            for (int i = 0; i < length; i++)
            {
                long left = i < _numbers.Length ? _numbers[i] : 0;
                long right = i < other._numbers.Length ? other._numbers[i] : 0;
                if (left != right) return left > right ? 1 : -1;
            }
            // 数字部分相同：无后缀的稳定版 > 带后缀的预发布版。
            bool leftStable = _suffix.Length == 0;
            bool rightStable = other._suffix.Length == 0;
            if (leftStable != rightStable) return leftStable ? 1 : -1;
            if (!leftStable)
            {
                int bySuffix = string.Compare(_suffix, other._suffix, StringComparison.OrdinalIgnoreCase);
                if (bySuffix != 0) return bySuffix;
            }
            return 0;
        }

        return string.Compare(Raw, other.Raw, StringComparison.OrdinalIgnoreCase);
    }

    public static int Compare(string? left, string? right) => Parse(left).CompareTo(Parse(right));
    public static bool IsNewer(string candidate, string current) => Compare(candidate, current) > 0;

    /// <summary>版本列表展示用：去重后从新到旧排列。</summary>
    public static IReadOnlyList<string> SortDescending(IEnumerable<string> versions)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unique = new List<string>();
        foreach (string version in versions)
        {
            string text = (version ?? "").Trim();
            if (text.Length == 0 || !seen.Add(text)) continue;
            unique.Add(text);
        }
        return unique
            .OrderByDescending(v => Parse(v))
            .ThenBy(v => v, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>相对当前版本的语义标注；当前版本未登记时不给结论。</summary>
    public static string DescribeRelation(string candidate, string? current)
    {
        string now = (current ?? "").Trim();
        if (now.Length == 0 || now is "—" or "-" or "Unknown")
            return "可安装版本";
        int result = Compare(candidate, now);
        return result > 0 ? "更新版本" : result == 0 ? "当前版本" : "旧版本";
    }
}
