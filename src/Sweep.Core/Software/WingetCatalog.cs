using System.Text.RegularExpressions;

namespace Sweep.Core.Software;

/// <summary>winget 报告的一个可升级包：Id 是后续 --id 参数，AvailableVersion 是默认升级目标。</summary>
public sealed record WingetPackage(
    string Name,
    string Id,
    string CurrentVersion,
    string AvailableVersion,
    string Source)
{
    /// <summary>
    /// 下拉里的展示文本：名称 + 包 ID。包 ID 是「凭什么用这一项去升级」的唯一凭据，
    /// 只写名称会让用户不知道选中项到底对应哪个包。
    /// </summary>
    public string Display => string.IsNullOrWhiteSpace(Id) ? Name : $"{Name} · {Id}";

    /// <summary>自绘 ComboBox 模板会把 DisplayMemberPath 退化成 ToString()。</summary>
    public override string ToString() => Display;
}

/// <summary>
/// winget 控制台文本的纯解析器。winget 没有稳定的 JSON 输出（1.29 实测 --output json 直接报
/// 「无法识别参数名称」），只能解析定宽表格：表头由 <see cref="WingetTable"/> 给出列起点，
/// 这里再按列名（中英文别名）取字段。找不到表头时退回旧的「多空格分列」逻辑，
/// 保证手写的片段与异常输出仍有兜底。
/// 解析结果只用于展示与关联，任何写操作都由用户确认后交给 winget 自己完成。
/// </summary>
public static class WingetOutputParser
{
    private static readonly Regex ColumnSeparator = new(@"\s{2,}", RegexOptions.Compiled);
    private static readonly Regex VersionTail = new(@"\bv?\d+(?:\.\d+)+\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex NoiseWords = new(@"[^\w\u4e00-\u9fff]+", RegexOptions.Compiled);

    private static readonly string[] NameHeaders = { "名称", "Name" };
    private static readonly string[] VersionHeaders = { "版本", "Version" };
    private static readonly string[] AvailableHeaders = { "可用", "Available" };
    private static readonly string[] SourceHeaders = { "源", "Source" };

    public static IReadOnlyList<WingetPackage> ParseUpgradable(string? output)
    {
        var table = WingetTable.Parse(output);
        if (table is null) return ParseUpgradableByGaps(output);

        int idColumn = table.Column("ID", "Id");
        int nameColumn = table.Column(NameHeaders);
        int versionColumn = table.Column(VersionHeaders);
        // 有些语言版本没有「可用」列：退回「名称 / ID / 版本 / 可用 / 源」的位置约定。
        int availableColumn = table.Column(AvailableHeaders);
        if (availableColumn < 0) availableColumn = 3;
        int sourceColumn = table.Column(SourceHeaders);

        var rows = new List<WingetPackage>();
        foreach (var row in table.Rows)
        {
            string id = WingetTable.WingetTableData.Cell(row, idColumn);
            if (!IsPlausibleId(id)) continue;
            string available = WingetTable.WingetTableData.Cell(row, availableColumn);
            if (!HasDigit(available)) continue;                       // 「可用」列必须是版本

            rows.Add(new WingetPackage(
                Name: WingetTable.WingetTableData.Cell(row, nameColumn),
                Id: id,
                CurrentVersion: WingetTable.WingetTableData.Cell(row, versionColumn),
                AvailableVersion: available,
                Source: WingetTable.WingetTableData.Cell(row, sourceColumn)));
        }
        return rows;
    }

    /// <summary>没有表头时的兜底解析：按两个以上空格分列（旧行为，保持兼容）。</summary>
    private static IReadOnlyList<WingetPackage> ParseUpgradableByGaps(string? output)
    {
        var rows = new List<WingetPackage>();
        foreach (string raw in Lines(output))
        {
            string line = raw.Trim();
            if (line.Length == 0 || IsSeparator(line)) continue;

            string[] fields = ColumnSeparator.Split(line)
                .Where(f => f.Length > 0).ToArray();
            if (fields.Length < 4) continue;
            if (fields[1].Equals("ID", StringComparison.OrdinalIgnoreCase)) continue;   // 中英文表头
            if (!IsPlausibleId(fields[1])) continue;
            if (!HasDigit(fields[3])) continue;                                         // 「可用」列必须是版本

            rows.Add(new WingetPackage(
                Name: fields[0],
                Id: fields[1],
                CurrentVersion: fields[2],
                AvailableVersion: fields[3],
                Source: fields.Length >= 5 ? fields[4] : ""));
        }
        return rows;
    }

    /// <summary>
    /// 解析 winget search 的结果表。列是「名称 / ID / 版本 / 匹配 / 源」，其中版本与匹配两列可能整列为空。
    /// 按列名取值而不是按位置：位置会因为「匹配」列是否存在而整体错位。
    /// </summary>
    public static IReadOnlyList<WingetPackage> ParseSearch(string? output)
    {
        var table = WingetTable.Parse(output);
        if (table is null) return ParseSearchByGaps(output);

        int idColumn = table.Column("ID", "Id");
        int nameColumn = table.Column(NameHeaders);
        int versionColumn = table.Column(VersionHeaders);
        int sourceColumn = table.Column(SourceHeaders);

        var rows = new List<WingetPackage>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in table.Rows)
        {
            string id = WingetTable.WingetTableData.Cell(row, idColumn);
            if (!IsPlausibleId(id) || !seen.Add(id)) continue;

            string version = WingetTable.WingetTableData.Cell(row, versionColumn);
            if (!LooksLikeVersion(version)) version = "";   // 「匹配」列的值不是版本的情况已在列名上排除

            rows.Add(new WingetPackage(
                Name: WingetTable.WingetTableData.Cell(row, nameColumn),
                Id: id,
                CurrentVersion: version,
                AvailableVersion: "",      // search 不报告「可升级到」，这里不做猜测
                Source: WingetTable.WingetTableData.Cell(row, sourceColumn)));
        }
        return rows;
    }

    private static IReadOnlyList<WingetPackage> ParseSearchByGaps(string? output)
    {
        var rows = new List<WingetPackage>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in Lines(output))
        {
            string line = raw.Trim();
            if (line.Length == 0 || IsSeparator(line)) continue;

            string[] fields = ColumnSeparator.Split(line).Where(f => f.Length > 0).ToArray();
            if (fields.Length < 2) continue;
            if (fields[1].Equals("Id", StringComparison.OrdinalIgnoreCase) ||
                fields[1].Equals("ID", StringComparison.OrdinalIgnoreCase)) continue;   // 中英文表头
            if (!IsPlausibleId(fields[1])) continue;
            if (!seen.Add(fields[1])) continue;

            var rest = fields.Skip(2).ToArray();
            string version = rest.FirstOrDefault(LooksLikeVersion) ?? "";
            string source = rest.Length > 0 && WingetSources.IsValidName(rest[^1]) && rest[^1] != version
                ? rest[^1]
                : "";

            rows.Add(new WingetPackage(
                Name: fields[0],
                Id: fields[1],
                CurrentVersion: version,
                AvailableVersion: "",      // search 不报告「可升级到」，这里不做猜测
                Source: source));
        }
        return rows;
    }

    /// <summary>
    /// 「像版本号」的判定要比版本白名单更严：白名单允许纯字母开头的串，
    /// 于是 search 的「匹配」列里的 7zip / moniker 之类会被误当成版本。
    /// 这里要求含数字，且要么带点、要么整体是数字。
    /// </summary>
    private static bool LooksLikeVersion(string text)
    {
        if (!UpgradeRequest.IsValidVersionToken(text)) return false;
        if (!text.Any(char.IsDigit)) return false;
        return text.Contains('.') || text.All(c => char.IsDigit(c) || c == ' ');
    }

    /// <summary>解析 winget show --id X --versions --exact 的单列版本清单，返回从新到旧、去重的结果。</summary>
    public static IReadOnlyList<string> ParseVersionList(string? output)
    {
        var versions = new List<string>();
        foreach (string raw in Lines(output))
        {
            string line = raw.Trim();
            if (line.Length == 0 || IsSeparator(line)) continue;
            if (line.StartsWith("Windows 程序包管理器", StringComparison.Ordinal) ||
                line.StartsWith("Windows Package Manager", StringComparison.OrdinalIgnoreCase)) continue;
            if (!HasDigit(line)) continue;
            if (!UpgradeRequest.IsValidVersionToken(line)) continue;
            versions.Add(line);
        }
        return SoftwareVersion.SortDescending(versions);
    }

    private static IEnumerable<string> Lines(string? output) =>
        (output ?? "").Replace('\r', '\n').Split('\n');

    private static bool IsSeparator(string line) => line.All(c => c is '-' or '=' or ' ' or '\t');

    private static bool IsPlausibleId(string id) =>
        id.Length >= 2 && id.Length <= 160 && !id.Any(char.IsWhiteSpace) &&
        (id.Contains('.') || id.Contains('\\') || id.Contains('_') || id.Contains('-') ||
         // 商店包 ID 是「字母 + 数字」的长串，没有任何分隔符（例如 XP9KHM4BK9FZ7Q）。
         (id.Length >= 8 && id.Any(char.IsLetter) && id.Any(char.IsDigit)));

    private static bool HasDigit(string text) => text.Any(char.IsDigit);
}

/// <summary>把只读注册表条目与 winget 的可升级包做名称关联；关联失败时返回 null 由用户手动填写包 ID。</summary>
public static class UpgradeCatalog
{
    private static readonly Regex VersionTail =
        new(@"\bv?\d+(?:\.\d+)+\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex NoiseWords =
        new(@"[^\w\u4e00-\u9fff]+", RegexOptions.Compiled);

    public static WingetPackage? Resolve(SoftwareEntry entry, IReadOnlyList<WingetPackage> packages)
    {
        if (entry is null || packages.Count == 0) return null;

        string name = Normalize(entry.Name);
        string bare = Normalize(VersionTail.Replace(entry.Name ?? "", " "));
        string current = (entry.Version ?? "").Trim();
        WingetPackage? best = null;
        int bestScore = 0;

        foreach (var package in packages)
        {
            if (string.IsNullOrWhiteSpace(package.Id)) continue;
            string other = Normalize(package.Name);
            string otherBare = Normalize(VersionTail.Replace(package.Name ?? "", " "));

            int score;
            if (name.Length > 0 && name == other) score = 100;
            else if (bare.Length >= 3 && bare == otherBare) score = 80;
            else if (bare.Length >= 4 && otherBare.Length >= 4 &&
                     (ContainsPhrase(other, bare) || ContainsPhrase(bare, otherBare))) score = 40;
            else continue;

            if (current.Length > 0 && string.Equals(current, package.CurrentVersion,
                    StringComparison.OrdinalIgnoreCase)) score += 10;
            if (package.Source.Contains("winget", StringComparison.OrdinalIgnoreCase)) score += 5;

            if (score > bestScore)
            {
                bestScore = score;
                best = package;
            }
        }
        return best;
    }

    /// <summary>整词短语包含，避免 "git" 误匹配 "github cli"。</summary>
    private static bool ContainsPhrase(string haystack, string needle) =>
        (" " + haystack + " ").Contains(" " + needle + " ", StringComparison.Ordinal);

    /// <summary>小写化、去掉括号修饰与标点，只保留可作为名称比对的词。</summary>
    private static string Normalize(string? text)
    {
        string value = (text ?? "").ToLowerInvariant();
        value = Regex.Replace(value, @"\(.*?\)", " ");
        value = NoiseWords.Replace(value, " ");
        return string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
