using System.Windows;
using System.Windows.Media;
using Sweep.App.Services;
using Xunit;

namespace Sweep.ViewModelTests;

/// <summary>
/// 运行时换肤的核心证据：替换合并字典里的令牌字典后，通过资源键查到的画刷真的变了，
/// 且结构样式字典保持不动。全程不需要 Application / Dispatcher / Window。
/// </summary>
public class ThemeSwapTests
{
    private static ResourceDictionary Tokens(byte r, byte g, byte b) =>
        new() { { "BgBrush", new SolidColorBrush(Color.FromRgb(r, g, b)) } };

    private static readonly Color Light = Color.FromRgb(0xF3, 0xF6, 0xFA);
    private static readonly Color Dark = Color.FromRgb(0x0A, 0x10, 0x18);

    [Fact]
    public void SwapTokens_ChangesResolvedBrushInPlaceAndKeepsStyles()
    {
        var styles = new ResourceDictionary { { "MarkerStyle", new Style() } };
        var merged = new List<ResourceDictionary> { Tokens(0x0A, 0x10, 0x18), styles };

        ThemeService.SwapTokens(merged, Tokens(0xF3, 0xF6, 0xFA));

        Assert.Equal(2, merged.Count);
        Assert.True(ReferenceEquals(styles, merged[1]), "结构样式字典不得被换掉或移动");
        Assert.Equal(Light, Assert.IsType<SolidColorBrush>(merged[0]["BgBrush"]).Color);
    }

    [Fact]
    public void SwapTokens_IsIdempotentAcrossRepeatedSwitches()
    {
        var merged = new List<ResourceDictionary> { Tokens(0x0A, 0x10, 0x18) };
        for (int i = 0; i < 5; i++)
        {
            ThemeService.SwapTokens(merged, Tokens(0xF3, 0xF6, 0xFA));
            ThemeService.SwapTokens(merged, Tokens(0x0A, 0x10, 0x18));
            Assert.True(merged.Count == 1, "反复切换不得不断插入新字典导致资源泄漏");
        }
        Assert.Equal(Dark, Assert.IsType<SolidColorBrush>(merged[0]["BgBrush"]).Color);
    }

    [Fact]
    public void SwapTokens_InsertsWhenNoTokenDictionaryPresent()
    {
        var styles = new ResourceDictionary { { "MarkerStyle", new Style() } };
        var merged = new List<ResourceDictionary> { styles };

        ThemeService.SwapTokens(merged, Tokens(0x0A, 0x10, 0x18));

        Assert.Equal(2, merged.Count);
        Assert.Same(styles, merged[1]);
        Assert.True(merged[0].Contains("BgBrush"));
    }
}
