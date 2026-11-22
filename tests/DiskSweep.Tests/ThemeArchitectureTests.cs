using System.Text.RegularExpressions;
using Xunit;

namespace DiskSweep.Tests;

/// <summary>
/// 主题回归守护：令牌双份一致、无散落硬编码色值、结构样式只用 DynamicResource、
/// 两套主题都满足 WCAG 对比下限。全部基于源码文本，不引用 App 程序集，
/// 因此不影响「测试项目刻意不引用可缺省 AI 插件」的架构红线。
/// </summary>
public class ThemeArchitectureTests
{
    private static readonly string Root = FindRoot();
    private static string AppDir => Path.Combine(Root, "src", "DiskSweep.App");
    private static string ThemesDir => Path.Combine(AppDir, "Themes");
    private static string Light => Path.Combine(ThemesDir, "Tokens.Light.xaml");
    private static string Dark => Path.Combine(ThemesDir, "Tokens.Dark.xaml");
    private static string Styles => Path.Combine(ThemesDir, "Styles.xaml");

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DiskSweep.slnx"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("未找到 DiskSweep.slnx 所在的仓库根目录");
    }

    private static string Read(string path) => File.ReadAllText(path);

    private static string[] AllXaml() => Directory
        .EnumerateFiles(AppDir, "*.xaml", SearchOption.AllDirectories)
        .Where(p => !p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                    !p.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        .ToArray();

    private static bool IsTokenFile(string path) =>
        Path.GetFileName(path).StartsWith("Tokens.", StringComparison.Ordinal);

    private static HashSet<string> Keys(string path) => new(
        Regex.Matches(Read(path), @"x:Key=""([^""]+)""")
            .Select(m => m.Groups[1].Value), StringComparer.Ordinal);

    private static Dictionary<string, string> Colors(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(Read(path),
                     @"<SolidColorBrush\s+x:Key=""(?<key>\w+)""\s+Color=""#(?<hex>[0-9A-Fa-f]{6,8})"""))
            result[m.Groups["key"].Value] = m.Groups["hex"].Value;
        return result;
    }

    private static double Luminance(string hex)
    {
        if (hex.Length == 8) hex = hex[2..];       // 去掉 ARGB 的 alpha
        static double Channel(string pair)
        {
            double c = byte.Parse(pair, System.Globalization.NumberStyles.HexNumber) / 255d;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(hex[..2]) + 0.7152 * Channel(hex[2..4]) + 0.0722 * Channel(hex[4..6]);
    }

    private static double Contrast(string a, string b)
    {
        double x = Luminance(a), y = Luminance(b);
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }

    [Fact]
    public void BothTokenDictionaries_DefineExactlyTheSameKeys()
    {
        var light = Keys(Light);
        var dark = Keys(Dark);
        Assert.True(light.Count >= 35, $"语义令牌数量不足：{light.Count}");
        Assert.Equal(light.Count, dark.Count);
        Assert.Empty(light.Except(dark));
        Assert.Empty(dark.Except(light));
    }

    [Fact]
    public void Styles_AreFreeOfColorDefinitions()
    {
        var styles = Read(Styles);
        Assert.DoesNotContain("<SolidColorBrush", styles);
        Assert.DoesNotContain("#", styles.Replace("StrokeDashArray=\"3 2\"", ""));
    }

    [Fact]
    public void NoXamlOutsideTokenFiles_HardcodesHexColors()
    {
        var files = AllXaml().Where(p => !IsTokenFile(p)).ToArray();
        Assert.True(files.Length >= 5, $"应扫描到至少 5 个非令牌 XAML，实际 {files.Length} 个");
        foreach (var file in files)
        {
            var source = Read(file);
            var match = Regex.Match(source, @"#[0-9A-Fa-f]{6}\b");
            Assert.True(!match.Success,
                $"{Path.GetFileName(file)} 含硬编码色值 {match.Value}，应改用语义令牌");
        }
    }

    [Fact]
    public void EveryBrushReference_IsDynamicSoRuntimeSwitchingWorks()
    {
        var files = AllXaml().Where(p => !IsTokenFile(p)).ToArray();
        foreach (var file in files)
        {
            var match = Regex.Match(Read(file), @"\{StaticResource\s+\w*Brush\}");
            Assert.True(!match.Success,
                $"{Path.GetFileName(file)} 使用 {match.Value}；换主题时不会刷新，必须改为 DynamicResource");
        }
        Assert.Contains("{DynamicResource BgBrush}", Read(Path.Combine(AppDir, "MainWindow.xaml")));
    }

    [Theory]
    [InlineData("Tokens.Light.xaml")]
    [InlineData("Tokens.Dark.xaml")]
    public void Theme_MeetsWcagContrastFloors(string file)
    {
        var c = Colors(Path.Combine(ThemesDir, file));
        (string Fg, string Bg, double Min, string Label)[] pairs =
        {
            ("TextBrush", "BgBrush", 4.5, "主文本 / 窗口背景"),
            ("TextBrush", "CardBrush", 4.5, "主文本 / 卡片"),
            ("MutedBrush", "BgBrush", 3.0, "次文本 / 窗口背景"),
            ("MutedBrush", "CardBrush", 3.0, "次文本 / 卡片"),
            ("FaintBrush", "CardBrush", 3.0, "三级文本 / 卡片"),
            ("OnAccentBrush", "AccentBrush", 4.5, "按钮文字 / 按钮底色"),
            ("AccentBrush", "BgBrush", 3.0, "焦点环 / 窗口背景"),
            ("BorderBrush", "BgBrush", 1.2, "边框可见性"),
            ("LowTextBrush", "LowSoftBrush", 4.5, "低风险徽章"),
            ("MediumTextBrush", "MediumSoftBrush", 4.5, "中风险徽章"),
            ("HighTextBrush", "HighSoftBrush", 4.5, "高风险徽章"),
            ("CriticalTextBrush", "CriticalSoftBrush", 4.5, "严重徽章"),
            ("DangerTextBrush", "DangerSoftBrush", 4.5, "危险高亮行")
        };

        foreach (var (fg, bg, min, label) in pairs)
        {
            Assert.True(c.ContainsKey(fg) && c.ContainsKey(bg), $"{file} 缺少 {fg} 或 {bg} 令牌");
            double ratio = Contrast(c[fg], c[bg]);
            Assert.True(ratio >= min,
                $"{file} {label} 对比度 {ratio:0.00}:1 低于下限 {min}:1");
        }
    }

    [Fact]
    public void RuntimeSwitching_IsWiredFromStartupToBothUiEntryPoints()
    {
        var app = Read(Path.Combine(AppDir, "App.xaml"));
        Assert.Contains("Themes/Tokens.Dark.xaml", app);
        Assert.Contains("Themes/Styles.xaml", app);

        var startup = Read(Path.Combine(AppDir, "App.xaml.cs"));
        Assert.Contains("Composition.Theme.Apply(Services.Settings.Theme)", startup);
        Assert.Contains("ThemeService.AttachSystemListener", startup);

        var services = Read(Path.Combine(AppDir, "Services", "WpfServices.cs"));
        Assert.Contains("class ThemeService : IThemeService", services);
        Assert.Contains("SwapTokens(dictionaries, tokens)", services);
        Assert.Contains("public static void SwapTokens", services);
        Assert.Contains("AppsUseLightTheme", services);

        Assert.Contains("interface IThemeService", Read(Path.Combine(AppDir, "Services", "Contracts.cs")));
        Assert.Contains("class ThemeModes", Read(Path.Combine(AppDir, "Services", "ThemeModes.cs")));

        Assert.Contains("ToggleThemeCommand", Read(Path.Combine(AppDir, "MainWindow.xaml")));
        Assert.Contains("[RelayCommand]", Read(Path.Combine(AppDir, "ViewModels", "MainViewModel.cs")));
        Assert.Contains("ThemeIndex, Mode=TwoWay", Read(Path.Combine(AppDir, "Views", "SettingsWindow.xaml")));
        Assert.Contains("_settings.Theme = ThemeModes.FromIndex(ThemeIndex)",
            Read(Path.Combine(AppDir, "ViewModels", "SettingsViewModel.cs")));
    }
}
