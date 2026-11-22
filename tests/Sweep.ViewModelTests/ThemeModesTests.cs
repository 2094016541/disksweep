using Sweep.App.Services;
using Xunit;

namespace Sweep.ViewModelTests;

/// <summary>主题偏好是纯策略 + 命令，全部可在无 WPF 消息循环下验证。</summary>
public class ThemeModesTests
{
    [Theory]
    [InlineData(null, "system")]
    [InlineData("", "system")]
    [InlineData("unknown", "system")]
    [InlineData("light", "light")]
    [InlineData("dark", "dark")]
    public void Normalize_RejectsUnknownValues(string? input, string expected) =>
        Assert.Equal(expected, ThemeModes.Normalize(input));

    [Fact]
    public void Next_CyclesThroughAllThreeModes()
    {
        Assert.Equal("light", ThemeModes.Next("system"));
        Assert.Equal("dark", ThemeModes.Next("light"));
        Assert.Equal("system", ThemeModes.Next("dark"));
        Assert.Equal("light", ThemeModes.Next(null));
    }

    [Theory]
    [InlineData("system", 0)]
    [InlineData("light", 1)]
    [InlineData("dark", 2)]
    [InlineData("bogus", 0)]
    public void Index_RoundTripsEveryMode(string mode, int index)
    {
        Assert.Equal(index, ThemeModes.ToIndex(mode));
        Assert.Equal(ThemeModes.Normalize(mode), ThemeModes.FromIndex(index));
    }

    [Fact]
    public void Label_IsLocalizedForEveryMode()
    {
        Assert.Equal("跟随系统", ThemeModes.Label("system"));
        Assert.Equal("浅色", ThemeModes.Label("light"));
        Assert.Equal("深色", ThemeModes.Label("dark"));
    }

    [Fact]
    public void MainViewModel_ToggleTheme_CyclesAppliesPersistsAndUpdatesLabel()
    {
        var context = new FakeContext();
        var theme = new FakeTheme();
        var vm = Fixture.CreateMain(context, theme: theme);

        Assert.Equal("system", theme.Mode);
        Assert.Contains("跟随系统", vm.ThemeLabel);

        vm.ToggleThemeCommand.Execute(null);
        Assert.Equal("light", theme.Mode);
        Assert.Equal("light", context.Settings.Theme);
        Assert.Contains("浅色", vm.ThemeLabel);
        Assert.Equal(1, context.SaveCount);

        vm.ToggleThemeCommand.Execute(null);
        Assert.Equal("dark", theme.Mode);
        Assert.Equal("dark", context.Settings.Theme);
        Assert.Contains("深色", vm.ThemeLabel);

        vm.ToggleThemeCommand.Execute(null);
        Assert.Equal("system", theme.Mode);
        Assert.Equal("system", context.Settings.Theme);
        Assert.Contains("跟随系统", vm.ThemeLabel);

        Assert.Equal(3, theme.ApplyCount);
        Assert.Equal(3, context.SaveCount);
    }
}
