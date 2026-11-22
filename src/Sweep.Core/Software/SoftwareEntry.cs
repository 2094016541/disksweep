using Sweep.Core.Recommendation;

namespace Sweep.Core.Software;

/// <summary>卸载键所在的注册表范围；同一软件可能同时在 64/32 位视图中出现。</summary>
public enum SoftwareScope
{
    Machine64,
    Machine32,
    CurrentUser
}

/// <summary>
/// 一条「已安装软件」记录，字段全部来自 Windows 的 Uninstall 注册表键，未做任何修改。
/// 这是只读快照：Sweep 不会写注册表，也不会自行删除安装目录。
/// </summary>
public sealed record SoftwareEntry(
    string RegistryKey,
    string Name,
    string Version,
    string Publisher,
    string InstallLocation,
    string InstallDate,
    string UninstallString,
    long EstimatedSizeBytes,
    SoftwareScope Scope,
    bool IsSystemComponent,
    // 下面三项是本轮补充的只读信息，均带默认值以兼容既有构造点。
    string DisplayIcon = "",
    bool NoRemove = false,
    // 注册值缺失时，从 DisplayIcon / 卸载程序目录推断出的安装目录；仅用于测量与打开位置。
    string DiscoveredLocation = "")
{
    public string ScopeText => Scope switch
    {
        SoftwareScope.Machine64 => "本机 · 64 位",
        SoftwareScope.Machine32 => "本机 · 32 位",
        _ => "当前用户"
    };

    /// <summary>注册表登记的估算体积（KB 精度），可能为 0 表示未登记。</summary>
    public string SizeText => EstimatedSizeBytes > 0 ? PlanItem.FormatSize(EstimatedSizeBytes) : "未登记";

    public string PublisherText => string.IsNullOrWhiteSpace(Publisher) ? "未知发布者" : Publisher;
    public string VersionText => string.IsNullOrWhiteSpace(Version) ? "—" : Version;
    public string DateText => string.IsNullOrWhiteSpace(InstallDate) ? "—" : InstallDate;

    /// <summary>
    /// 界面与测量用的目录：优先注册值，缺失时用推断值。
    /// 两条路径都过一遍 <see cref="SoftwareLocation.NormalizePath"/>——注册表里的 InstallLocation
    /// 常带引号或多余空格，原样拿去 Directory.Exists 会判成「目录不存在」，于是既打不开也量不到。
    /// </summary>
    public string EffectiveLocation
    {
        get
        {
            string registered = SoftwareLocation.NormalizePath(InstallLocation);
            return registered.Length > 0 ? registered : SoftwareLocation.NormalizePath(DiscoveredLocation);
        }
    }

    public bool LocationIsDerived =>
        SoftwareLocation.NormalizePath(InstallLocation).Length == 0 && HasLocation;

    public string LocationText => !HasLocation
        ? "未登记安装目录"
        : EffectiveLocation + (LocationIsDerived ? "（据图标推断）" : "");

    public bool HasLocation => !string.IsNullOrWhiteSpace(EffectiveLocation);

    /// <summary>
    /// 只有「有卸载命令」且「未被厂商标记 NoRemove」才允许点卸载。
    /// NoRemove=1 是厂商明确的“不要在这里卸载”信号（例如 VMware 的组件登记），必须尊重。
    /// </summary>
    public bool CanUninstall => !string.IsNullOrWhiteSpace(UninstallString) && !NoRemove;

    /// <summary>不可卸载时的原因；可卸载时为空串。界面据此给出具体说明而不是灰按钮。</summary>
    public string UninstallHint
    {
        get
        {
            if (NoRemove) return "厂商登记为不可卸载（NoRemove），请用软件自带的维护入口";
            if (string.IsNullOrWhiteSpace(UninstallString)) return "该登记没有卸载命令，只能查看或用「扫描残留」处理";
            return "";
        }
    }

    public bool Matches(string query) =>
        query.Length == 0 ||
        Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        Publisher.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        Version.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        EffectiveLocation.Contains(query, StringComparison.OrdinalIgnoreCase);
}

/// <summary>把注册表原始值规范化成可展示、可排序的软件清单。</summary>
public static class SoftwareEntryFilter
{
    /// <summary>
    /// 过滤系统组件 / 父项下的补丁，并按「名称+版本+发布者」去重。
    /// 无卸载命令的登记会保留——它们是真实安装过的软件（例如 JetBrains 的新版安装器只写登记不写命令），
    /// 整条丢弃会让用户以为没装；保留后由 <see cref="SoftwareEntry.CanUninstall"/> 与提示文案说明原因。
    /// </summary>
    public static IReadOnlyList<SoftwareEntry> Normalize(IEnumerable<SoftwareEntry> entries)
    {
        var unique = new Dictionary<string, SoftwareEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (entry is null || entry.IsSystemComponent) continue;
            if (string.IsNullOrWhiteSpace(entry.Name)) continue;

            string key = string.Join("|",
                entry.Name.Trim(), entry.Version.Trim(), entry.Publisher.Trim());
            if (!unique.TryGetValue(key, out var existing) || Score(entry) > Score(existing))
                unique[key] = entry;
        }

        return unique.Values
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Publisher, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>信息越全分数越高；能真正执行卸载的登记额外加权，避免被同名只读登记挤掉。</summary>
    private static int Score(SoftwareEntry e) =>
        (string.IsNullOrWhiteSpace(e.UninstallString) ? 0 : 8) +
        (e.Scope == SoftwareScope.Machine64 ? 4 : 0) +
        (e.HasLocation ? 2 : 0) +
        (e.EstimatedSizeBytes > 0 ? 1 : 0) +
        (e.InstallDate.Length > 0 ? 1 : 0);

    /// <summary>InstallDate 通常是 yyyymmdd；其它格式原样返回，不猜、不抛异常。</summary>
    public static string ParseInstallDate(string? raw)
    {
        var text = (raw ?? "").Trim();
        if (text.Length == 8 && text.All(char.IsDigit) &&
            DateTime.TryParseExact(text, "yyyyMMdd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var date))
            return date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        return text;
    }

    /// <summary>EstimatedSize 是 REG_DWORD，单位 KB；异常/负值一律记为 0。</summary>
    public static long ParseEstimatedSize(object? value)
    {
        long kb = value switch
        {
            int i => i,
            long l => l,
            uint u => u,
            string s when long.TryParse(s, out var parsed) => parsed,
            _ => 0
        };
        return kb > 0 ? kb * 1024L : 0;
    }
}
