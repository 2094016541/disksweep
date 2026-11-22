using DiskSweep.App.ViewModels;
using DiskSweep.Core;
using DiskSweep.Core.Recommendation;
using Xunit;

namespace DiskSweep.ViewModelTests;

/// <summary>
/// 清理清单的交互契约：分组内按体积降序、行内「打开位置 / 加入白名单」必须真的落到
/// 注入的服务与设置上（而不是画个按钮），失败时不能留下半截状态。
/// </summary>
public class CleanupInteractionTests
{
    [Fact]
    public void CategoryGroup_OrdersItemsBySizeDescending()
    {
        var group = new CategoryGroup("临时文件", new[]
        {
            Fixture.Plan(@"C:\t\small.tmp", size: 1_000),
            Fixture.Plan(@"C:\t\big.tmp", size: 9_000_000),
            Fixture.Plan(@"C:\t\mid.tmp", size: 20_000)
        }, highRisk: false);

        Assert.Equal(
            new[] { @"C:\t\big.tmp", @"C:\t\mid.tmp", @"C:\t\small.tmp" },
            group.Items.Select(i => i.Path).ToArray());
        Assert.True(group.CanSelectAll);
    }

    [Fact]
    public void Whitelist_AddsPatternPersistsAndRemovesRow()
    {
        var context = new FakeContext();
        var dialogs = new FakeDialogs();
        var vm = Fixture.CreateMain(context, dialogs);
        var item = Fixture.Plan(@"C:\Users\me\AppData\Local\Temp\a.tmp");
        vm.SetPlan(new[] { item, Fixture.Plan(@"C:\Users\me\AppData\Local\Temp\b.tmp") });

        var group = vm.Groups.Single();
        group.WhitelistCommand.Execute(item);

        Assert.Contains(item.Path, context.Settings.Whitelist);
        Assert.Equal(1, context.SaveCount);
        Assert.Single(group.Items);
        Assert.DoesNotContain(group.Items, i => i.Path == item.Path);
        Assert.Empty(dialogs.Messages);                    // 成功路径不弹窗，只在状态栏说话
        Assert.Contains("已加入白名单", vm.StatusText);
    }

    [Fact]
    public void Whitelist_ForDirectory_UsesRecursivePattern()
    {
        var context = new FakeContext();
        var vm = Fixture.CreateMain(context);
        var dir = Fixture.Plan(@"C:\Users\me\AppData\Local\Temp\cache",
            category: "空目录", action: RecommendationAction.Review, size: 0, isDirectory: true);
        vm.SetPlan(new[] { dir });

        vm.Groups.Single().WhitelistCommand.Execute(dir);

        Assert.Contains(@"C:\Users\me\AppData\Local\Temp\cache\**", context.Settings.Whitelist);
        Assert.Empty(vm.Groups);                             // 唯一一项被移除后分组也消失
    }

    [Fact]
    public void Whitelist_SaveFailure_RollsBackAndKeepsRow()
    {
        var context = new FakeContext { SaveThrows = true };
        var dialogs = new FakeDialogs();
        var vm = Fixture.CreateMain(context, dialogs);
        var item = Fixture.Plan(@"C:\Users\me\AppData\Local\Temp\a.tmp");
        vm.SetPlan(new[] { item });

        vm.Groups.Single().WhitelistCommand.Execute(item);

        Assert.Empty(context.Settings.Whitelist);             // 回滚，不留半截白名单
        Assert.Single(vm.Groups.Single().Items);
        Assert.Contains(dialogs.Messages, m => m.Contains("白名单保存失败"));
    }

    [Fact]
    public void OpenLocation_GoesThroughInjectedLocator()
    {
        var locator = new FakeShellLocator();
        var vm = Fixture.CreateMain(locator: locator);
        var item = Fixture.Plan(@"C:\Windows\Temp\x.log");
        vm.SetPlan(new[] { item });

        vm.Groups.Single().OpenLocationCommand.Execute(item);

        Assert.Equal(new[] { item.Path }, locator.Opened);
    }

    [Fact]
    public void OpenLocation_Failure_ReportsInsteadOfSilentNoOp()
    {
        var locator = new FakeShellLocator { Result = false };
        var dialogs = new FakeDialogs();
        var vm = Fixture.CreateMain(dialogs: dialogs, locator: locator);
        var item = Fixture.Plan(@"C:\Windows\Temp\gone.log");
        vm.SetPlan(new[] { item });

        vm.Groups.Single().OpenLocationCommand.Execute(item);

        Assert.Contains(dialogs.Messages, m => m.Contains("无法打开位置"));
    }

    /// <summary>
    /// 「系统维护」分组里是清空更新缓存、重启服务这类动作，没有可统计字节。
    /// 分组头若照常显示 0 B，用户会以为这一组是空的；因此整组系统接口项时改用
    /// 「需管理员 · 不可回退」标签，并保持全选置灰（系统操作必须逐项确认）。
    /// </summary>
    [Fact]
    public void SystemOperationGroup_ReplacesZeroBytesWithAdminLabel()
    {
        var vm = Fixture.CreateMain();
        vm.SetPlan(new[]
        {
            Fixture.Plan(@"C:\Temp\a.tmp", size: 700_000),
            Fixture.Plan(@"C:\Windows\SoftwareDistribution\Download",
                category: "系统维护（不可回退）", size: 0, isSystem: true)
        });

        var files = vm.Groups.Single(g => g.Name == "临时文件");
        var system = vm.Groups.Single(g => g.Name == "系统维护（不可回退）");

        Assert.False(files.IsSystemGroup);
        Assert.Equal("683.6 KB", files.TotalText);

        Assert.True(system.IsSystemGroup);
        Assert.Equal("0 B", system.TotalText);
        Assert.False(system.CanSelectAll);
    }
}
