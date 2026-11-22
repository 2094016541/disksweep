using System.Text.RegularExpressions;

namespace Sweep.Core.Software;

/// <summary>winget 配置的一个软件源；Name 用于 --source 参数，Url 只用于展示。</summary>
public sealed record WingetSource(string Name, string Url, bool IsExplicit);

/// <summary>来自某个源的一个可用版本；同一版本若在多个源都有，各自保留为独立可选项。</summary>
public sealed record AvailableVersion(string Version, string Source);

/// <summary>
/// winget 多源的纯解析与合并逻辑。不启动进程、不访问注册表，可以在无 GUI 环境下单测。
/// 所有进入命令行的源名都经过白名单校验，避免把 winget 源列表文本直接拼进参数。
/// </summary>
public static class WingetSources
{
    private static readonly Regex SourceNamePattern =
        new(@"^[A-Za-z][A-Za-z0-9._-]{0,63}$", RegexOptions.Compiled);
    /// <summary>winget 源表列宽不齐：winget-font 后面只有一个空格，不能按「2+ 空格」分列。</summary>
    private static readonly Regex SourceRow = new(
        @"^(?<name>[A-Za-z][A-Za-z0-9._-]{0,63})\s+(?<url>https?://\S+)(?:\s+(?<explicit>true|false))?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>winget 源名白名单：字母开头，只允许字母数字和 . _ -。</summary>
    public static bool IsValidName(string? name) =>
        name is not null && SourceNamePattern.IsMatch(name.Trim());

    /// <summary>自定义源地址：必须是 http/https，且不含空白（会被拼进命令行）。</summary>
    public static bool IsValidUrl(string? url)
    {
        string text = (url ?? "").Trim();
        if (text.Length is 0 or > 300 || text.Any(char.IsWhiteSpace)) return false;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return false;
        return (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
               && !string.IsNullOrWhiteSpace(uri.Host);
    }

    /// <summary>
    /// 解析 winget source list 的中英文表格。表头、分隔线与异常行直接跳过，
    /// 源名不合法时同样跳过——这些文本永远不会被用作 --source 参数。
    /// </summary>
    public static IReadOnlyList<WingetSource> ParseList(string? output)
    {
        var sources = new List<WingetSource>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in (output ?? "").Replace('\r', '\n').Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.All(c => c is '-' or '=' or ' ' or '\t')) continue;

            // 源参数列一定是 URL；这同时挡住中英文表头与任何异常行。
            var match = SourceRow.Match(line);
            if (!match.Success) continue;

            string name = match.Groups["name"].Value;
            if (!IsValidName(name) || !seen.Add(name)) continue;

            bool isExplicit = bool.TryParse(match.Groups["explicit"].Value, out bool flag) && flag;
            sources.Add(new WingetSource(name, match.Groups["url"].Value, isExplicit));
        }
        return sources;
    }

    /// <summary>
    /// 把 winget 当前真正可用的源整理成下拉列表：第一项是「所有软件源」（并发扫描已启用的源，
    /// 再按版本合并），其后依次是已启用的国内镜像、官方源、商店源，最后是用户自己加的源。
    ///
    /// 未启用的预置源<b>不进这个列表</b>：它们是「可以加，但还没加」的东西，选它只会让 winget 报错。
    /// 想让用户知道有更快的源可用，用 <see cref="WingetSourceCatalog.FirstUnconfiguredDomestic"/>
    /// 单独给一个「添加镜像」的动作，而不是在下拉里摆一堆「未启用」。
    ///
    /// statusKnown=false 表示 winget 源清单没读出来：这时只留「所有软件源」，
    /// 由界面说明「源状态未知」，绝不把预置源伪装成可选项。
    /// </summary>
    public static IReadOnlyList<SourceChoice> BuildChoices(
        IEnumerable<WingetSource>? configuredSources,
        IEnumerable<WingetSourcePreset>? presets,
        bool statusKnown = true)
    {
        var configured = new Dictionary<string, WingetSource>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in configuredSources ?? Array.Empty<WingetSource>())
            if (IsValidName(source.Name)) configured[source.Name.Trim()] = source;

        var allPresets = (presets ?? WingetSourceCatalog.Presets).ToArray();
        var choices = new List<SourceChoice>
        {
            new("", "所有软件源", "", "并行扫描 winget 里所有已启用的源，再按版本合并",
                true, true, false, false)
        };

        foreach (var preset in allPresets
                     .Where(p => p.Kind != WingetSourceKind.Custom)
                     .OrderBy(p => p.Kind switch
                     {
                         WingetSourceKind.Domestic => 0,
                         WingetSourceKind.Official => 1,
                         _ => 2
                     })
                     .ThenBy(p => p.DisplayName, StringComparer.Ordinal))
        {
            // 只列已经进 winget 的源；没加的走「添加镜像」动作。
            if (!configured.TryGetValue(preset.Name, out var found)) continue;
            if (!statusKnown) continue;

            choices.Add(new SourceChoice(
                preset.Name,
                $"{preset.DisplayName} · 已启用",
                found.Url,
                preset.Note,
                true,
                false,
                preset.Kind == WingetSourceKind.Domestic,
                false,
                preset));
        }

        // 用户自己加的源（或 winget 自带的 winget-font 这类辅助源）原样列出，不隐藏、不改名。
        foreach (var source in configured.Values
                     .Where(s => WingetSourceCatalog.Find(s.Name) is null)
                     .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
        {
            choices.Add(new SourceChoice(
                source.Name,
                $"{source.Name} · 已启用",
                source.Url,
                source.IsExplicit ? "winget 中的显式源" : "winget 中的自定义源",
                true,
                false,
                false,
                true));
        }

        return choices;
    }

    /// <summary>
    /// 合并「一个源 → 该源的全部版本」为下拉选项：版本从新到旧，同版本多源分别保留，
    /// 让用户既能选版本也能选具体由哪个源安装。
    /// </summary>
    public static IReadOnlyList<AvailableVersion> MergeVersions(
        IEnumerable<KeyValuePair<string, IReadOnlyList<string>>> bySource)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var all = new List<AvailableVersion>();
        foreach (var group in bySource)
        {
            string source = (group.Key ?? "").Trim();
            if (!IsValidName(source)) continue;
            foreach (string raw in group.Value ?? Array.Empty<string>())
            {
                string version = (raw ?? "").Trim();
                if (!UpgradeRequest.IsValidVersionToken(version)) continue;
                if (!seen.Add(source + "\0" + version)) continue;
                all.Add(new AvailableVersion(version, source));
            }
        }

        return all
            .OrderByDescending(v => v.Version, Comparer<string>.Create(SoftwareVersion.Compare))
            .ThenBy(v => v.Source, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
