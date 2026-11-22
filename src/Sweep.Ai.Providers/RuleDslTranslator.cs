using System.Text.RegularExpressions;
using Sweep.Ai.Abstractions;

namespace Sweep.Ai.Providers;

/// <summary>6.9 自然语言 → 规则 DSL：仅翻译 + 预览，绝不直接执行。纯本地解析，离线可用。</summary>
public static class RuleDslTranslator
{
    private static readonly string[] KnownExtensions =
    {
        ".exe", ".msi", ".iso", ".zip", ".7z", ".rar", ".dmg", ".apk", ".tmp", ".log", ".dmp", ".bak"
    };

    public static RuleDsl Translate(string input)
    {
        var text = input ?? "";
        var extensions = KnownExtensions
            .Where(e => text.Contains(e, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (extensions.Count == 0 && (text.Contains("安装包") || text.Contains("安装程序")))
            extensions = new List<string> { ".exe", ".msi", ".iso", ".zip" };
        if (extensions.Count == 0 && text.Contains("日志"))
            extensions = new List<string> { ".log", ".tmp" };
        if (extensions.Count == 0 && text.Contains("缓存"))
            extensions = new List<string> { ".tmp", ".cache" };

        int days = 90;
        var numeric = Regex.Match(text, @"(\d+)\s*天", RegexOptions.None, TimeSpan.FromSeconds(1));
        if (numeric.Success) int.TryParse(numeric.Groups[1].Value, out days);
        else if (text.Contains("一年") || text.Contains("12 个月")) days = 365;
        else if (text.Contains("半年") || text.Contains("6 个月")) days = 180;
        else if (text.Contains("三个月") || text.Contains("3 个月")) days = 90;
        else if (text.Contains("一个月") || text.Contains("1 个月")) days = 30;
        else if (text.Contains("一周")) days = 7;

        var excludes = new List<string>();
        foreach (Match m in Regex.Matches(text, @"(?:别动|排除|不包含|不要)\s*([A-Za-z])\s*盘",
                     RegexOptions.None, TimeSpan.FromSeconds(1)))
            excludes.Add(m.Groups[1].Value.ToUpperInvariant());

        string category = text.Contains("安装包") || text.Contains("安装程序") ? "安装包"
                        : text.Contains("日志") ? "日志"
                        : text.Contains("缓存") ? "缓存" : "自定义规则";
        return new RuleDsl(category, extensions, Math.Clamp(days, 1, 3650), excludes.Distinct().ToArray());
    }

    public static string Render(RuleDsl dsl)
    {
        var ext = dsl.Extensions.Count > 0 ? string.Join("、", dsl.Extensions) : "（未指定扩展名）";
        var drives = dsl.ExcludeDrives.Count > 0
            ? $"；排除 {string.Join("、", dsl.ExcludeDrives)} 盘" : "";
        return $"类别「{dsl.Category}」；扩展名 {ext}；修改时间早于 {dsl.OlderThanDays} 天{drives}。仅生成候选预览，需用户确认后写入用户规则包。";
    }
}
