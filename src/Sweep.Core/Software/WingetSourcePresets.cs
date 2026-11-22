namespace Sweep.Core.Software;

/// <summary>内置软件源的类别，用于排序与文案（不影响 winget 的源类型）。</summary>
public enum WingetSourceKind
{
    Domestic,
    Official,
    Store,
    Custom
}

/// <summary>
/// 一个可选的 winget 软件源。Name 就是 <c>--source</c> 参数，Url 是添加源时用的地址。
/// 预置源只是「模板」：是否已经加进 winget 由 <see cref="WingetSources"/> 与源表比对得出。
/// </summary>
public sealed record WingetSourcePreset(
    string Name,
    string DisplayName,
    string Url,
    string Note,
    WingetSourceKind Kind);

/// <summary>
/// 内置软件源目录与「添加到 winget」的参数拼装。纯逻辑：不启动进程、不读注册表，可单测。
///
/// 选源口径：winget 官方源与商店源都在境外，国内拉索引经常几十秒甚至超时；
/// 中科大镜像（ustc）与教育网联合镜像（cernet）都镜像了 winget-pkgs 社区库，
/// 因此默认推荐 ustc——它是这两个镜像里维护时间最长、更新最勤的一个。
/// 官方源保持启用，不删除、不替换，用户随时可以切回去。
/// </summary>
public static class WingetSourceCatalog
{
    public const string OfficialName = "winget";
    public const string StoreName = "msstore";
    /// <summary>国内首选：中科大镜像。</summary>
    public const string PreferredDomesticName = "ustc";
    /// <summary>1.8 起 source add 需要显式信任级别，否则会被当作不可信源。</summary>
    public static readonly Version TrustLevelSince = new(1, 8);

    public static IReadOnlyList<WingetSourcePreset> Presets { get; } = new[]
    {
        new WingetSourcePreset(
            PreferredDomesticName, "中科大镜像", "https://mirrors.ustc.edu.cn/winget-source",
            "国内首选：镜像 winget-pkgs 社区库，索引更新最勤", WingetSourceKind.Domestic),
        new WingetSourcePreset(
            "cernet", "教育网联合镜像", "https://mirrors.cernet.edu.cn/winget-source",
            "教育网联合镜像，自动跳到最近节点", WingetSourceKind.Domestic),
        new WingetSourcePreset(
            OfficialName, "微软官方源", "https://cdn.winget.microsoft.com/cache",
            "微软官方维护，国内访问较慢", WingetSourceKind.Official),
        new WingetSourcePreset(
            StoreName, "微软商店源", "https://storeedgefd.dsx.mp.microsoft.com/v9.0",
            "仅商店应用（MSIX），需接受商店协议", WingetSourceKind.Store)
    };

    public static WingetSourcePreset? Find(string? name)
    {
        string key = (name ?? "").Trim();
        return key.Length == 0
            ? null
            : Presets.FirstOrDefault(p => p.Name.Equals(key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 默认选中的源：已启用的国内源优先，没有则返回 null（表示用「所有软件源」）。
    /// 绝不返回未启用的源——选一个没加进 winget 的源只会让查询直接报错。
    /// </summary>
    public static string? Recommend(IEnumerable<string>? configuredNames)
    {
        var configured = new HashSet<string>(
            (configuredNames ?? Array.Empty<string>()).Where(WingetSources.IsValidName),
            StringComparer.OrdinalIgnoreCase);

        foreach (var preset in Presets.Where(p => p.Kind == WingetSourceKind.Domestic))
            if (configured.Contains(preset.Name)) return preset.Name;
        return null;
    }

    /// <summary>
    /// 还没加进 winget 的国内镜像（用于「添加镜像」动作，而不是塞进源下拉）。
    /// 顺序按 <see cref="Presets"/>：中科大优先。
    /// </summary>
    public static WingetSourcePreset? FirstUnconfiguredDomestic(IEnumerable<string>? configuredNames)
    {
        var configured = new HashSet<string>(
            (configuredNames ?? Array.Empty<string>()).Where(WingetSources.IsValidName),
            StringComparer.OrdinalIgnoreCase);

        return Presets.FirstOrDefault(p =>
            p.Kind == WingetSourceKind.Domestic && !configured.Contains(p.Name));
    }

    /// <summary>
    /// 拼装 <c>winget source add</c> 的参数。1.8 起必须带 --trust-level trusted，
    /// 旧版本不认这个参数，所以要按实际版本裁剪。
    /// 不添加 --accept-source-agreements：是否接受协议由用户在控制台里自己确认。
    /// </summary>
    public static IReadOnlyList<string> AddArguments(
        WingetSourcePreset preset, string? wingetVersion)
    {
        var args = new List<string>
        {
            "source", "add",
            "--name", preset.Name,
            "--arg", preset.Url,
            "--disable-interactivity"
        };
        if (SupportsTrustLevel(wingetVersion)) args.AddRange(new[] { "--trust-level", "trusted" });
        return args;
    }

    public static bool SupportsTrustLevel(string? wingetVersion)
    {
        if (string.IsNullOrWhiteSpace(wingetVersion)) return true;   // 未知版本时按新版处理
        string text = wingetVersion.Trim().TrimStart('v', 'V');
        int dash = text.IndexOfAny(new[] { '-', '+' });
        if (dash > 0) text = text[..dash];
        return Version.TryParse(text, out var version) ? version >= TrustLevelSince : true;
    }
}

/// <summary>下拉里的一项：既可能是「所有软件源」，也可能是某个具体的源。</summary>
public sealed record SourceChoice(
    string Name,
    string Display,
    string Url,
    string Note,
    bool IsConfigured,
    bool IsAll,
    bool IsDomestic,
    bool IsCustom,
    // 预置源携带原始定义，界面要「启用该源」时直接用它拼 winget 参数；「所有软件源」为 null。
    WingetSourcePreset? Preset = null)
{
    /// <summary>ComboBox 的自绘模板会把 DisplayMemberPath 退化成 ToString()，所以这里也返回 Display。</summary>
    public override string ToString() => Display;
}
