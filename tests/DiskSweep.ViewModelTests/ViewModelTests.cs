using System.IO;
using DiskSweep.App.ViewModels;
using DiskSweep.Core;
using DiskSweep.Core.Executors;
using DiskSweep.Core.Recommendation;
using Xunit;

namespace DiskSweep.ViewModelTests;

/// <summary>
/// 这些测试全程没有 Application / Window / Dispatcher：ViewModel 通过接口注入即可驱动，
/// 这正是补齐 MVVM 后新增的可测能力（重构前交互逻辑锁在 MainWindow.xaml.cs 的 Click 里）。
/// </summary>
public class ViewModelTests
{
    [Fact]
    public void MainViewModel_ScanningState_DrivesVisibilityFlagsAndCommandAvailability()
    {
        var vm = Fixture.CreateMain();
        bool readyNotified = false;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.IsReady)) readyNotified = true; };

        Assert.True(vm.IsReady);
        Assert.True(vm.StartScanCommand.CanExecute(null));
        Assert.False(vm.CancelScanCommand.CanExecute(null));

        vm.IsScanning = true;

        Assert.True(readyNotified, "IsReady 必须随 IsScanning 收到属性变更通知");
        Assert.False(vm.IsReady);
        Assert.False(vm.StartScanCommand.CanExecute(null));
        Assert.True(vm.CancelScanCommand.CanExecute(null));
    }

    [Fact]
    public void MainViewModel_Initialize_PopulatesDiskAndAiStatusFromInjectedServices()
    {
        var vm = Fixture.CreateMain();
        vm.InitializeCommand.Execute(null);

        Assert.Equal(60, vm.UsedPercent);              // FakeDiskInfo: total 100 / free 40
        Assert.Contains("磁盘", vm.DriveLabel);
        Assert.Equal("AI 未启用（测试）", vm.AiStatusText);
    }

    [Fact]
    public void MainViewModel_SelectLowRisk_LeavesHighRiskAndSystemGroupsUnselected()
    {
        var vm = Fixture.CreateMain();
        var low = Fixture.Plan(@"C:\Temp\a.tmp", category: "临时文件", risk: RiskLevel.Low);
        var high = Fixture.Plan(@"C:\Windows\old", category: "系统维护",
            risk: RiskLevel.High, isSystem: true);
        vm.SetPlan(new[] { low, high });
        Assert.Equal(2, vm.Groups.Count);

        vm.SelectLowRiskCommand.Execute(null);

        Assert.True(low.IsSelected);
        Assert.False(high.IsSelected, "高风险 / 系统维护组不得被「全选低风险」勾选");
        Assert.Equal(low.AllocationSize, vm.EstimatedReclaimBytes);
        Assert.Equal("1 项", vm.SelectedCountText);
    }

    [Fact]
    public void CategoryGroup_ToggleGroupCommand_NeverSelectsKeepOrCritical()
    {
        var keep = Fixture.Plan(@"C:\Temp\keep.tmp", action: RecommendationAction.Keep);
        var critical = Fixture.Plan(@"C:\Temp\critical.tmp", risk: RiskLevel.Critical);
        var normal = Fixture.Plan(@"C:\Temp\normal.tmp");
        var group = new CategoryGroup("测试组", new[] { keep, critical, normal }, false);
        keep.IsSelected = critical.IsSelected = normal.IsSelected = false;

        group.ToggleGroupCommand.Execute((bool?)true);

        Assert.False(keep.IsSelected);
        Assert.False(critical.IsSelected);
        Assert.True(normal.IsSelected);
        Assert.Equal(1, group.SelectedCount);
    }

    [Fact]
    public void CategoryGroup_HeaderCheckbox_TracksTriStateWhenItemsChange()
    {
        var a = Fixture.Plan(@"C:\Temp\a.tmp");
        var b = Fixture.Plan(@"C:\Temp\b.tmp");
        var group = new CategoryGroup("测试组", new[] { a, b }, false);
        a.IsSelected = b.IsSelected = false;

        Assert.False(group.AllSelected);
        a.IsSelected = true;
        Assert.True(group.AllSelected is null, "部分选中时分组头应为不确定态");
        b.IsSelected = true;
        Assert.True(group.AllSelected, "全部选中时分组头应为勾选态");

        group.ToggleGroupCommand.Execute((bool?)false);
        Assert.False(group.AllSelected);
        Assert.Equal(0, group.SelectedCount);
    }

    [Fact]
    public void MainViewModel_SelectLowRisk_UpdatesEachGroupHeaderState()
    {
        var vm = Fixture.CreateMain();
        vm.SetPlan(new[]
        {
            Fixture.Plan(@"C:\Temp\a.tmp", category: "临时文件", risk: RiskLevel.Low),
            Fixture.Plan(@"C:\Windows\old", category: "系统维护", risk: RiskLevel.High, isSystem: true)
        });

        vm.SelectLowRiskCommand.Execute(null);

        var low = vm.Groups.Single(g => g.Name == "临时文件");
        var high = vm.Groups.Single(g => g.Name == "系统维护");
        Assert.True(low.AllSelected, "低风险组全选后分组头必须显示勾选");
        Assert.False(high.AllSelected, "高风险组未被勾选，分组头保持未勾选");
    }

    /// <summary>分组头的全选直接写在 AllSelected 上（不再依赖命令），写入必须级联到每一项。</summary>
    [Fact]
    public void CategoryGroup_HeaderCheckboxWrite_CascadesToItemsButSkipsCritical()
    {
        var normal = Fixture.Plan(@"C:\Temp\normal.tmp");
        var critical = Fixture.Plan(@"C:\Temp\critical.tmp", risk: RiskLevel.Critical);
        var group = new CategoryGroup("测试组", new[] { normal, critical }, false);
        normal.IsSelected = critical.IsSelected = false;

        group.AllSelected = true;

        Assert.True(normal.IsSelected);
        Assert.False(critical.IsSelected, "CRITICAL 项永远不能被分组全选打开");
        // 三态只统计「可批量勾选」的项：选不中的 CRITICAL 项不该让组头停在半选
        Assert.True(group.AllSelected, "可勾选项已全部选上，组头应显示勾选");
    }

    /// <summary>
    /// 整组都是受保护（Keep）项时，全选必须置灰（CanSelectAll=false）：
    /// 桌面 / 文档这类受保护目录扫出来的「其他」分类就是这种情况，点全选不会有任何反应。
    /// </summary>
    [Fact]
    public void CategoryGroup_ProtectedOnlyGroup_DisablesSelectAll()
    {
        var keep = Fixture.Plan(@"C:\Users\wang\Desktop\a.dat", category: "其他",
            risk: RiskLevel.Medium, action: RecommendationAction.Keep);
        var group = new CategoryGroup("其他", new[] { keep }, false);
        keep.IsSelected = false;

        Assert.False(group.CanSelectAll);
        group.AllSelected = true;
        Assert.False(keep.IsSelected, "受保护项不能被批量勾选");
        Assert.False(group.AllSelected);
    }

    /// <summary>顶部全局全选：一次写到底，同时仍把高风险 / 系统接口项挡在外面。</summary>
    /// <summary>
    /// 清理成功后同步清单：删掉的项从分组移除、空分组删除、预计可释放归零，
    /// 避免"已删项仍勾选，再点清理一堆 TOCTOU 失败"。
    /// </summary>
    [Fact]
    public void MainViewModel_RemoveCleanedItems_DropsDeletedAndResetsSelection()
    {
        var vm = Fixture.CreateMain();
        var a = Fixture.Plan(@"C:\Temp\a.tmp", category: "临时文件", risk: RiskLevel.Low);
        var b = Fixture.Plan(@"C:\Temp\b.tmp", category: "临时文件", risk: RiskLevel.Low);
        vm.SetPlan(new[] { a, b });
        a.IsSelected = b.IsSelected = true;
        Assert.Equal(8192, vm.EstimatedReclaimBytes);   // Fixture.Plan 固定 4096 B/项

        vm.RemoveCleanedItems(new[] { @"C:\Temp\a.tmp" }, Array.Empty<PlanItem>());

        var group = Assert.Single(vm.Groups);
        Assert.Single(group.Items);
        Assert.Equal(@"C:\Temp\b.tmp", group.Items[0].Path);
        Assert.Equal(4096, vm.EstimatedReclaimBytes);
        Assert.Single(vm.SelectedItems);
        Assert.True(vm.HasResults);

        // 移除最后一项后空分组消失、空状态生效、估计归零
        vm.RemoveCleanedItems(new[] { @"C:\Temp\b.tmp" }, Array.Empty<PlanItem>());
        Assert.Empty(vm.Groups);
        Assert.False(vm.HasResults);
        Assert.Equal(0, vm.EstimatedReclaimBytes);
        Assert.Empty(vm.SelectedItems);
        Assert.False(vm.AllSelected);
    }

    [Fact]
    public void MainViewModel_GlobalSelectAll_CascadesToGroupsAndSkipsHighRisk()
    {
        var vm = Fixture.CreateMain();
        var low = Fixture.Plan(@"C:\Temp\a.tmp", category: "临时文件", risk: RiskLevel.Low);
        var critical = Fixture.Plan(@"C:\Temp\crit.tmp", category: "系统维护",
            risk: RiskLevel.Critical, isSystem: true);
        vm.SetPlan(new[] { low, critical });
        low.IsSelected = critical.IsSelected = false;
        Assert.False(vm.AllSelected);

        vm.AllSelected = true;

        Assert.True(low.IsSelected);
        Assert.False(critical.IsSelected, "全局全选也不能打开 CRITICAL / 系统接口项");
        Assert.True(vm.Groups.Single(g => g.Name == "临时文件").AllSelected);
        Assert.True(vm.CanSelectAll, "有安全项时全局全选必须可用");
        Assert.Single(vm.SelectedItems);
        Assert.True(vm.AllSelected, "所有可勾选项都选上了，全局全选就该显示勾选而不是停在半选");
    }

    /// <summary>
    /// 「系统维护（不可回退）」这类系统接口项即使在全选下也不能被勾上：
    /// 它们走的是另一条执行路径，必须逐项人工确认。
    /// </summary>
    [Fact]
    public void MainViewModel_GlobalSelectAll_NeverSelectsSystemOperations()
    {
        var vm = Fixture.CreateMain();
        var files = Enumerable.Range(0, 5)
            .Select(i => Fixture.Plan($@"C:\Temp\u{i}.dat", category: "其他", risk: RiskLevel.Medium))
            .ToArray();
        var system = Enumerable.Range(0, 5)
            .Select(i => Fixture.Plan($@"C:\System\{i}", category: "系统维护（不可回退）",
                risk: RiskLevel.High, action: RecommendationAction.Review, isSystem: true))
            .ToArray();
        vm.SetPlan(files.Concat(system));
        foreach (var item in files.Concat(system)) item.IsSelected = false;

        vm.AllSelected = true;

        Assert.All(files, f => Assert.True(f.IsSelected));
        var systemGroup = vm.Groups.Single(g => g.Name.Contains("系统维护"));
        Assert.All(systemGroup.Items, i => Assert.False(i.IsSelected));
        Assert.False(systemGroup.AllSelected);
    }

    /// <summary>整份清单都是受保护项时，顶部全局全选同样置灰。</summary>
    [Fact]
    public void MainViewModel_AllProtected_DisablesGlobalSelectAll()
    {
        var vm = Fixture.CreateMain();
        var keep = Fixture.Plan(@"C:\Users\wang\Desktop\a.dat", category: "其他",
            risk: RiskLevel.Medium, action: RecommendationAction.Keep);
        vm.SetPlan(new[] { keep });
        keep.IsSelected = false;

        Assert.False(vm.CanSelectAll);
        Assert.False(vm.AllSelected);
    }

    /// <summary>全局全选的三态要跟着子项走：部分选中 → 不确定；再写 false → 全部清空。</summary>
    [Fact]
    public void MainViewModel_GlobalSelectAll_TracksPartialStateAndClearsEverything()
    {
        var vm = Fixture.CreateMain();
        var a = Fixture.Plan(@"C:\Temp\a.tmp", category: "临时文件", risk: RiskLevel.Low);
        var b = Fixture.Plan(@"C:\Temp\b.tmp", category: "临时文件", risk: RiskLevel.Low);
        vm.SetPlan(new[] { a, b });
        a.IsSelected = b.IsSelected = false;

        a.IsSelected = true;
        Assert.True(vm.AllSelected is null, "只勾一项时全局全选应是不确定态");

        vm.AllSelected = false;

        Assert.False(a.IsSelected);
        Assert.False(b.IsSelected);
        Assert.False(vm.AllSelected);
    }

    [Fact]
    public void MainViewModel_OpenSettings_OnlyReloadsServicesWhenDialogConfirms()
    {
        var context = new FakeContext();
        var dialogs = new FakeDialogs();
        var vm = Fixture.CreateMain(context, dialogs);

        vm.OpenSettingsCommand.Execute(null);
        Assert.Equal(0, context.ReloadCount);

        dialogs.SettingsResult = true;
        vm.OpenSettingsCommand.Execute(null);
        Assert.Equal(1, context.ReloadCount);
    }

    [Fact]
    public void MainViewModel_CleanupCommands_AreBlockedWhileCleaning()
    {
        // 删除改到后台线程后，按钮必须在整个清理过程中失效，否则连点会重复删同一批。
        var vm = Fixture.CreateMain();

        Assert.True(vm.CleanupCommand.CanExecute(null));
        Assert.True(vm.StartScanCommand.CanExecute(null));

        vm.IsCleaning = true;

        Assert.False(vm.CleanupCommand.CanExecute(null));
        Assert.False(vm.ElevatedCleanupCommand.CanExecute(null));
        Assert.False(vm.StartScanCommand.CanExecute(null));
        Assert.True(vm.IsBusy);

        vm.IsCleaning = false;
        Assert.True(vm.CleanupCommand.CanExecute(null));
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task MainViewModel_Cleanup_WithoutSelection_ShowsHintAndDoesNotDelete()
    {
        var dialogs = new FakeDialogs();
        var vm = Fixture.CreateMain(dialogs: dialogs);

        await vm.CleanupCommand.ExecuteAsync(null);

        Assert.Contains(dialogs.Messages, m => m.Contains("请先勾选"));
    }

    [Fact]
    public void PreviewViewModel_PermanentDeletion_RequiresExactConfirmWord()
    {
        var dialogs = new FakeDialogs();
        var window = new FakeWindow();
        var vm = new PreviewViewModel(dialogs, window,
            new[] { Fixture.Plan(@"C:\Temp\a.tmp") }, new DiskSweep.Core.Storage.AppSettings());

        vm.Permanent = true;
        Assert.True(vm.ConfirmEnabled, "永久删除开启后确认词输入框才可用");
        Assert.Equal("确认永久删除", vm.ConfirmCaption);

        vm.ConfirmWord = "随便写的";
        vm.ConfirmCommand.Execute(null);
        Assert.True(vm.Result is null, "确认词不匹配时不得产生删除决定");
        Assert.Equal(0, window.CloseCount);
        Assert.Contains(dialogs.Messages, m => m.Contains("确认词"));

        vm.ConfirmWord = DeleteExecutor.PermanentConfirmWord;
        vm.ConfirmCommand.Execute(null);
        Assert.NotNull(vm.Result);
        Assert.True(vm.Result!.Confirmed);
        Assert.True(vm.Result.Permanent);
        Assert.Equal(DeleteExecutor.PermanentConfirmWord, vm.Result.ConfirmWord);
        Assert.Equal(true, window.ClosedWith);
    }

    [Fact]
    public void PreviewViewModel_PermanentMode_RejectsAiSuggestions()
    {
        var window = new FakeWindow();
        var vm = new PreviewViewModel(new FakeDialogs(), window,
            new[] { Fixture.Plan(@"C:\Temp\a.tmp", source: RecommendationSource.Ai) },
            new DiskSweep.Core.Storage.AppSettings())
        {
            Permanent = true,
            ConfirmWord = DeleteExecutor.PermanentConfirmWord
        };

        vm.ConfirmCommand.Execute(null);

        Assert.True(vm.Result is null, "8.1 红线：永久删除模式下 AI 建议一律禁用");
        Assert.Equal(0, window.CloseCount);
    }

    [Fact]
    public void PreviewViewModel_Cancel_ReturnsCancelledDecision()
    {
        var window = new FakeWindow();
        var vm = new PreviewViewModel(new FakeDialogs(), window,
            new[] { Fixture.Plan(@"C:\Temp\a.tmp") }, new DiskSweep.Core.Storage.AppSettings());

        vm.CancelCommand.Execute(null);

        Assert.NotNull(vm.Result);
        Assert.False(vm.Result!.Confirmed);
        Assert.False(vm.Result.Permanent);
        Assert.Equal(false, window.ClosedWith);
    }

    [Fact]
    public void PreviewViewModel_RecycleMode_NeedsNoConfirmWord()
    {
        var window = new FakeWindow();
        var vm = new PreviewViewModel(new FakeDialogs(), window,
            new[] { Fixture.Plan(@"C:\Temp\a.tmp") }, new DiskSweep.Core.Storage.AppSettings());

        vm.ConfirmCommand.Execute(null);

        Assert.True(vm.Result!.Confirmed);
        Assert.False(vm.Result.Permanent);
        Assert.Null(vm.Result.ConfirmWord);
        Assert.Equal(true, window.ClosedWith);
    }

    [Fact]
    public void MainViewModel_RestartAsAdmin_OnlyOfferedWithGapsAndNonAdmin()
    {
        var launcher = new FakeAdminLauncher { IsAdministrator = false };
        var dialogs = new FakeDialogs();
        var vm = Fixture.CreateMain(dialogs: dialogs, adminLauncher: launcher);

        Assert.False(vm.CanRestartAsAdmin);
        Assert.False(vm.RestartAsAdminCommand.CanExecute(null));

        // 只有真的有统计缺口时才提供重扫入口（缺口列表由扫描结果填充）。
        vm.ScanGaps = new[] { "182 个目录无访问权限" };
        Assert.True(vm.CanRestartAsAdmin);
        Assert.True(vm.RestartAsAdminCommand.CanExecute(null));

        vm.RestartAsAdminCommand.Execute(null);
        Assert.Equal(1, launcher.RestartCount);
        Assert.Equal(@"C:\", launcher.LastRoot);

        // 用户拒绝 UAC：保留当前结果与提示，不关闭实例。
        launcher.RestartResult = false;
        vm.RestartAsAdminCommand.Execute(null);
        Assert.Contains(dialogs.Messages, m => m.Contains("取消了 UAC"));
    }

    [Fact]
    public void MainViewModel_DefaultsToSystemDriveRoot()
    {
        // 默认扫描整块系统盘（通常是 C:\），而不是用户目录；系统装在非 C 盘时按实际系统卷推导。
        var vm = Fixture.CreateMain();

        Assert.Equal(MainViewModel.DefaultScanRoot, vm.RootPath);
        Assert.Equal(3, vm.RootPath.Length);
        Assert.True(char.IsLetter(vm.RootPath[0]) && vm.RootPath[1] == ':' && vm.RootPath[2] == '\\');
        Assert.Equal(@"C:\", MainViewModel.DefaultScanRoot);   // 本机系统盘
    }

    [Fact]
    public void MainViewModel_Initialize_ListsEveryDriveAndMarksCurrentVolume()
    {
        var vm = Fixture.CreateMain();
        vm.InitializeCommand.Execute(null);

        Assert.True(vm.HasDrives);
        Assert.Equal(3, vm.Drives.Count);
        Assert.Equal(new[] { "C:", "D:", "E:" }, vm.Drives.Select(d => d.Name).ToArray());
        string currentVolume = Path.GetPathRoot(Path.GetFullPath(vm.RootPath))!
            .TrimEnd('\\').ToUpperInvariant();
        var highlighted = Assert.Single(vm.Drives, d => d.IsCurrent);
        Assert.Equal(currentVolume, highlighted.Name.ToUpperInvariant());
        Assert.Equal(60, vm.UsedPercent);
    }

    [Fact]
    public void MainViewModel_SelectDrive_ChangesScanRootAndHighlight()
    {
        var vm = Fixture.CreateMain();
        vm.InitializeCommand.Execute(null);

        vm.SelectDriveCommand.Execute(@"D:\");

        Assert.Equal(@"D:\", vm.RootPath);
        Assert.True(vm.Drives.Single(d => d.Name == "D:").IsCurrent);
        Assert.False(vm.Drives.Single(d => d.Name == "C:").IsCurrent);
        Assert.Contains(@"D:\", vm.StatusText);
    }

    [Fact]
    public void MainViewModel_RootPath_AcceptsFreeTextAndSurvivesInvalidInput()
    {
        var vm = Fixture.CreateMain();
        vm.InitializeCommand.Execute(null);

        const string custom = @"C:\Users\someone\My Folder";
        vm.RootPath = custom;
        Assert.Equal(custom, vm.RootPath);

        // 控制字符会让 Path.GetFullPath 抛异常；卷解析必须容错，且不能改写用户手输内容。
        const string invalid = "C:\\bad\0path";
        vm.RootPath = invalid;
        Assert.Equal(invalid, vm.RootPath);
        Assert.True(vm.HasDrives);
    }

    [Fact]
    public void MainViewModel_ScanPathOptions_OnlyAllDrivesAndDriveRoots()
    {
        var vm = Fixture.CreateMain();
        vm.InitializeCommand.Execute(null);

        // 下拉不再夹带常用目录：只有「所有磁盘」+ 每个盘根。
        Assert.Equal(
            new[] { MainViewModel.AllDrivesToken, @"C:\", @"D:\", @"E:\" },
            vm.ScanPathOptions.Select(o => o.Path).ToArray());

        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.DoesNotContain(vm.ScanPathOptions, o => string.Equals(o.Path, profile,
            StringComparison.OrdinalIgnoreCase));

        var driveOption = vm.ScanPathOptions.Single(o => o.Path == @"D:\");
        Assert.Contains("本地磁盘", driveOption.Hint);
        // 可编辑下拉选中项后，文本框必须得到真实路径而不是带说明的展示文本。
        Assert.Equal(driveOption.Path, driveOption.ToString());
        Assert.Contains("全部", vm.ScanPathOptions[0].Hint);
    }

    [Fact]
    public void MainViewModel_AllDrives_HighlightsEveryDiskAndGuardsMissingVolumes()
    {
        var diskInfo = new FakeDiskInfo();
        diskInfo.Drives.Clear();
        diskInfo.Drives.Add(new DriveDescriptor(@"Z:\no-such\", "虚拟盘", "本地磁盘", 100, 50));
        diskInfo.Drives.Add(new DriveDescriptor(@"Y:\no-such\", "虚拟盘", "本地磁盘", 100, 50));
        var dialogs = new FakeDialogs();
        var vm = Fixture.CreateMain(dialogs: dialogs, diskInfo: diskInfo);
        vm.InitializeCommand.Execute(null);

        Assert.Contains(vm.ScanPathOptions, o => o.Path == MainViewModel.AllDrivesToken);
        vm.RootPath = MainViewModel.AllDrivesToken;

        Assert.True(vm.ScanAllDrives);
        Assert.All(vm.Drives, d => Assert.True(d.IsCurrent, "所有磁盘模式下每块盘都是扫描目标"));
        Assert.Contains("2 个磁盘", vm.AllDrivesSummaryText);

        // 所有根都不可访问时应给出明确提示，而不是把「所有磁盘」当真实路径扫描。
        vm.StartScanCommand.Execute(null);
        Assert.Contains(dialogs.Messages, m => m.Contains("没有可访问的磁盘"));
        Assert.False(vm.IsScanning);
    }

    [Fact]
    public void MainViewModel_RepeatedRefresh_KeepsPathOptionInstancesAndCustomRoot()
    {
        var vm = Fixture.CreateMain();
        vm.InitializeCommand.Execute(null);
        var before = vm.ScanPathOptions.ToArray();
        Assert.NotEmpty(before);

        // 可编辑 ComboBox 在 ItemsSource 被 Clear 时回写空 Text；选项实例必须保持稳定。
        const string custom = @"D:\Some\Custom Folder";
        vm.RootPath = custom;
        vm.InitializeCommand.Execute(null);
        Assert.Equal(before, vm.ScanPathOptions.ToArray());
        Assert.Equal(custom, vm.RootPath);

        vm.SelectDriveCommand.Execute(@"E:\");
        Assert.Equal(before, vm.ScanPathOptions.ToArray());
        Assert.Equal(@"E:\", vm.RootPath);
        Assert.Contains(@"E:\", vm.StatusText);
    }

    [Fact]
    public void MainViewModel_PathOptionHint_UpdatesInPlaceWhenFreeSpaceChanges()
    {
        var diskInfo = new FakeDiskInfo();
        var vm = Fixture.CreateMain(diskInfo: diskInfo);
        vm.InitializeCommand.Execute(null);
        var option = vm.ScanPathOptions.Single(o => o.Path == @"D:\");
        string original = option.Hint;

        diskInfo.Drives[1] = diskInfo.Drives[1] with { FreeBytes = 1 };
        vm.InitializeCommand.Execute(null);

        Assert.Same(option, vm.ScanPathOptions.Single(o => o.Path == @"D:\"));
        Assert.NotEqual(original, option.Hint);
    }

    [Fact]
    public void MainViewModel_EmptyDriveEnumeration_KeepsUsableState()
    {
        var diskInfo = new FakeDiskInfo();
        diskInfo.Drives.Clear();
        var vm = Fixture.CreateMain(diskInfo: diskInfo);

        vm.InitializeCommand.Execute(null);

        Assert.Empty(vm.Drives);
        Assert.False(vm.HasDrives);
        Assert.NotEmpty(vm.ScanPathOptions);
        Assert.Equal(60, vm.UsedPercent);
    }

    [Fact]
    public void DriveItem_ZeroCapacityAndInvertedUsage_DoNotOverflow()
    {
        var empty = new DriveItem(new DriveDescriptor(@"F:\", "", "光驱", 0, 0), true);
        Assert.True(empty.IsCurrent);
        Assert.Equal(0, empty.UsedPercent);
        Assert.Equal("0%", empty.UsedPercentText);
        Assert.Equal("光驱", empty.Label);
        Assert.Contains("0 B", empty.CapacityText);

        // FreeBytes > TotalBytes 时无符号相减会下溢成天文数字，必须先比较再减。
        var inverted = new DriveItem(new DriveDescriptor(@"G:\", "异常卷", "本地磁盘", 100, 500), false);
        Assert.Equal(0, inverted.UsedPercent);
    }

    [Fact]
    public void MainViewModel_SidebarNavigation_SwitchesBetweenAnalysisCleanupAndSoftware()
    {
        var vm = Fixture.CreateMain();

        // 默认先落在空间分析：扫描完成后用户先看占用，再决定要不要清理。
        Assert.True(vm.IsAnalysisView);
        Assert.False(vm.IsCleanupView);
        Assert.False(vm.IsSoftwareView);
        Assert.Equal(MainViewModel.AnalysisTabIndex, vm.ContentTabIndex);

        vm.ShowCleanupViewCommand.Execute(null);
        Assert.Equal(MainViewModel.CleanupTabIndex, vm.ContentTabIndex);
        Assert.True(vm.IsCleanupView);
        Assert.False(vm.IsAnalysisView);

        vm.ShowSoftwareViewCommand.Execute(null);
        Assert.Equal(MainViewModel.SoftwareTabIndex, vm.ContentTabIndex);
        Assert.True(vm.IsSoftwareView);
        Assert.False(vm.IsCleanupView);

        // 三个入口都保持可达，分析页没有强制门槛。
        vm.ShowAnalysisViewCommand.Execute(null);
        Assert.Equal(MainViewModel.AnalysisTabIndex, vm.ContentTabIndex);
        Assert.True(vm.IsAnalysisView);
        Assert.False(vm.IsSoftwareView);
    }

    [Fact]
    public void MainViewModel_HasNoTabControlIndexAndUsesScanTotalAsPercentBase()
    {
        var vmType = typeof(MainViewModel);
        Assert.NotNull(vmType.GetProperty("IsAnalysisView"));
        Assert.NotNull(vmType.GetProperty("IsCleanupView"));
        Assert.NotNull(vmType.GetProperty("ScannedTotalBytes"));
        Assert.Null(vmType.GetProperty("TabControlIndex"));
        Assert.Null(vmType.GetProperty("IsResultsView"));
        Assert.Null(vmType.GetProperty("MaxCategoryBytes"));
        Assert.Null(vmType.GetProperty("ShowResultsViewCommand"));
    }
}
