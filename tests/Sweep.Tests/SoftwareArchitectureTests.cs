using System.Text.RegularExpressions;
using Xunit;

namespace Sweep.Tests;

/// <summary>
/// 软件管理功能的静态守护：界面契约、服务隔离、卸载安全。
/// 全部基于源码文本，不引用 App 程序集，保持「测试项目不依赖可缺省 AI 插件」的红线。
/// </summary>
public class SoftwareArchitectureTests
{
    private static readonly string Root = FindRoot();

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Sweep.slnx"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("未找到 Sweep.slnx 所在的仓库根目录");
    }

    private static string AppDir => Path.Combine(Root, "src", "Sweep.App");
    private static string Read(string relative) => File.ReadAllText(Path.Combine(AppDir, relative));
    private static string ReadCore(string relative) =>
        File.ReadAllText(Path.Combine(Root, "src", "Sweep.Core", relative));

    /// <summary>剔除注释后再做「禁止标识符」检查，避免说明文字造成误判。</summary>
    private static string Code(string relative) => StripComments(Read(relative));
    private static string CoreCode(string relative) => StripComments(ReadCore(relative));

    private static string StripComments(string source)
    {
        source = Regex.Replace(source, @"/\*[\s\S]*?\*/", " ");
        return Regex.Replace(source, @"(?m)//.*$", " ");
    }

    /// <summary>页面契约：软件管理由左侧固定导航栏切换，不再是页签，也不再有下拉菜单。</summary>
    [Fact]
    public void MainWindow_HasSoftwareManagementSidebarEntryAndItsBindings()
    {
        var xaml = Read("MainWindow.xaml");
        Assert.Contains("x:Key=\"NavButton\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"NavSoftwareButton\"", xaml);
        Assert.Contains("Text=\"软件管理\"", xaml);
        Assert.Contains("ShowSoftwareViewCommand", xaml);
        Assert.Contains("IsSoftwareView", xaml);
        // 清理清单与空间占用合并后，内容区不再有页签索引双向回写。
        Assert.DoesNotContain("TabControlIndex", xaml);
        Assert.DoesNotContain("<TabItem", xaml);
        Assert.DoesNotContain("<Menu", xaml);
        Assert.DoesNotContain("Header=\"软件管理\"", xaml);
        Assert.DoesNotContain("SelectedIndex=\"{Binding ContentTabIndex, Mode=TwoWay}\"", xaml);
        Assert.Contains("DataContext=\"{Binding Software}\"", xaml);
        Assert.Contains("Text=\"{Binding SearchText, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}\"", xaml);
        Assert.Contains("ItemsSource=\"{Binding Items}\"", xaml);
        Assert.Contains("SelectedItem=\"{Binding SelectedItem, Mode=TwoWay}\"", xaml);

        foreach (var command in new[]
        {
            "RefreshSoftwareCommand", "OpenAppsAndFeaturesCommand", "ScanSoftwareLocationCommand",
            "OpenSoftwareLocationCommand", "MeasureSoftwareSizeCommand", "UninstallSelectedCommand",
            "CheckSoftwareUpgradesCommand", "OpenUpgradeSelectedCommand",
            "StartUpgradeCommand", "LoadVersionsCommand", "CancelUpgradeCommand"
        })
            Assert.Contains(command, xaml);
    }

    /// <summary>
    /// 升级面板的绑定契约：软件源下拉、候选包下拉、全部版本下拉与确认按钮都在 ViewModel 上。
    /// 包 ID 不再有手输入口——普通用户不可能知道 "Ruihu.Apifox" 这种标识符，
    /// 界面只展示自动匹配到的候选。
    /// </summary>
    [Fact]
    public void MainWindow_BindsUpgradePanelToItsOwnViewModel()
    {
        var xaml = Read("MainWindow.xaml");
        Assert.Contains("Visibility=\"{Binding IsUpgradeVisible, Converter={StaticResource BoolToVis}}\"", xaml);
        Assert.Contains("DataContext=\"{Binding Upgrade}\"", xaml);
        Assert.Contains("ItemsSource=\"{Binding SourceOptions}\"", xaml);
        Assert.Contains("SelectedIndex=\"{Binding SelectedSourceIndex, Mode=TwoWay}\"", xaml);
        Assert.Contains("ItemsSource=\"{Binding VersionOptions}\"", xaml);
        Assert.Contains("SelectedIndex=\"{Binding SelectedVersionIndex, Mode=TwoWay}\"", xaml);
        Assert.Contains("IsEditable=\"False\"", xaml);
        Assert.Contains("ItemsSource=\"{Binding PackageOptions}\"", xaml);
        Assert.Contains("SelectedIndex=\"{Binding SelectedPackageIndex, Mode=TwoWay}\"", xaml);
        Assert.Contains("Text=\"{Binding VersionHintText}\"", xaml);
        Assert.Contains("Text=\"目标版本（多源全部版本）\"", xaml);
        Assert.Contains("Text=\"软件源\"", xaml);
        Assert.Contains("Text=\"winget 包（自动匹配）\"", xaml);
        // 包 ID 没有可手写的文本框：那个框对普通用户毫无意义，填错了还会升级到别的软件。
        Assert.DoesNotContain("<TextBox Text=\"{Binding PackageId", xaml);
        Assert.DoesNotContain("DataGridTemplateColumn Header=\"可升级到\"", xaml);
    }

    /// <summary>目标版本只能由下拉决定：界面与 ViewModel 都不存在手写版本号旁路。</summary>
    [Fact]
    public void UpgradeTargetVersion_IsChosenOnlyFromDropdown()
    {
        var xaml = Read("MainWindow.xaml");
        Assert.DoesNotContain("CustomVersion", xaml);
        Assert.DoesNotContain("或手写指定版本", xaml);

        var panel = Code(Path.Combine("ViewModels", "SoftwareUpgradeViewModel.cs"));
        Assert.DoesNotContain("CustomVersion", panel);
        Assert.Contains("HasVersions && SelectedVersionIndex >= 0", panel);
        Assert.Contains("_versionRequests[SelectedVersionIndex]", panel);
        Assert.Contains("RebuildOptions(result.Versions)", panel);
    }

    /// <summary>软件源来自 winget 配置、经白名单校验后进入 --source，绝不拼接任意文本。</summary>
    [Fact]
    public void UpgradeSource_IsDiscoveredWhitelistedAndPassedToWinget()
    {
        var sources = CoreCode(Path.Combine("Software", "WingetSources.cs"));
        Assert.Contains("SourceNamePattern", sources);
        Assert.Contains("ParseList", sources);
        Assert.Contains("MergeVersions", sources);

        var request = CoreCode(Path.Combine("Software", "UpgradeRequest.cs"));
        Assert.Contains("WingetSources.IsValidName(Source)", request);
        Assert.Contains("\"--source\"", request);

        var service = Code(Path.Combine("Services", "WingetUpgradeService.cs"));
        Assert.Contains("GetSourcesAsync", service);
        Assert.Contains("\"source\", \"list\"", service);
        Assert.Contains("\"--source\", name", service);
        // 启动升级不得重建请求（重建会漏字段）：直接用已校验请求的 BuildArguments。
        Assert.DoesNotContain("UpgradeRequest.TryCreate(request", service);
        Assert.Contains("request.BuildArguments()", service);

        var panel = Code(Path.Combine("ViewModels", "SoftwareUpgradeViewModel.cs"));
        // 源清单只能来自 winget 自己（服务查询）与 Core 的预置目录，ViewModel 不得自带一份硬编码列表。
        Assert.Contains("_service.GetSourcesAsync(", panel);
        Assert.Contains("WingetSources.BuildChoices(", panel);
        Assert.Contains("WingetSourceCatalog.Presets", panel);
        Assert.DoesNotContain("mirrors.ustc", panel);
        Assert.Contains("OnSelectedSourceIndexChanged", panel);
    }

    /// <summary>
    /// 顶部卡片分视图可见：软件管理隐藏扫描动作卡片（清单与升级面板需要完整高度）；
    /// 磁盘总览、扫描路径行、扫描状态行只在空间分析页出现，清理页只留清理动作与清单。
    /// </summary>
    [Fact]
    public void MainWindow_TopCardsVisibilityPerView()
    {
        var xaml = Read("MainWindow.xaml");

        // 仍由 IsSoftwareView 隐藏的扫描动作卡片只剩 1 张（预计可释放 + 清理按钮）。
        Assert.Single(Regex.Matches(xaml,
            @"<Border Grid\.Row=""[12]""[^>]*IsSoftwareView, Converter=\{StaticResource InverseBoolToVis\}"));

        // 磁盘总览、路径/模式行、状态/进度行共 3 处只在分析页显示，清理页看不到磁盘与扫描配置。
        Assert.Equal(3, Regex.Matches(xaml,
            @"(?:Border|Grid) Grid\.Row=""[12]""[^>]*IsAnalysisView, Converter=\{StaticResource BoolToVis\}").Count);
    }

    /// <summary>每个绑定的软件命令都由 [RelayCommand] 生成，而不是 code-behind 事件。</summary>
    [Fact]
    public void EverySoftwareCommand_IsGeneratedFromARelayCommand()
    {
        var generated = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in new[]
                 {
                     Path.Combine("ViewModels", "SoftwareViewModel.cs"),
                     Path.Combine("ViewModels", "SoftwareUpgradeViewModel.cs")
                 })
        {
            foreach (Match m in Regex.Matches(Read(file),
                         @"\[RelayCommand[^\]]*\]\s*private\s+(?:async\s+)?(?:Task|void)\s+(\w+)\s*\("))
            {
                var method = m.Groups[1].Value;
                if (method.EndsWith("Async", StringComparison.Ordinal)) method = method[..^5];
                generated.Add(method + "Command");
            }
        }

        foreach (var expected in new[]
        {
            "RefreshSoftwareCommand", "OpenAppsAndFeaturesCommand", "ScanSoftwareLocationCommand",
            "OpenSoftwareLocationCommand", "MeasureSoftwareSizeCommand", "UninstallSelectedCommand",
            "CheckSoftwareUpgradesCommand", "OpenUpgradeSelectedCommand",
            "StartUpgradeCommand", "LoadVersionsCommand", "CancelUpgradeCommand"
        })
            Assert.Contains(expected, generated);
    }

    /// <summary>注册表与进程启动必须集中在服务适配层，ViewModel 不碰系统 API。</summary>
    [Fact]
    public void WindowsActions_AreIsolatedInSoftwareService()
    {
        foreach (var file in new[] { "ViewModels/SoftwareViewModel.cs", "ViewModels/MainViewModel.cs" })
        {
            var source = Code(file);
            foreach (var banned in new[] { "Process.Start", "RegistryKey", "Microsoft.Win32", "MessageBox" })
                Assert.DoesNotContain(banned, source);
        }

        var service = Read(Path.Combine("Services", "SoftwareService.cs"));
        Assert.Contains("class SoftwareService : ISoftwareService", service);
        Assert.Contains("RegistryKey.OpenBaseKey", service);
        Assert.Contains("UseShellExecute = true", service);
    }

    /// <summary>
    /// 卸载安全：绝不读取 QuietUninstallString，检测到静默参数必须拒绝，
    /// 且启动前要求可执行文件存在或是系统命令。
    /// </summary>
    [Fact]
    public void UninstallPath_RefusesSilentCommandsAndValidatesTarget()
    {
        var service = Code(Path.Combine("Services", "SoftwareService.cs"));
        Assert.DoesNotContain("QuietUninstallString", service);
        Assert.Contains("command.IsSilent", service);
        Assert.Contains("File.Exists(command.FileName)", service);

        var confirm = Read(Path.Combine("ViewModels", "SoftwareViewModel.cs"));
        Assert.Contains("_dialogs.Confirm(", confirm);
        Assert.Contains("不会静默卸载", confirm);
    }

    /// <summary>服务接缝与组合根接线：生产路径注入真实实现，未注入时仍有空实现兜底。</summary>
    [Fact]
    public void CompositionRoot_WiresRealSoftwareService()
    {
        Assert.Contains("interface ISoftwareService", Read(Path.Combine("Services", "Contracts.cs")));
        Assert.Contains("interface ISoftwareUpgradeService", Read(Path.Combine("Services", "Contracts.cs")));
        var composition = Read(Path.Combine("Services", "CompositionRoot.cs"));
        Assert.Contains("_software = new SoftwareService()", composition);
        Assert.Contains("_upgrade = new WingetUpgradeService()", composition);
        Assert.Contains("_residue = new UninstallResidueService()", composition);
        Assert.Contains("_forceRemoval = new SoftwareForceRemovalService()", composition);
        Assert.Contains("_theme, _software, _upgrade, _residue,", composition);

        var main = Read(Path.Combine("ViewModels", "MainViewModel.cs"));
        Assert.Contains("NullSoftwareService.Instance", main);
        Assert.Contains("public SoftwareViewModel Software { get; }", main);
    }

    /// <summary>
    /// 升级安全：只启动可见的交互式 winget 控制台，绝不静默、绝不强制、绝不自动接受协议；
    /// 命令文本在 Core 里做白名单校验，启动前必须有二次确认。
    /// </summary>
    [Fact]
    public void UpgradeLaunch_IsInteractiveConfirmedAndNeverSilent()
    {
        var service = Code(Path.Combine("Services", "WingetUpgradeService.cs"));
        Assert.Contains("\"cmd.exe\"", service);
        Assert.Contains("\"/k\"", service);
        Assert.Contains("UseShellExecute = true", service);
        Assert.Contains("ArgumentList.Add(argument)", service);
        foreach (var banned in new[]
                 {
                     "--silent", "--force", "accept-package-agreements", "accept-source-agreements",
                     "QuietUninstallString", "LaunchUninstall"
                 })
            Assert.DoesNotContain(banned, service);

        var request = CoreCode(Path.Combine("Software", "UpgradeRequest.cs"));
        Assert.Contains("\"--interactive\"", request);
        Assert.Contains("IsValidId", request);
        Assert.Contains("IsValidVersionToken", request);
        Assert.Contains("WingetSources.IsValidName(Source)", request);

        var panel = Read(Path.Combine("ViewModels", "SoftwareUpgradeViewModel.cs"));
        Assert.Contains("_dialogs.Confirm(", panel);
        Assert.Contains("不会静默安装", panel);
    }

    /// <summary>升级相关 ViewModel 依旧不碰进程、注册表与 WPF 消息框。</summary>
    [Fact]
    public void UpgradeViewModels_DoNotTouchSystemApis()
    {
        foreach (var file in new[]
                 {
                     Path.Combine("ViewModels", "SoftwareViewModel.cs"),
                     Path.Combine("ViewModels", "SoftwareUpgradeViewModel.cs"),
                     Path.Combine("ViewModels", "MainViewModel.cs")
                 })
        {
            var source = Code(file);
            foreach (var banned in new[]
                     {
                         "Process.Start", "RegistryKey", "Microsoft.Win32", "MessageBox"
                     })
                Assert.DoesNotContain(banned, source);
        }
    }

    /// <summary>
    /// 软件列表只保留高频列；发布者 / 安装日期 / 来源 / 安装目录移到选中后的详情区。
    /// 「可升级到」列已删除：它在未检查升级时整列空白，检查后只重复一遍版本号，
    /// 而「可升级」这个事实已经由筛选下拉表达。
    /// </summary>
    [Fact]
    public void SoftwareList_KeepsHighFrequencyColumnsAndShowsDetailsOnSelection()
    {
        var xaml = Read("MainWindow.xaml");
        var grid = Regex.Match(xaml, @"<DataGrid ItemsSource=""\{Binding Items\}""[\s\S]*?</DataGrid>");
        Assert.True(grid.Success, "未找到软件列表 DataGrid");

        foreach (var header in new[] { "软件名称", "版本", "占用" })
            Assert.Contains($"Header=\"{header}\"", grid.Value);
        foreach (var removed in new[] { "可升级到", "发布者", "安装日期", "来源", "安装目录" })
            Assert.DoesNotContain($"Header=\"{removed}\"", grid.Value);

        Assert.Contains("Text=\"{Binding SelectionDetailText}\"", xaml);
        Assert.Contains("Text=\"{Binding SelectionLocationText}\"", xaml);
        Assert.Contains("Visibility=\"{Binding HasSelection, Converter={StaticResource BoolToVis}}\"", xaml);
        Assert.DoesNotContain("SizeKind", xaml);
        Assert.DoesNotContain("登记值", xaml);
        Assert.DoesNotContain("SizeKind", Code(Path.Combine("ViewModels", "SoftwareViewModel.cs")));
    }

    /// <summary>
    /// 卸载残留清理的安全边界：注册表写操作只在专用服务；删除前必导出 .reg 备份且复核失效；
    /// ViewModel 不得出现任何注册表写/删标识符，Core 判定为纯函数。
    /// </summary>
    [Fact]
    public void ResidueCleanup_IsolatedAndBackupBeforeDelete()
    {
        var service = Read(Path.Combine("Services", "UninstallResidueService.cs"));
        var serviceCode = Code(Path.Combine("Services", "UninstallResidueService.cs"));
        Assert.Contains("DeleteSubKeyTree", serviceCode);
        Assert.Contains("ExportKey", serviceCode);
        Assert.Contains("\"export\"", service);                 // reg.exe export 备份
        Assert.Contains("Evaluate(fresh", serviceCode);          // 删除瞬间复核仍为失效
        Assert.Contains("ArgumentList.Add", serviceCode);        // 参数逐项传入防注入

        foreach (var file in new[] { "ViewModels/SoftwareViewModel.cs", "ViewModels/MainViewModel.cs" })
        {
            var code = Code(file);
            Assert.DoesNotContain("DeleteSubKey", code);
            Assert.DoesNotContain("RegistryKey", code);
            Assert.DoesNotContain("reg.exe", code);
        }

        // Core 判定不引用注册表，保持纯逻辑可测
        var core = CoreCode(Path.Combine("Software", "UninstallResidue.cs"));
        Assert.DoesNotContain("Registry", core);
        Assert.Contains("public static ResidueVerdict Evaluate", core);
    }

    /// <summary>
    /// 强制删除安全边界（Geek 式扫描全关联）：系统级黑名单 + 逐项勾选 + 回收站 + 注册表备份，
    /// 且系统 API 全部隔离在服务层，ViewModel / Core 不直接碰注册表或删除。
    /// </summary>
    [Fact]
    public void ForceRemoval_IsConservativeAndIsolated()
    {
        var core = CoreCode(Path.Combine("Software", "ForceRemovalClassifier.cs"));
        Assert.Contains("SystemLevelNames", core);
        Assert.Contains("SharedRuntimeFolders", core);
        Assert.Contains("IsInSafeRoot", core);
        Assert.Contains("IsPathStillSafe", core);   // 删除瞬间 TOCTOU 复核
        Assert.DoesNotContain("RegistryKey", core); // Core 纯判定，不碰注册表 API（枚举名 ForceRemovalKind.Registry 是数据标签，不算系统 API）

        var service = Code(Path.Combine("Services", "SoftwareForceRemovalService.cs"));
        Assert.Contains("_deleteBatch(safe, true)", service);  // 磁盘项固定进回收站，可撤销
        Assert.Contains("ExportKey", service);                 // 注册表先备份
        Assert.Contains("DeleteSubKeyTree", service);
        Assert.Contains("IsPathStillSafe", service);           // 删除前复核
        Assert.Contains("ArgumentList.Add", service);          // reg.exe 参数防注入

        foreach (var file in new[] { "ViewModels/SoftwareViewModel.cs", "ViewModels/MainViewModel.cs" })
        {
            var code = Code(file);
            Assert.DoesNotContain("DeleteSubKey", code);
            Assert.DoesNotContain("RegistryKey", code);
            Assert.DoesNotContain("Process.Start", code);
            Assert.DoesNotContain("reg.exe", code);
        }

        var xaml = Read("MainWindow.xaml");
        Assert.Contains("ScanForceRemovalCommand", xaml);
        Assert.Contains("ForceRemoveSelectedCommand", xaml);
        Assert.Contains("ForceRemovalCandidates", xaml);
    }

    /// <summary>失效判定必须双重证据：卸载器与安装目录均不存在才算残留。</summary>
    [Fact]
    public void ResidueVerdict_RequiresTwoIndependentSignals()
    {
        var core = CoreCode(Path.Combine("Software", "UninstallResidue.cs"));
        Assert.Contains("uninstallerGone", core);
        Assert.Contains("locationGone", core);
        Assert.Contains("IsSystemCommand", core);
        Assert.Contains("IsSystemComponent", core);
    }
}
