using DiskSweep.Core.Software;
using Xunit;

namespace DiskSweep.Tests;

/// <summary>
/// 强制删除安全判定：Geek 式扫描全关联必须建立在保守判定上。
/// 重点是负向断言——系统级软件、共享运行时、安全区外、无关联证据一律不许删。
/// </summary>
public class ForceRemovalClassifierTests
{
    private static readonly string[] SafeRoots =
    {
        @"C:\Program Files", @"C:\Program Files (x86)", @"C:\ProgramData",
        @"C:\Users\alice\AppData\Local", @"C:\Users\alice\AppData\Roaming", @"C:\Users\alice\AppData\Local\Temp"
    };

    [Theory]
    [InlineData("Microsoft Edge")]
    [InlineData("Windows Defender")]
    [InlineData("Intel Graphics Driver")]
    [InlineData("NVIDIA GeForce Experience")]
    [InlineData("AMD Radeon Software")]
    [InlineData("Realtek Audio Driver")]
    [InlineData("Microsoft Visual C++ 2015")]
    [InlineData(".NET Framework")]
    [InlineData("")]
    public void SystemLevelSoftware_NeverAllowed(string name)
    {
        Assert.True(ForceRemovalClassifier.IsSystemLevel(name));
        var verdict = ForceRemovalClassifier.Evaluate(
            @"C:\Program Files\Whatever", ForceRemovalKind.Directory, name, null, @"C:\Program Files\Whatever", SafeRoots);
        Assert.False(verdict.CanDelete);
        Assert.Contains("系统级", verdict.Reason);
    }

    [Fact]
    public void OrdinarySoftware_NotMistakenForSystemLevel()
    {
        Assert.False(ForceRemovalClassifier.IsSystemLevel("Microwave Studio"));  // 不能因 Micro 误伤
        Assert.False(ForceRemovalClassifier.IsSystemLevel("ArmCraft Editor"));     // ARM 必须整词
        Assert.False(ForceRemovalClassifier.IsSystemLevel("Foobar 2024"));
    }

    [Fact]
    public void InstallLocation_InsideSafeRoot_Allowed()
    {
        var v = ForceRemovalClassifier.Evaluate(
            @"C:\Program Files\Foobar", ForceRemovalKind.Directory, "Foobar", "Foo Inc",
            @"C:\Program Files\Foobar", SafeRoots);
        Assert.True(v.CanDelete);
        Assert.Contains("安装目录", v.Reason);
    }

    [Fact]
    public void OutsideSafeRoot_NeverAllowed()
    {
        // Windows 系统目录、盘根、其它用户目录都不在白名单
        foreach (var path in new[] { @"C:\Windows\System32", @"D:\SomeApp", @"C:\Users\bob\AppData\Local\X" })
        {
            var v = ForceRemovalClassifier.Evaluate(
                path, ForceRemovalKind.Directory, "Foobar", null, null, SafeRoots);
            Assert.False(v.CanDelete);
            Assert.Contains("安全目录", v.Reason);
        }
    }

    [Fact]
    public void SharedRuntimeSegment_NeverAllowed()
    {
        var v = ForceRemovalClassifier.Evaluate(
            @"C:\Program Files\Common Files\Foobar", ForceRemovalKind.Directory, "Foobar", null, null, SafeRoots);
        Assert.False(v.CanDelete);
        Assert.Contains("共享运行时", v.Reason);
    }

    [Fact]
    public void NameRelatedDirectory_Allowed_BothDirections()
    {
        // 目录名含软件名
        var a = ForceRemovalClassifier.Evaluate(
            @"C:\Users\alice\AppData\Local\FoobarCache", ForceRemovalKind.AppData, "Foobar", null, null, SafeRoots);
        Assert.True(a.CanDelete);
        // 软件名含目录名（去空格后相等）
        var b = ForceRemovalClassifier.Evaluate(
            @"C:\Users\alice\AppData\Roaming\FooBar", ForceRemovalKind.AppData, "Foo Bar Pro", null, null, SafeRoots);
        Assert.True(b.CanDelete);
    }

    [Fact]
    public void UnrelatedDirectory_NeverAllowed()
    {
        var v = ForceRemovalClassifier.Evaluate(
            @"C:\Program Files\TotallyUnrelated", ForceRemovalKind.Directory, "Foobar", "Foo Inc", null, SafeRoots);
        Assert.False(v.CanDelete);
        Assert.Contains("关联证据", v.Reason);
    }

    [Fact]
    public void OwnRegistryKey_Allowed_ButSystemLevelRegistryRejected()
    {
        var v = ForceRemovalClassifier.Evaluate(
            @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Foobar",
            ForceRemovalKind.Registry, "Foobar", null, null, SafeRoots);
        Assert.True(v.CanDelete);   // 注册表键不走磁盘安全根判定，服务层强制先备份

        var sys = ForceRemovalClassifier.Evaluate(
            @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\WindowsDefender",
            ForceRemovalKind.Registry, "Windows Defender", null, null, SafeRoots);
        Assert.False(sys.CanDelete);
        Assert.Contains("系统级", sys.Reason);
    }
}
