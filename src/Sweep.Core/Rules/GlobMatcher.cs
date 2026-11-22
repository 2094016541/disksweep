using System.Text;
using System.Text.RegularExpressions;

namespace Sweep.Core.Rules;

/// <summary>规则包与白名单共用语法：** 递归、* 单层、%VAR% 展开、结尾 \ 表示目录。</summary>
public sealed class GlobMatcher
{
    private readonly Regex _regex;
    /// <summary>
    /// 模式恰好是「字面量目录 + \**」时（内置规则里最常见的形态），记下该目录前缀。
    /// 这类匹配等价于一次带分隔符边界的 StartsWith，不必进正则——扫描热路径上省掉最贵的一步。
    /// </summary>
    private readonly string? _literalDirectory;
    /// <summary>
    /// 模式里被通配符切出来的字面量片段。任何匹配路径都必须按顺序包含这些片段，
    /// 因此先用 IndexOf 做一次廉价预筛，绝大多数对象根本不必进正则
    /// （扫描时每个对象都要跟几十条规则比对，正则是这条热路径上最贵的一步）。
    /// </summary>
    private readonly string[] _fragments;
    /// <summary>模式不以通配符开头时首个片段必须落在路径开头（正则是 ^ 锚定的）。</summary>
    private readonly bool _anchored;

    public string Pattern { get; }

    private GlobMatcher(string pattern, Regex regex, string[] fragments, bool anchored, string? literalDirectory)
    {
        Pattern = pattern;
        _regex = regex;
        _fragments = fragments;
        _anchored = anchored;
        _literalDirectory = literalDirectory;
    }

    public bool Matches(string normalizedPath)
    {
        if (string.IsNullOrEmpty(normalizedPath)) return false;
        var path = normalizedPath.TrimEnd('\\');

        // 最常见形态的快速通道：`<目录>\**` 只匹配该目录之下的对象（不含目录自身）。
        if (_literalDirectory is { } dir)
            return path.Length > dir.Length &&
                   path[dir.Length] == '\\' &&
                   path.AsSpan(0, dir.Length).Equals(dir, StringComparison.OrdinalIgnoreCase);

        if (_fragments.Length > 0)
        {
            int searchFrom = 0;
            for (int i = 0; i < _fragments.Length; i++)
            {
                var fragment = _fragments[i];
                if (i == 0 && _anchored)
                {
                    if (!path.StartsWith(fragment, StringComparison.OrdinalIgnoreCase)) return false;
                }
                else
                {
                    int at = path.IndexOf(fragment, searchFrom, StringComparison.OrdinalIgnoreCase);
                    if (at < 0) return false;
                    searchFrom = at + fragment.Length;
                }
            }
        }
        return _regex.IsMatch(path);
    }

    public static string ExpandEnvironment(string pattern)
    {
        return Regex.Replace(pattern, "%([^%]+)%", m =>
            Environment.GetEnvironmentVariable(m.Groups[1].Value) ?? m.Value,
            RegexOptions.None, TimeSpan.FromSeconds(1));
    }

    public static bool TryCreate(string pattern, out GlobMatcher? matcher, out string? error)
    {
        matcher = null;
        error = null;
        if (string.IsNullOrWhiteSpace(pattern))
        {
            error = "模式为空";
            return false;
        }

        var expanded = ExpandEnvironment(pattern).Replace('/', '\\');
        if (expanded.Contains('%'))
        {
            error = "环境变量未闭合";
            return false;
        }
        if (expanded.Contains("***"))
        {
            error = "不允许连续的 *** 通配符";
            return false;
        }
        if (expanded.StartsWith(@"\\", StringComparison.Ordinal))
        {
            error = "不支持 UNC / 远程卷模式";
            return false;
        }
        if (expanded.Length >= 2 && char.IsLetter(expanded[0]) && expanded[1] == ':')
            expanded = char.ToUpperInvariant(expanded[0]) + expanded[1..];
        else if (!expanded.StartsWith(@"\", StringComparison.Ordinal) && !expanded.Contains(':'))
        {
            // 允许相对模式，但一律按完整路径匹配，避免误命中
            error = null;
        }

        try
        {
            var regex = BuildRegex(expanded);
            var fragments = BuildFragments(expanded, out bool anchored);
            matcher = new GlobMatcher(pattern, regex, fragments, anchored, LiteralDirectoryOf(expanded));
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 按通配符切出字面量片段，作为「必然包含」的廉价预筛条件。
    /// 片段两端的 \ 会去掉：条件更弱但依然必要，能匹配更多才安全。
    /// </summary>
    private static string[] BuildFragments(string expanded, out bool anchored)
    {
        anchored = expanded.Length > 0 && expanded[0] != '*' && expanded[0] != '?';
        var fragments = new List<string>(2);
        int start = 0;
        for (int i = 0; i <= expanded.Length; i++)
        {
            bool boundary = i == expanded.Length || expanded[i] == '*' || expanded[i] == '?';
            if (!boundary) continue;
            var piece = expanded[start..i].Trim('\\');
            if (piece.Length >= 2) fragments.Add(piece);
            start = i + 1;
        }
        return fragments.ToArray();
    }

    /// <summary>
    /// 识别「纯字面量目录 + \**」形态：除结尾递归通配外没有任何通配符。
    /// 返回去掉结尾 \** 的目录字面量，供 Matches 走前缀比较快通道。
    /// </summary>
    private static string? LiteralDirectoryOf(string expanded)
    {
        if (!expanded.EndsWith(@"\**", StringComparison.Ordinal)) return null;
        var head = expanded[..^3];
        if (head.Length == 0) return null;
        return head.AsSpan().IndexOfAny('*', '?') < 0 ? head : null;
    }

    private static Regex BuildRegex(string pattern)
    {
        // 结尾语义必须带分隔符边界：`%TEMP%\**` 只能命中 Temp 之下的对象，
        // 不能把同前缀的 `...\Temperature\x` 也算进来（否则会误删/误报）。
        string working;
        string tail;
        if (pattern.EndsWith(@"\**", StringComparison.Ordinal))
        {
            working = pattern[..^3];
            tail = @"\\.*$";                      // 必须再跟一个分隔符：只匹配目录内容
        }
        else if (pattern.EndsWith(@"\", StringComparison.Ordinal))
        {
            working = pattern.TrimEnd('\\');
            tail = @"(?:\\.*)?$";                 // 结尾 \ 表示「目录本身及其内容」
        }
        else
        {
            working = pattern;
            tail = @"(?:\\.*)?$";
        }

        var sb = new StringBuilder("^");
        for (int i = 0; i < working.Length; i++)
        {
            char c = working[i];
            if (c == '*')
            {
                bool isDouble = i + 1 < working.Length && working[i + 1] == '*';
                if (isDouble)
                {
                    i++;
                    if (i + 1 < working.Length && working[i + 1] == '\\')
                    {
                        i++;
                        sb.Append("(?:[^\\\\]+\\\\)*");
                    }
                    else sb.Append(".*");
                }
                else sb.Append("[^\\\\]*");
            }
            else if (c == '?') sb.Append("[^\\\\]");
            else sb.Append(Regex.Escape(c.ToString()));
        }
        sb.Append(tail);
        return new Regex(sb.ToString(),
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
            TimeSpan.FromSeconds(1));
    }
}
