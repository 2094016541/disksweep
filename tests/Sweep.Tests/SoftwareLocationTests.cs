using Sweep.Core.Software;
using Xunit;

namespace Sweep.Tests;

/// <summary>
/// 安装目录推断：注册值缺失时从 DisplayIcon / 卸载程序目录找回，但绝不把系统目录、
/// 共享目录或盘符根当成某个软件的安装目录。存在性用委托注入，测试不依赖真实磁盘。
/// </summary>
public class SoftwareLocationTests
{
    private static readonly SoftwarePathGuard Guard = new(
        Trees: new[] { @"C:\Windows", @"C:\ProgramData", @"C:\Temp" },
        Exact: new[] { @"C:\Program Files", @"C:\Users\me\AppData\Local" });

    private static (Func<string, bool> File, Func<string, bool> Dir) Disk(
        IEnumerable<string> files, IEnumerable<string> directories)
    {
        var fileSet = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
        var dirSet = new HashSet<string>(directories, StringComparer.OrdinalIgnoreCase);
        return (path => fileSet.Contains(path), path => dirSet.Contains(path));
    }

    [Fact]
    public void Resolve_PrefersRegisteredLocation()
    {
        var (file, dir) = Disk(
            new[] { @"D:\Apps\Real\app.exe" },
            new[] { @"D:\Apps\Real", @"D:\Apps\Icon" });

        string resolved = SoftwareLocation.Resolve(
            @"D:\Apps\Real", @"D:\Apps\Icon\app.exe,0", @"D:\Apps\Real\uninst.exe", file, dir, Guard);

        Assert.Equal(@"D:\Apps\Real", resolved);
    }

    /// <summary>本机最常见的形态：InstallLocation 为空，只有 DisplayIcon。</summary>
    [Fact]
    public void Resolve_FallsBackToDisplayIconDirectory()
    {
        var (file, dir) = Disk(
            new[] { @"C:\Program Files (x86)\VMware\VMware Workstation\vmware.exe" },
            new[] { @"C:\Program Files (x86)\VMware\VMware Workstation" });

        string resolved = SoftwareLocation.Resolve(
            "", @"C:\Program Files (x86)\VMware\VMware Workstation\vmware.exe,0", "", file, dir, Guard);

        Assert.Equal(@"C:\Program Files (x86)\VMware\VMware Workstation", resolved);
    }

    [Fact]
    public void Resolve_FallsBackToUninstallerDirectory()
    {
        var (file, dir) = Disk(
            new[] { @"D:\Tools\Foo\uninstall.exe" },
            new[] { @"D:\Tools\Foo" });

        string resolved = SoftwareLocation.Resolve("", "", @"""D:\Tools\Foo\uninstall.exe"" /S", file, dir, Guard);

        Assert.Equal(@"D:\Tools\Foo", resolved);
    }

    /// <summary>系统目录即使存在也不能当成安装目录。</summary>
    [Theory]
    [InlineData(@"C:\Windows\System32\notepad.exe", @"C:\Windows\System32")]
    [InlineData(@"C:\ProgramData\Vendor\app.exe", @"C:\ProgramData\Vendor")]
    [InlineData(@"C:\Temp\setup\app.exe", @"C:\Temp\setup")]
    public void Resolve_RejectsSystemAndSharedDirectories(string icon, string iconDirectory)
    {
        var (file, dir) = Disk(new[] { icon }, new[] { iconDirectory });

        string resolved = SoftwareLocation.Resolve("", icon, "", file, dir, Guard);

        Assert.Equal("", resolved);
    }

    [Fact]
    public void Resolve_RejectsContainerDirectoriesThemselves()
    {
        var (file, dir) = Disk(
            new[] { @"C:\Program Files\app.exe" },
            new[] { @"C:\Program Files" });

        string resolved = SoftwareLocation.Resolve("", @"C:\Program Files\app.exe", "", file, dir, Guard);

        Assert.Equal("", resolved);
    }

    /// <summary>但 Program Files 的子目录是正常安装位置，Program Files 自身才要排除。</summary>
    [Fact]
    public void Resolve_AllowsSubdirectoryOfProtectedContainer()
    {
        var (file, dir) = Disk(
            new[] { @"C:\Program Files\Acme\acme.exe" },
            new[] { @"C:\Program Files\Acme" });

        string resolved = SoftwareLocation.Resolve("", @"C:\Program Files\Acme\acme.exe", "", file, dir, Guard);

        Assert.Equal(@"C:\Program Files\Acme", resolved);
    }

    /// <summary>VS Code / Teams 这类装到 %LOCALAPPDATA%\Programs 的软件必须能找回来。</summary>
    [Fact]
    public void Resolve_AllowsLocalAppDataPrograms()
    {
        var (file, dir) = Disk(
            new[] { @"C:\Users\me\AppData\Local\Programs\Microsoft VS Code\Code.exe" },
            new[] { @"C:\Users\me\AppData\Local\Programs\Microsoft VS Code" });

        string resolved = SoftwareLocation.Resolve(
            "", @"C:\Users\me\AppData\Local\Programs\Microsoft VS Code\Code.exe", "", file, dir, Guard);

        Assert.Equal(@"C:\Users\me\AppData\Local\Programs\Microsoft VS Code", resolved);
    }

    [Fact]
    public void Resolve_DropsTrailingIconIndex()
    {
        Assert.Equal(@"D:\App\a.exe", SoftwareLocation.StripIconIndex(@"D:\App\a.exe,0"));
        Assert.Equal(@"D:\App\a.exe", SoftwareLocation.StripIconIndex(@"D:\App\a.exe, 12"));
        // 不是纯数字的尾巴不能当索引切掉（含逗号的真实路径不能被截断）
        Assert.Equal(@"D:\App\a,b.exe", SoftwareLocation.StripIconIndex(@"D:\App\a,b.exe"));
        Assert.Equal(@"D:\App\a.exe,-1", SoftwareLocation.StripIconIndex(@"D:\App\a.exe,-1"));
    }

    /// <summary>引导目录不是安装目录：量到的是引导器体积，比「未登记」更容易误导。</summary>
    [Fact]
    public void Resolve_RejectsBootstrapperDirectories()
    {
        const string dir = @"C:\Program Files\Microsoft SQL Server\170\Setup Bootstrap\SQL2025\x64";
        var (file, dirExists) = Disk(new[] { dir + @"\SetupARP.exe" }, new[] { dir });

        string resolved = SoftwareLocation.Resolve("", dir + @"\SetupARP.exe", "", file, dirExists, Guard);

        Assert.Equal("", resolved);
    }

    [Fact]
    public void Resolve_ReturnsEmptyWhenNothingExists()
    {
        var (file, dir) = Disk(Array.Empty<string>(), Array.Empty<string>());

        string resolved = SoftwareLocation.Resolve(
            @"D:\Gone", @"D:\Gone\app.exe,0", @"D:\Gone\uninst.exe", file, dir, Guard);

        Assert.Equal("", resolved);
    }

    /// <summary>推断值只用于测量/打开位置，界面要标明来源是推断，避免当成厂商登记值。</summary>
    [Fact]
    public void Entry_UsesDiscoveredLocationButMarksItAsDerived()
    {
        var entry = new SoftwareEntry(
            "Machine64:x", "VMware", "16.2", "VMware, Inc.", "", "2022-04-15",
            "", 1115982 * 1024, SoftwareScope.Machine64, false,
            DisplayIcon: @"C:\Program Files (x86)\VMware\vmware.exe,0",
            DiscoveredLocation: @"C:\Program Files (x86)\VMware");

        Assert.True(entry.HasLocation);
        Assert.True(entry.LocationIsDerived);
        Assert.Equal(@"C:\Program Files (x86)\VMware", entry.EffectiveLocation);
        Assert.Contains("据图标推断", entry.LocationText);
        // 卸载残留判定只看注册值，推断值不参与
        Assert.Equal("", entry.InstallLocation);
    }
}
