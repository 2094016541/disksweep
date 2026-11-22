using DiskSweep.Core.Software;
using Xunit;

namespace DiskSweep.Tests;

/// <summary>
/// 失效残留判定的安全红线：只有卸载器与安装目录都已不存在的登记才判失效；
/// 任何仍存活的证据（卸载器在、目录在、系统命令、系统组件、证据不足）都必须保留。
/// </summary>
public class UninstallResidueTests
{
    private static SoftwareEntry Entry(
        string? uninstall = @"C:\Program Files\Foo\unins000.exe",
        string? location = @"C:\Program Files\Foo",
        bool systemComponent = false) => new(
        "Machine64:Foo", "Foo 1.0", "1.0", "Acme", location ?? "", "20260101",
        uninstall ?? "", 0, SoftwareScope.Machine64, systemComponent);

    [Fact]
    public void BothUninstallerAndLocationGone_IsOrphan()
    {
        var verdict = UninstallResidue.Evaluate(Entry(), _ => false, _ => false);
        Assert.True(verdict.IsOrphan);
        Assert.Contains("均已不存在", verdict.Reason);
    }

    [Fact]
    public void UninstallerStillPresent_NeverOrphan()
    {
        var verdict = UninstallResidue.Evaluate(Entry(), _ => true, _ => false);
        Assert.False(verdict.IsOrphan);
        Assert.Contains("卸载程序仍存在", verdict.Reason);
    }

    [Fact]
    public void InstallDirectoryStillPresent_NeverOrphan()
    {
        var verdict = UninstallResidue.Evaluate(Entry(), _ => false, _ => true);
        Assert.False(verdict.IsOrphan);
        Assert.Contains("安装目录仍在", verdict.Reason);
    }

    [Fact]
    public void MissingUninstallerButNoRegisteredLocation_InsufficientEvidence_Kept()
    {
        var verdict = UninstallResidue.Evaluate(Entry(location: null), _ => false, _ => false);
        Assert.False(verdict.IsOrphan);
        Assert.Contains("证据不足", verdict.Reason);
    }

    [Fact]
    public void SystemComponent_NeverOrphan()
    {
        var verdict = UninstallResidue.Evaluate(Entry(systemComponent: true), _ => false, _ => false);
        Assert.False(verdict.IsOrphan);
    }

    [Theory]
    [InlineData("msiexec.exe /x {GUID}")]
    [InlineData("rundll32.exe foo.dll,Uninstall")]
    public void SystemCommandNeverOrphan_EvenIfNothingExists(string uninstall)
    {
        var verdict = UninstallResidue.Evaluate(Entry(uninstall: uninstall), _ => false, _ => false);
        Assert.False(verdict.IsOrphan);
        Assert.Contains("系统安装服务", verdict.Reason);
    }

    [Fact]
    public void NoUninstallString_NeverOrphan()
    {
        var verdict = UninstallResidue.Evaluate(Entry(uninstall: ""), _ => false, _ => false);
        Assert.False(verdict.IsOrphan);
    }

    [Fact]
    public void UnquotedPathWithSpaces_IsParsedAndEvaluated()
    {
        var entry = Entry(uninstall: @"C:\Program Files\Foo\unins000.exe /SILENT", location: @"C:\Program Files\Foo");
        // 静默参数不影响“是否失效”判定；只看卸载器与目录是否都不存在
        var verdict = UninstallResidue.Evaluate(entry, _ => false, _ => false);
        Assert.True(verdict.IsOrphan);
        Assert.Equal(@"C:\Program Files\Foo\unins000.exe", verdict.UninstallerPath);
    }
}
