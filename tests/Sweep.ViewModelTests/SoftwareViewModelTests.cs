using Sweep.App.Services;
using Sweep.App.ViewModels;
using Sweep.Core.Software;
using Xunit;

namespace Sweep.ViewModelTests;

/// <summary>软件管理动作全部通过接口注入：注册表与进程启动在测试里被替换为假实现。</summary>
internal sealed class FakeSoftwareService : ISoftwareService
{
    public List<SoftwareEntry> Entries { get; } = new();
    public long MeasuredBytes { get; set; }
    /// <summary>按目录给出不同体积，用于批量测量用例。</summary>
    public Dictionary<string, long> Sizes { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>按目录给出「测不出来」的状态（目录不存在 / 无权限），用于占用列的状态文案用例。</summary>
    public Dictionary<string, MeasureStatus> Problems { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> MeasuredPaths { get; } = new();
    public int LaunchCount { get; private set; }
    public int SettingsOpened { get; private set; }
    public string? LastLocation { get; private set; }
    public SoftwareEntry? LastUninstallEntry { get; private set; }
    public SoftwareActionResult LaunchResult { get; set; } = new(true, "已启动卸载向导");

    public IReadOnlyList<SoftwareEntry> ListInstalled() => Entries.ToArray();

    public Task<MeasureOutcome> MeasureAsync(string path, CancellationToken ct)
    {
        MeasuredPaths.Add(path);
        string key = path.TrimEnd('\\');
        if (Problems.TryGetValue(key, out var status)) return Task.FromResult(new MeasureOutcome(0, status));
        long value = Sizes.TryGetValue(key, out long size) ? size : MeasuredBytes;
        return Task.FromResult(new MeasureOutcome(value, MeasureStatus.Measured));
    }

    public SoftwareActionResult OpenLocation(string path)
    {
        LastLocation = path;
        return new(true, "已打开安装目录");
    }

    public SoftwareActionResult LaunchUninstall(SoftwareEntry entry)
    {
        LaunchCount++;
        LastUninstallEntry = entry;
        return LaunchResult;
    }

    public SoftwareActionResult OpenAppsAndFeatures()
    {
        SettingsOpened++;
        return new(true, "已打开系统设置");
    }
}

public class SoftwareViewModelTests
{
    private static SoftwareEntry Entry(
        string name,
        string publisher = "Acme",
        string location = @"C:\Program Files\Acme",
        long size = 0,
        string date = "2025-01-02",
        string uninstall = "MsiExec.exe /X{TEST}") =>
        new(name, name, "1.0", publisher, location, date, uninstall, size,
            SoftwareScope.Machine64, false);

    private static (SoftwareViewModel Vm, FakeSoftwareService Service, FakeDialogs Dialogs) Create()
    {
        var service = new FakeSoftwareService();
        var dialogs = new FakeDialogs();
        var vm = new SoftwareViewModel(service, dialogs, new FakeDispatcher());
        return (vm, service, dialogs);
    }

    private static MainViewModel CreateMain(FakeSoftwareService service, FakeDialogs dialogs) =>
        new(new FakeContext(), dialogs, new FakeDispatcher(), 
            new FakeDiskInfo(), new FakeAiCatalog(), new FakeTheme(), service);

    [Fact]
    public async Task LoadAsync_PopulatesSortedListAndSummary()
    {
        var (vm, service, _) = Create();
        service.Entries.Add(Entry("Zeta"));
        service.Entries.Add(Entry("Alpha", size: 4096));
        service.Entries.Add(Entry("Beta", publisher: "Other"));

        await vm.LoadAsync();

        Assert.True(vm.IsLoaded);
        Assert.True(vm.HasItems);
        Assert.Equal(new[] { "Alpha", "Beta", "Zeta" }, vm.Items.Select(i => i.Name));
        Assert.Contains("3 个", vm.SummaryText);
        Assert.Contains("已读取 3 个软件", vm.StatusText);
        Assert.False(vm.IsLoading);
    }

    [Fact]
    public async Task SearchText_FiltersByNamePublisherAndLocation()
    {
        var (vm, service, _) = Create();
        service.Entries.Add(Entry("Sweep", publisher: "Tencent", location: @"D:\Apps\Sweep"));
        service.Entries.Add(Entry("Browser", publisher: "Acme", location: @"C:\Program Files\Browser"));
        await vm.LoadAsync();

        vm.SearchText = "tencent";
        Assert.Single(vm.Items);
        Assert.Equal("Sweep", vm.Items[0].Name);

        vm.SearchText = @"D:\APPS";
        Assert.Single(vm.Items);
        Assert.Equal("Sweep", vm.Items[0].Name);

        vm.SearchText = "missing";
        Assert.Empty(vm.Items);
        Assert.False(vm.HasItems);

        vm.SearchText = "";
        Assert.Equal(2, vm.Items.Count);
    }

    [Fact]
    public async Task MeasuredSize_ChangesSortingAndDisplayWithoutReload()
    {
        var (vm, service, _) = Create();
        service.Entries.Add(Entry("Small", size: 1024));
        service.Entries.Add(Entry("Large", size: 1024 * 1024));
        await vm.LoadAsync();

        var small = vm.Items.Single(i => i.Name == "Small");
        service.MeasuredBytes = 10L * 1024 * 1024;
        vm.SelectedItem = small;
        vm.MeasureSoftwareSizeCommand.Execute(null);

        Assert.Equal(10L * 1024 * 1024, small.MeasuredBytes);
        Assert.Contains("实测", vm.StatusText);

        vm.SortIndex = 1;      // 按占用排序
        Assert.Equal("Small", vm.Items[0].Name);
        Assert.Same(small, vm.Items[0]);
        Assert.Equal(10L * 1024 * 1024, vm.Items[0].MeasuredBytes);
    }

    /// <summary>发布者 / 安装日期 / 来源 / 安装目录只在选中后出现，未选中时详情为空。</summary>
    [Fact]
    public async Task SelectionDetail_AppearsOnlyAfterSelection()
    {
        var (vm, service, _) = Create();
        service.Entries.Add(Entry("Detail App", publisher: "Acme Studio", date: "2026-03-04"));
        await vm.LoadAsync();

        Assert.False(vm.HasSelection);
        Assert.Equal("", vm.SelectionDetailText);
        Assert.Equal("", vm.SelectionLocationText);

        vm.SelectedItem = vm.Items[0];

        Assert.True(vm.HasSelection);
        Assert.Contains("Acme Studio", vm.SelectionDetailText);
        Assert.Contains("安装日期 2026-03-04", vm.SelectionDetailText);
        Assert.Contains("来源", vm.SelectionDetailText);
        Assert.DoesNotContain("版本", vm.SelectionDetailText);   // 名称已含版本，详情不重复
        Assert.Contains(@"C:\Program Files\Acme", vm.SelectionLocationText);
    }

    [Fact]
    public async Task OpenLocation_UsesSelectedInstallationDirectory()
    {
        var (vm, service, _) = Create();
        service.Entries.Add(Entry("Acme", location: @"D:\Tools\Acme"));
        await vm.LoadAsync();

        vm.SelectedItem = vm.Items[0];
        vm.OpenSoftwareLocationCommand.Execute(null);

        Assert.Equal(@"D:\Tools\Acme", service.LastLocation);
        Assert.Contains("已打开安装目录", vm.StatusText);
    }

    [Fact]
    public async Task Uninstall_RequiresExplicitConfirmation()
    {
        var (vm, service, dialogs) = Create();
        service.Entries.Add(Entry("Acme"));
        await vm.LoadAsync();
        vm.SelectedItem = vm.Items[0];

        dialogs.ConfirmResult = false;
        vm.UninstallSelectedCommand.Execute(null);
        Assert.Equal(0, service.LaunchCount);
        Assert.Contains("已取消", vm.StatusText);

        dialogs.ConfirmResult = true;
        vm.UninstallSelectedCommand.Execute(null);
        Assert.Equal(1, service.LaunchCount);
        Assert.Equal("Acme", service.LastUninstallEntry?.Name);
        Assert.Contains("已启动", vm.StatusText);
    }

    [Fact]
    public async Task CommandAvailability_FollowsSelectionAndRegistryData()
    {
        var (vm, service, _) = Create();
        service.Entries.Add(Entry("WithLocation"));
        service.Entries.Add(Entry("Bare", location: "", uninstall: ""));
        await vm.LoadAsync();

        Assert.False(vm.UninstallSelectedCommand.CanExecute(null));
        Assert.False(vm.ScanSoftwareLocationCommand.CanExecute(null));

        vm.SelectedItem = vm.Items.Single(i => i.Name == "Bare");
        Assert.False(vm.UninstallSelectedCommand.CanExecute(null));
        Assert.False(vm.OpenSoftwareLocationCommand.CanExecute(null));

        vm.SelectedItem = vm.Items.Single(i => i.Name == "WithLocation");
        Assert.True(vm.UninstallSelectedCommand.CanExecute(null));
        Assert.True(vm.OpenSoftwareLocationCommand.CanExecute(null));
        Assert.True(vm.MeasureSoftwareSizeCommand.CanExecute(null));
        Assert.True(vm.HasSelection);
    }

    [Fact]
    public async Task ScanLocation_FeedsRootPathBackToMainViewModel()
    {
        var service = new FakeSoftwareService();
        service.Entries.Add(Entry("Acme", location: @"D:\Tools\Acme"));
        var dialogs = new FakeDialogs();
        var main = CreateMain(service, dialogs);

        await main.Software.LoadAsync();
        main.Software.SelectedItem = main.Software.Items[0];
        main.Software.ScanSoftwareLocationCommand.Execute(null);

        Assert.Equal(@"D:\Tools\Acme", main.RootPath);
        Assert.Equal(0, main.ContentTabIndex);
        Assert.Contains("扫描目录已切换", main.StatusText);
    }

    [Fact]
    public void MainViewModel_StillConstructsWithoutInjectedSoftwareService()
    {
        var main = Fixture.CreateMain();

        Assert.NotNull(main.Software);
        Assert.False(main.Software.HasItems);
        Assert.Equal(0, main.ContentTabIndex);
    }

    /// <summary>真实服务对静默卸载命令必须拒绝，且不得走到启动进程那一步。</summary>
    [Fact]
    public void RealSoftwareService_RefusesSilentUninstallCommands()
    {
        var service = new SoftwareService();
        var silent = Entry("Silent", uninstall: "MsiExec.exe /X{ABC} /qn");

        var result = service.LaunchUninstall(silent);

        Assert.False(result.Ok);
        Assert.Contains("静默", result.Message);
    }

    /// <summary>
    /// 没有卸载命令的登记会保留在列表里（JetBrains 等只写登记不写命令），
    /// 筛选要能把它们分出来，而不是让它们混在可卸载项中间。
    /// </summary>
    [Fact]
    public async Task Filter_SeparatesUninstallableFromRegistryOnlyEntries()
    {
        var (vm, service, _) = Create();
        service.Entries.Add(Entry("Runnable"));
        service.Entries.Add(Entry("RegistryOnly", uninstall: ""));
        await vm.LoadAsync();
        Assert.Equal(2, vm.Items.Count);

        vm.FilterIndex = 1;
        Assert.Equal(new[] { "Runnable" }, vm.Items.Select(i => i.Name));

        vm.FilterIndex = 3;
        Assert.Equal(new[] { "RegistryOnly" }, vm.Items.Select(i => i.Name));

        vm.FilterIndex = 0;
        Assert.Equal(2, vm.Items.Count);
    }

    /// <summary>
    /// 「无占用数据」只筛既没实测、也没有登记估值的行。只筛「没实测」会把几十条
    /// 只有估值的行一起算进来——那批是有数字的，混进去反而找不到真正空缺的那几行。
    /// </summary>
    [Fact]
    public async Task Filter_NoSizeData_OnlyShowsRowsWithoutAnyNumber()
    {
        var (vm, service, _) = Create();
        service.Entries.Add(Entry("Measured", location: @"D:\M"));
        service.Entries.Add(Entry("Estimated", location: @"D:\E", size: 2048));
        service.Entries.Add(Entry("Blank", location: ""));
        service.Sizes[@"D:\M"] = 4096;
        await vm.LoadAsync();
        await vm.MeasureAllSizesCommand.ExecuteAsync(null);

        vm.FilterIndex = 4;

        Assert.Equal(new[] { "Blank" }, vm.Items.Select(i => i.Name));
        Assert.Equal("未登记", vm.Items[0].SizeText);
        Assert.Contains("无法测量", vm.Items[0].SizeHint);
    }

    /// <summary>占用的三种「没有数字」状态必须各有各的说法，不能都显示成 0 B 或空白。</summary>
    [Fact]
    public async Task SizeText_DistinguishesMissingDirectoryFromUnmeasured()
    {
        var (vm, service, _) = Create();
        service.Entries.Add(Entry("Gone", location: @"D:\Gone"));
        service.Entries.Add(Entry("Denied", location: @"D:\Denied"));
        service.Entries.Add(Entry("Estimated", location: "", size: 3L * 1024 * 1024));
        service.Problems[@"D:\Gone"] = MeasureStatus.DirectoryMissing;
        service.Problems[@"D:\Denied"] = MeasureStatus.AccessDenied;
        await vm.LoadAsync();
        await vm.MeasureAllSizesCommand.ExecuteAsync(null);

        Assert.Equal("目录不存在", vm.Items.Single(i => i.Name == "Gone").SizeText);
        Assert.Contains("卸载残留", vm.Items.Single(i => i.Name == "Gone").SizeHint);
        Assert.Equal("无权限", vm.Items.Single(i => i.Name == "Denied").SizeText);
        // 没有目录可测的行继续显示登记估值，并且明确标成 ≈ 而不是冒充实测。
        Assert.StartsWith("≈ ", vm.Items.Single(i => i.Name == "Estimated").SizeText);
        Assert.False(vm.Items.Single(i => i.Name == "Estimated").SizeIsMeasured);
        Assert.True(vm.Items.Single(i => i.Name == "Estimated").HasSizeData);
        // 一项都没量成时不报「实测 0 项」，直接说清登记值与空缺各有几项。
        Assert.Contains("登记值 1 项", vm.SummaryText);
        Assert.Contains("无数据 2 项", vm.SummaryText);
        Assert.Contains("目录不存在", vm.StatusText);
        Assert.Contains("无权限", vm.StatusText);
    }

    /// <summary>可升级筛选要跟着 winget 结果走，且不能被空清单清空列表。</summary>
    [Fact]
    public async Task Filter_ByUpgradeUsesResolvedVersions()
    {
        var (vm, service, _) = Create();
        service.Entries.Add(Entry("Old"));
        service.Entries.Add(Entry("New"));
        await vm.LoadAsync();

        vm.Items.Single(i => i.Name == "Old").AvailableVersion = "2.0";
        vm.FilterIndex = 2;

        Assert.Equal(new[] { "Old" }, vm.Items.Select(i => i.Name));
    }

    /// <summary>一次性测完所有有目录的项，并给出可核对的进度与总计。</summary>
    [Fact]
    public async Task MeasureAll_MeasuresEachLocationAndReportsTotals()
    {
        var (vm, service, _) = Create();
        service.Entries.Add(Entry("A", location: @"D:\A", size: 1024));
        service.Entries.Add(Entry("B", location: @"D:\B", size: 1024));
        service.Entries.Add(Entry("C", location: "", size: 1024));   // 没目录，不参与
        service.Sizes[@"D:\A"] = 3L * 1024 * 1024;
        service.Sizes[@"D:\B"] = 5L * 1024 * 1024;
        await vm.LoadAsync();

        // 列表加载完就必须可用：CanExecute 会读 IsLoading，通知早于 IsLoading 复位曾让按钮一直禁用。
        Assert.True(vm.MeasureAllSizesCommand.CanExecute(null));

        await vm.MeasureAllSizesCommand.ExecuteAsync(null);

        Assert.Equal(2, service.MeasuredPaths.Count);
        Assert.DoesNotContain(@"D:\C", service.MeasuredPaths);
        Assert.True(vm.Items.Single(i => i.Name == "A").SizeIsMeasured);
        Assert.Contains("批量测量完成", vm.StatusText);
        // 合计口径必须把「实测」与「登记值」分开写：混成一个数会让人以为总量已经量准了。
        Assert.Contains("实测 2 项", vm.SummaryText);
        Assert.Contains("登记值 1 项", vm.SummaryText);
        Assert.False(vm.IsMeasuringAll);
    }

    /// <summary>刷新列表不应该把已经花时间测出来的体积丢掉——缓存按安装目录复用。</summary>
    [Fact]
    public async Task MeasureAll_ReusesCacheAfterReload()    {
        var (vm, service, _) = Create();
        service.Entries.Add(Entry("Cached", location: @"D:\Cached"));
        service.Sizes[@"D:\Cached"] = 7L * 1024 * 1024;
        await vm.LoadAsync();
        await vm.MeasureAllSizesCommand.ExecuteAsync(null);
        Assert.Single(service.MeasuredPaths);

        await vm.LoadAsync();

        Assert.Single(service.MeasuredPaths);                       // 没有重复测量
        Assert.True(vm.Items[0].SizeIsMeasured);
        Assert.Contains("复用 1 项已测体积", vm.StatusText);
    }

    /// <summary>批量测量里已有缓存的项直接跳过，不再走盘。</summary>
    [Fact]
    public async Task MeasureAll_SkipsItemsAlreadyMeasuredIndividually()
    {
        var (vm, service, _) = Create();
        service.Entries.Add(Entry("First", location: @"D:\First"));
        service.Entries.Add(Entry("Second", location: @"D:\Second"));
        service.Sizes[@"D:\First"] = 1024;
        service.Sizes[@"D:\Second"] = 2048;
        await vm.LoadAsync();

        vm.SelectedItem = vm.Items.Single(i => i.Name == "First");
        vm.MeasureSoftwareSizeCommand.Execute(null);
        Assert.Single(service.MeasuredPaths);

        await vm.MeasureAllSizesCommand.ExecuteAsync(null);

        Assert.Equal(2, service.MeasuredPaths.Count);               // 只补测了 Second
        Assert.Equal(@"D:\Second", service.MeasuredPaths[1]);
    }

    /// <summary>
    /// 进入页面时自动补测：注册表里大量登记没有体积，一列「未登记」会让人以为功能坏了。
    /// 只补测「还没量过的目录」，第二次进来直接命中缓存，不再走盘。
    /// </summary>
    [Fact]
    public async Task EnsureLoaded_AutoMeasuresUnmeasuredLocationsOnlyOnce()
    {
        var (vm, service, _) = Create();
        service.Entries.Add(Entry("A", location: @"D:\A"));
        service.Entries.Add(Entry("B", location: @"D:\B"));
        service.Entries.Add(Entry("NoPath", location: ""));
        service.Sizes[@"D:\A"] = 1024;
        service.Sizes[@"D:\B"] = 2048;

        await vm.EnsureLoadedAsync();

        Assert.Equal(2, service.MeasuredPaths.Count);              // 没有目录的那行不参与
        Assert.All(vm.Items.Where(i => i.HasLocation), i => Assert.True(i.SizeIsMeasured));
        Assert.Contains("实测 2 项", vm.SummaryText);

        await vm.EnsureLoadedAsync();                              // 再进一次不重复走盘
        Assert.Equal(2, service.MeasuredPaths.Count);
    }

    /// <summary>刷新列表只重读注册表；自动补测是「进入页面」的行为，不该藏在刷新按钮里。</summary>
    [Fact]
    public async Task RefreshList_DoesNotStartMeasuring()
    {
        var (vm, service, _) = Create();
        service.Entries.Add(Entry("A", location: @"D:\A"));

        await vm.LoadAsync();

        Assert.Empty(service.MeasuredPaths);
        Assert.Contains("已读取", vm.StatusText);
    }

    /// <summary>不可卸载时必须说清原因，而不是只给一个灰掉的按钮。</summary>
    [Fact]
    public async Task SelectionHint_ExplainsWhyUninstallIsDisabled()
    {
        var (vm, service, _) = Create();
        service.Entries.Add(Entry("NoCommand", uninstall: ""));
        service.Entries.Add(Entry("VendorBlocked", uninstall: "MsiExec.exe /X{A}")
            with { NoRemove = true });
        await vm.LoadAsync();

        vm.SelectedItem = vm.Items.Single(i => i.Name == "NoCommand");
        Assert.True(vm.HasSelectionHint);
        Assert.Contains("没有卸载命令", vm.SelectionHintText);
        Assert.Contains("没有卸载命令", vm.UninstallToolTip);

        vm.SelectedItem = vm.Items.Single(i => i.Name == "VendorBlocked");
        Assert.Contains("NoRemove", vm.SelectionHintText);

        vm.SelectedItem = vm.Items.Single(i => i.Name == "NoCommand");
        vm.SelectedItem = null;
        Assert.False(vm.HasSelectionHint);
    }

    /// <summary>确认框里要显示真正会执行的命令：msiexec 的维护参数已规范化为卸载参数。</summary>
    [Fact]
    public async Task Uninstall_ConfirmationShowsNormalizedMsiCommand()
    {
        var (vm, service, dialogs) = Create();
        service.Entries.Add(Entry("Msi", uninstall: @"MsiExec.exe /I{1CC6D498-D386-4FB3-8B3A-F00088C626C3}"));
        await vm.LoadAsync();
        vm.SelectedItem = vm.Items[0];

        vm.UninstallSelectedCommand.Execute(null);

        string text = dialogs.LastConfirmMessage ?? "";
        Assert.Contains("/X{1CC6D498-D386-4FB3-8B3A-F00088C626C3}", text);
        Assert.DoesNotContain("/I{1CC6D498", text);
    }

    /// <summary>推断出来的安装目录也要能测量、能打开，否则 58% 的项永远测不了体积。</summary>
    [Fact]
    public async Task DerivedLocation_IsUsedForMeasuringAndOpening()
    {
        var (vm, service, _) = Create();
        service.Entries.Add(Entry("Derived", location: "")
            with { DiscoveredLocation = @"C:\Program Files (x86)\Vendor\App" });
        service.Sizes[@"C:\Program Files (x86)\Vendor\App"] = 4096;
        await vm.LoadAsync();

        var item = vm.Items[0];
        Assert.True(item.HasLocation);
        Assert.Contains("据图标推断", item.Location);

        vm.SelectedItem = item;
        vm.MeasureSoftwareSizeCommand.Execute(null);
        Assert.Equal(@"C:\Program Files (x86)\Vendor\App", service.MeasuredPaths[0]);

        vm.OpenSoftwareLocationCommand.Execute(null);
        Assert.Equal(@"C:\Program Files (x86)\Vendor\App", service.LastLocation);
    }

    /// <summary>
    /// 选中变化必须通知「扫描残留」重新求值：它读 SelectedItem，漏掉通知时按钮从启动到关闭一直是灰的，
    /// 强制删除入口等于不存在（GUI 冒烟实测发现）。
    /// </summary>
    [Fact]
    public async Task ScanForceRemoval_EnablesOnlyAfterSelection()
    {
        var service = new FakeSoftwareService();
        service.Entries.Add(Entry("Acme", location: @"D:\Tools\Acme"));
        var forceRemoval = new FakeForceRemovalService();
        var vm = new SoftwareViewModel(service, new FakeDialogs(), new FakeDispatcher(), 
            forceRemoval: forceRemoval);
        await vm.LoadAsync();

        Assert.False(vm.ScanForceRemovalCommand.CanExecute(null));

        vm.SelectedItem = vm.Items[0];
        Assert.True(vm.ScanForceRemovalCommand.CanExecute(null));

        vm.SelectedItem = null;
        Assert.False(vm.ScanForceRemovalCommand.CanExecute(null));
    }
}
