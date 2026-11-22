using DiskSweep.Core.Software;
using Xunit;

namespace DiskSweep.Tests;

/// <summary>软件目录的纯逻辑：注册表值规范化与卸载命令解析都不需要真实注册表 / 进程。</summary>
public class SoftwareCatalogTests
{
    private static SoftwareEntry Entry(
        string name,
        string version = "1.0",
        string publisher = "Acme",
        string location = @"C:\Program Files\Acme",
        long size = 0,
        string date = "",
        string uninstall = "MsiExec.exe /X{TEST}",
        SoftwareScope scope = SoftwareScope.Machine64,
        bool system = false) =>
        new(name + "-" + scope, name, version, publisher, location, date,
            uninstall, size, scope, system);

    [Fact]
    public void UninstallCommand_SplitsQuotedPathAndArguments()
    {
        var command = UninstallCommand.Parse(
            "\"C:\\Program Files\\Acme\\unins000.exe\" /LOG /NORESTART");

        Assert.NotNull(command);
        Assert.Equal(@"C:\Program Files\Acme\unins000.exe", command!.FileName);
        Assert.Equal("/LOG /NORESTART", command.Arguments);
        Assert.False(command.IsSilent);
        Assert.False(command.IsSystemCommand);
    }

    [Fact]
    public void UninstallCommand_SplitsUnquotedPathAtExeBoundary()
    {
        var command = UninstallCommand.Parse(@"C:\Program Files\Acme\uninst.exe /U:1");

        Assert.NotNull(command);
        Assert.Equal(@"C:\Program Files\Acme\uninst.exe", command!.FileName);
        Assert.Equal("/U:1", command.Arguments);
    }

    [Fact]
    public void UninstallCommand_RecognizesMsiAndSystemCommands()
    {
        var command = UninstallCommand.Parse("MsiExec.exe /X{12345678-ABCD}");

        Assert.NotNull(command);
        Assert.Equal("MsiExec.exe", command!.FileName);
        Assert.Equal("/X{12345678-ABCD}", command.Arguments);
        Assert.True(command.IsSystemCommand);
        Assert.False(command.IsSilent);
    }

    [Theory]
    [InlineData("MsiExec.exe /X{ABC} /qn")]
    [InlineData("\"C:\\Program Files\\Acme\\unins000.exe\" /VERYSILENT")]
    [InlineData("\"C:\\Program Files\\Acme\\unins000.exe\" /S")]
    [InlineData("uninst.exe /passive")]
    [InlineData("uninst.exe --quiet")]
    public void UninstallCommand_DetectsSilentSwitches(string raw)
    {
        var command = UninstallCommand.Parse(raw);
        Assert.NotNull(command);
        Assert.True(command!.IsSilent, $"{raw} 应被识别为静默卸载");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"C:\\unterminated.exe /X")]
    [InlineData("uninst.exe /X\ncalc.exe")]
    public void UninstallCommand_RejectsUnusableOrInjectedStrings(string raw) =>
        Assert.Null(UninstallCommand.Parse(raw));

    /// <summary>
    /// 系统组件与补丁子项要过滤，同名同版本的同一条目按信息量去重；
    /// 但「没有卸载命令」的登记必须保留——JetBrains 新版安装器就只写登记不写命令，
    /// 整条丢掉会让用户以为软件没装。保留后由 CanUninstall=false 与提示文案说明原因。
    /// </summary>
    [Fact]
    public void Normalize_FiltersSystemComponentsAndDuplicateViews()
    {
        var entries = new[]
        {
            Entry("Zeta"),
            Entry("Alpha", location: "", size: 0, scope: SoftwareScope.Machine32),
            Entry("Alpha", location: @"C:\Program Files\Alpha", size: 2048, date: "2025-01-02"),
            Entry("Hidden", system: true),
            Entry("NoUninstall", uninstall: ""),
            Entry("   "),
        };

        var result = SoftwareEntryFilter.Normalize(entries);

        Assert.Equal(new[] { "Alpha", "NoUninstall", "Zeta" }, result.Select(e => e.Name));
        // 同名同版本同发布者只保留信息更全的 64 位记录。
        Assert.Equal(SoftwareScope.Machine64, result[0].Scope);
        Assert.Equal(2048, result[0].EstimatedSizeBytes);
        Assert.True(result[0].CanUninstall);

        // 只读登记可见但不可卸载，并且给得出原因。
        Assert.False(result[1].CanUninstall);
        Assert.Contains("没有卸载命令", result[1].UninstallHint);
    }

    /// <summary>厂商用 NoRemove=1 明确表示「不要在这里卸载」，必须尊重。</summary>
    [Fact]
    public void Normalize_RespectsVendorNoRemoveFlag()
    {
        var blocked = Entry("Vmware Component") with { NoRemove = true };

        var result = SoftwareEntryFilter.Normalize(new[] { blocked });

        Assert.Single(result);
        Assert.False(result[0].CanUninstall);
        Assert.Contains("NoRemove", result[0].UninstallHint);
        // 卸载命令本身还在，只是不允许从这里执行。
        Assert.False(string.IsNullOrWhiteSpace(result[0].UninstallString));
    }

    /// <summary>msiexec 的 /I 是「安装/维护」，点卸载时应该执行 /X。</summary>
    [Theory]
    [InlineData(@"MsiExec.exe /I{1CC6D498-D386-4FB3-8B3A-F00088C626C3}", "/X{1CC6D498-D386-4FB3-8B3A-F00088C626C3}")]
    [InlineData(@"msiexec /i{1CC6D498-D386-4FB3-8B3A-F00088C626C3}", "/X{1CC6D498-D386-4FB3-8B3A-F00088C626C3}")]
    [InlineData(@"MsiExec.exe /X{1CC6D498-D386-4FB3-8B3A-F00088C626C3}", "/X{1CC6D498-D386-4FB3-8B3A-F00088C626C3}")]
    public void UninstallCommand_RewritesMsiMaintenanceToUninstall(string raw, string expected)
    {
        var command = UninstallCommand.Parse(raw);

        Assert.NotNull(command);
        Assert.Equal(expected, command!.Arguments);
    }

    /// <summary>带其它开关的 /I 不改写：那些参数可能是有意义的安装/转换指令。</summary>
    [Fact]
    public void UninstallCommand_KeepsMsiArgumentsBeyondBareProductCode()
    {
        var command = UninstallCommand.Parse(@"MsiExec.exe /I{1CC6D498-D386-4FB3-8B3A-F00088C626C3} /qb");

        Assert.NotNull(command);
        Assert.Equal("/I{1CC6D498-D386-4FB3-8B3A-F00088C626C3} /qb", command!.Arguments);
        Assert.True(command.IsSilent);
    }

    [Theory]
    [InlineData("20250102", "2025-01-02")]
    [InlineData("2025-01-02", "2025-01-02")]
    [InlineData("nonsense", "nonsense")]
    [InlineData(null, "")]
    public void ParseInstallDate_NormalizesOnlyYyyyMmdd(string? raw, string expected) =>
        Assert.Equal(expected, SoftwareEntryFilter.ParseInstallDate(raw));

    [Fact]
    public void ParseEstimatedSize_ConvertsKilobytesAndGuardsBadValues()
    {
        Assert.Equal(2048 * 1024L, SoftwareEntryFilter.ParseEstimatedSize(2048));
        Assert.Equal(4096 * 1024L, SoftwareEntryFilter.ParseEstimatedSize("4096"));
        Assert.Equal(0, SoftwareEntryFilter.ParseEstimatedSize(-1));
        Assert.Equal(0, SoftwareEntryFilter.ParseEstimatedSize(null));
        Assert.Equal(0, SoftwareEntryFilter.ParseEstimatedSize(new object()));
    }

    [Fact]
    public void SoftwareEntry_MatchesAcrossAllFieldsIgnoringCase()
    {
        var entry = Entry("DiskSweep", publisher: "Tencent",
            location: @"D:\Apps\DiskSweep");

        Assert.True(entry.Matches(""));
        Assert.True(entry.Matches("disksweep"));
        Assert.True(entry.Matches("TENCENT"));
        Assert.True(entry.Matches(@"D:\APPS"));
        Assert.False(entry.Matches("not-installed"));
        Assert.Equal("未登记", entry.SizeText);
        Assert.False(entry.CanUninstall == false);
    }
}
