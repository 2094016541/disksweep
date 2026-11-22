using System.Text.RegularExpressions;
using Xunit;

namespace Sweep.Tests;

/// <summary>
/// 文档 2.3 / 3.2 要求 WPF / MVVM + CommunityToolkit.Mvvm。
/// 这里的检查全部基于源码文本，不需要引用 App 程序集，因此不影响
/// 「测试项目刻意不引用可缺省 AI 插件」的架构红线。
/// </summary>
public class MvvmArchitectureTests
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

    private static bool IsSource(string path) =>
        !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
        !path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    private static string[] XamlFiles() =>
        Directory.EnumerateFiles(AppDir, "*.xaml", SearchOption.AllDirectories)
            .Where(IsSource).ToArray();

    private static string[] Sources(string subdir, string pattern) =>
        Directory.EnumerateFiles(Path.Combine(AppDir, subdir), pattern, SearchOption.AllDirectories)
            .Where(IsSource).ToArray();

    private static string Read(string path) => File.ReadAllText(path);

    /// <summary>架构扫描只看代码：剔除 XML 文档/行注释/块注释，避免说明文字造成误判。</summary>
    private static string Code(string path)
    {
        var source = Read(path);
        source = Regex.Replace(source, @"/\*[\s\S]*?\*/", " ");
        return Regex.Replace(source, @"(?m)//.*$", " ");
    }

    /// <summary>
    /// 事件处理器只可能以「属性名起始」出现。用负向后顾边界排除 IsChecked="{Binding ...}"
    /// 这类合法双向绑定，以及 using Sweep.App.Services; 这类命名空间导入。
    /// </summary>
    private static readonly Regex EventHandlerAttribute = new(
        @"(?<![\w.])(?:Click|Checked|Unchecked|SelectionChanged|TextChanged|Loaded|KeyDown|MouseDoubleClick)=""",
        RegexOptions.Compiled);

    private static readonly Regex GlobalStaticAccess = new(
        @"(?<![\w.])App\.Services\.|Application\.Current|Dispatcher\.Invoke",
        RegexOptions.Compiled);

    [Fact]
    public void Xaml_HasNoCodeBehindEventHandlers()
    {
        var files = XamlFiles();
        Assert.True(files.Length >= 7, $"应扫描到至少 7 个 XAML，实际 {files.Length} 个");
        foreach (var file in files)
        {
            var xaml = Read(file);
            var match = EventHandlerAttribute.Match(xaml);
            Assert.True(!match.Success,
                $"{Path.GetFileName(file)} 仍有 code-behind 事件处理器：{match.Value}");
        }
    }

    [Fact]
    public void MainWindow_BindsAllButtonsToCommands()
    {
        var xaml = Read(Path.Combine(AppDir, "MainWindow.xaml"));
        var commands = Regex.Matches(xaml, @"\bCommand=""\{Binding").Count;
        Assert.True(commands >= 9, $"主窗口应有至少 9 个命令绑定，实际 {commands} 个");
        Assert.DoesNotContain("ReadyVisibility", xaml);
        Assert.DoesNotContain("ScanVisibility", xaml);
        Assert.Contains("IsReady, Converter={StaticResource BoolToVis}", xaml);
    }

    [Fact]
    public void EveryBoundCommand_IsGeneratedFromARelayCommand()
    {
        var generated = new HashSet<string>(StringComparer.Ordinal);
        var vmFiles = Sources("ViewModels", "*.cs");
        Assert.True(vmFiles.Length >= 5, $"应有至少 5 个 ViewModel 源文件，实际 {vmFiles.Length} 个");

        foreach (var file in vmFiles)
        {
            var matches = Regex.Matches(Read(file),
                @"\[RelayCommand[^\]]*\]\s*private\s+(?:async\s+)?(?:Task|void)\s+(\w+)\s*\(");
            foreach (Match match in matches)
            {
                var method = match.Groups[1].Value;
                if (method.EndsWith("Async", StringComparison.Ordinal)) method = method[..^5];
                generated.Add(method + "Command");
            }
        }
        Assert.True(generated.Count >= 20, $"应生成至少 20 个命令，实际 {generated.Count} 个");

        var bound = 0;
        foreach (var file in XamlFiles())
        {
            foreach (Match match in Regex.Matches(Read(file), @"\bCommand=""\{Binding\s+(\w+Command)\}"""))
            {
                bound++;
                Assert.Contains(match.Groups[1].Value, generated);
            }
        }
        Assert.True(bound >= 20, $"应检查至少 20 个 XAML 命令绑定，实际 {bound} 个");
    }

    [Fact]
    public void ViewModels_DoNotReferenceWpfOrGlobalStatics()
    {
        var files = Sources("ViewModels", "*.cs");
        string[] banned =
        {
            "using System.Windows", "MessageBox", "Visibility", "RoutedEventArgs", "new Window"
        };
        foreach (var file in files)
        {
            var code = Code(file);
            foreach (var word in banned)
                Assert.DoesNotContain(word, code);
            Assert.False(GlobalStaticAccess.IsMatch(code),
                $"{Path.GetFileName(file)} 仍访问全局静态组合根或 WPF Dispatcher");
        }

        Assert.Contains("using CommunityToolkit.Mvvm.ComponentModel;", Read(Path.Combine(AppDir, "ViewModels", "MainViewModel.cs")));
        Assert.Contains("using CommunityToolkit.Mvvm.Input;", Read(Path.Combine(AppDir, "ViewModels", "MainViewModel.cs")));
    }

    [Fact]
    public void Views_AreThinBindingBridges()
    {
        var files = Sources("Views", "*.xaml.cs")
            .Concat(new[] { Path.Combine(AppDir, "MainWindow.xaml.cs") }).ToArray();
        Assert.True(files.Length >= 3, $"应有至少 3 个窗口 code-behind，实际 {files.Length} 个");
        foreach (var file in files)
        {
            var code = Code(file);
            Assert.DoesNotContain("RoutedEventArgs", code);
            Assert.DoesNotContain("MessageBox", code);
            Assert.DoesNotContain("DeleteExecutor", code);
            Assert.False(GlobalStaticAccess.IsMatch(code),
                $"{Path.GetFileName(file)} 仍访问全局静态组合根或 WPF Dispatcher");
            int lines = Read(file).Split('\n').Length;
            Assert.True(lines <= 30, $"{Path.GetFileName(file)} 有 {lines} 行，超出薄桥接上限");
        }
    }

    [Fact]
    public void App_UsesToolkitAndARealCompositionRoot()
    {
        var csproj = Read(Path.Combine(AppDir, "Sweep.App.csproj"));
        Assert.Contains("CommunityToolkit.Mvvm", csproj);

        var contracts = Read(Path.Combine(AppDir, "Services", "Contracts.cs"));
        foreach (var seam in new[]
        {
            "interface IAppContext", "interface IDialogService", "interface IUiDispatcher",
            "interface IWindowController", "interface IPasswordSupplier",
            "interface IDiskInfoService", "interface IAiCatalog", "interface IScheduleService"
        })
            Assert.Contains(seam, contracts);

        var composition = Read(Path.Combine(AppDir, "Services", "CompositionRoot.cs"));
        foreach (var factory in new[]
        {
            "CreateMainViewModel", "CreateSettingsViewModel",
            "CreatePreviewViewModel"
        })
            Assert.Contains(factory, composition);

        Assert.Contains(": IAppContext", Read(Path.Combine(AppDir, "Services", "AppServices.cs")));
    }

    /// <summary>
    /// 界面契约的静态守护：系统所有磁盘总览、可编辑的路径下拉、磁盘块命令。
    /// 这些绑定只在运行时解析，构建成功不代表窗口能打开。
    /// </summary>
    [Fact]
    public void MainWindow_ShowsAllDrivesAndAnEditablePathPicker()
    {
        var xaml = Read(Path.Combine(AppDir, "MainWindow.xaml"));
        Assert.Contains("ItemsSource=\"{Binding Drives}\"", xaml);
        Assert.Contains("SelectDriveCommand", xaml);
        Assert.Contains("x:Key=\"DriveChipTemplate\"", xaml);
        Assert.Contains("x:Key=\"EditablePathComboBox\"", xaml);
        Assert.Contains("PART_EditableTextBox", xaml);
        Assert.Contains("ItemsSource=\"{Binding ScanPathOptions}\"", xaml);
        Assert.Contains("Text=\"{Binding RootPath, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}\"", xaml);
        Assert.Contains("AllDrivesSummaryText", xaml);
    }

    /// <summary>
    /// 布局契约：磁盘只在概览出现一次、不再有「当前扫描盘」环、预计可释放与清理动作在扫描区，
    /// 且「所有磁盘」必须真的走多根扫描（单根时 ScanAllAsync 会委托回 ScanAsync）。
    /// </summary>
    [Fact]
    public void MainWindow_HasSingleDriveOverviewAndReclaimInsideScanArea()
    {
        var xaml = Read(Path.Combine(AppDir, "MainWindow.xaml"));
        Assert.Contains("系统所有磁盘", xaml);
        Assert.Contains("预计可释放", xaml);
        Assert.Contains("CleanupCommand", xaml);
        Assert.DoesNotContain("Text=\"当前扫描盘\"", xaml);
        Assert.DoesNotContain("Text=\"下方列出全部磁盘", xaml);
        Assert.Single(Regex.Matches(xaml, @"ItemsSource=""\{Binding Drives\}"""));

        var vm = Read(Path.Combine(AppDir, "ViewModels", "MainViewModel.cs"));
        Assert.Contains("ScanService.ScanAllAsync(", vm);
        Assert.Contains("AllDrivesToken", vm);
    }

    /// <summary>
    /// ProgressBar.Value 默认双向绑定；DriveItem.UsedPercent 只读，
    /// 漏写 Mode=OneWay 会在窗口 Show() 时抛 XamlParseException（已发生过的回归）。
    /// </summary>
    [Fact]
    public void DriveChip_ReadOnlyUsageBinding_IsExplicitlyOneWay()
    {
        var xaml = Read(Path.Combine(AppDir, "MainWindow.xaml"));
        Assert.Contains("Value=\"{Binding UsedPercent, Mode=OneWay}\"", xaml);
    }

    /// <summary>
    /// 侧边栏导航契约：不再有任何下拉菜单，三个视图入口都是 RelayCommand 按钮；
    /// 选中态由只读布尔属性的 DataTrigger 驱动，不依赖 IsChecked 的双向绑定。
    /// 空间分析与清理项已拆成两个独立视图，内容区不再有 TabControl / 页签条。
    /// </summary>
    [Fact]
    public void MainWindow_HasSidebarNavigationWithCommandOnlyItems()
    {
        var xaml = Read(Path.Combine(AppDir, "MainWindow.xaml"));
        Assert.Contains("<DockPanel LastChildFill=\"True\">", xaml);
        Assert.Contains("AutomationProperties.Name=\"主导航侧边栏\"", xaml);
        Assert.Contains("x:Key=\"NavButton\"", xaml);
        Assert.Contains("x:Name=\"AnalysisView\"", xaml);
        Assert.Contains("x:Name=\"CleanupView\"", xaml);
        Assert.Contains("ShowAnalysisViewCommand", xaml);
        Assert.Contains("ShowCleanupViewCommand", xaml);
        Assert.Contains("ShowSoftwareViewCommand", xaml);
        Assert.Contains("DataTrigger Binding=\"{Binding IsAnalysisView}\" Value=\"True\"", xaml);
        Assert.Contains("DataTrigger Binding=\"{Binding IsCleanupView}\" Value=\"True\"", xaml);
        Assert.Contains("DataTrigger Binding=\"{Binding IsSoftwareView}\" Value=\"True\"", xaml);
        Assert.Contains("Visibility=\"{Binding IsSoftwareView, Converter={StaticResource BoolToVis}}\"", xaml);
        Assert.Contains("Visibility=\"{Binding IsSoftwareView, Converter={StaticResource InverseBoolToVis}}\"", xaml);
        Assert.DoesNotContain("<TabControl", xaml);
        Assert.DoesNotContain("<TabItem", xaml);
        Assert.DoesNotContain("ContentOnlyTabControl", xaml);
        Assert.DoesNotContain("TabControlIndex", xaml);
        Assert.DoesNotContain("ResultsView", xaml);
        Assert.DoesNotContain("MaxCategoryBytes", xaml);
        Assert.DoesNotContain("<Menu", xaml);
        Assert.DoesNotContain("<MenuItem", xaml);
        Assert.DoesNotContain("IsSubmenuOpen", xaml);
        Assert.DoesNotContain("AppMenuBar", xaml);
        Assert.DoesNotContain("Header=\"软件管理\"", xaml);
    }

    /// <summary>
    /// 拆分后的两个视图：清理清单与空间占用表格各自独立、互不嵌套；
    /// 分析页带「去清理」引导按钮，占比分母统一为本次扫描总字节而不是最大类别。
    /// </summary>
    [Fact]
    public void MainWindow_HasSeparateAnalysisAndCleanupViews()
    {
        var xaml = Read(Path.Combine(AppDir, "MainWindow.xaml"));
        int cleanupStart = xaml.IndexOf("<Grid x:Name=\"CleanupView\"", StringComparison.Ordinal);
        int analysisStart = xaml.IndexOf("<Grid x:Name=\"AnalysisView\"", StringComparison.Ordinal);
        int software = xaml.IndexOf("<!-- 软件管理视图", StringComparison.Ordinal);
        Assert.True(cleanupStart >= 0 && analysisStart > cleanupStart && software > analysisStart,
            "清理项与空间分析必须各自是独立的命名 Grid");

        var cleanup = xaml[cleanupStart..analysisStart];
        Assert.Contains("ItemTemplate=\"{StaticResource GroupTemplate}\"", cleanup);
        Assert.Contains("ItemsSource=\"{Binding Groups}\"", cleanup);
        Assert.Contains("Command=\"{Binding ElevatedCleanupCommand}\"", cleanup);
        Assert.Contains("HasResults", cleanup);
        Assert.DoesNotContain("ItemsSource=\"{Binding Analysis}\"", cleanup);

        var analysis = xaml[analysisStart..software];
        Assert.Contains("ItemsSource=\"{Binding Analysis}\"", analysis);
        Assert.Contains("Header=\"占本次扫描\"", analysis);
        Assert.Contains("ScannedTotalBytes", analysis);
        Assert.DoesNotContain("MaxCategoryBytes", analysis);
        Assert.Contains("AutomationProperties.AutomationId=\"AnalysisToCleanupButton\"", analysis);
        Assert.Contains("Command=\"{Binding ShowCleanupViewCommand}\"", analysis);
        Assert.DoesNotContain("ItemTemplate=\"{StaticResource GroupTemplate}\"", analysis);
    }

    /// <summary>
    /// 分组标题行必须是显式的 Grid，而不是寄宿在 Expander.Header ——
    /// 一旦内层模板吃掉 Header，分类名和「全选」会一起消失（真实回归）。
    /// </summary>
    [Fact]
    public void MainWindow_GroupHeaderIsExplicitAndCarriesSelectAll()
    {
        var xaml = Read(Path.Combine(AppDir, "MainWindow.xaml"));
        int start = xaml.IndexOf("x:Key=\"GroupTemplate\"", StringComparison.Ordinal);
        Assert.True(start > 0, "未找到清理清单分组模板");
        int end = xaml.IndexOf("</DataTemplate>", start, StringComparison.Ordinal);
        var group = xaml[start..end];

        Assert.DoesNotContain("<Expander", xaml);
        Assert.DoesNotContain("<Expander.Header>", xaml);
        Assert.Contains("Style=\"{StaticResource ExpanderToggle}\"", group);
        Assert.Contains("IsChecked=\"{Binding IsExpanded, Mode=TwoWay}\"", group);
        Assert.Contains("Text=\"{Binding Name}\"", group);
        Assert.Contains("Text=\"{Binding TotalText}\"", group);
        Assert.Contains("ItemsSource=\"{Binding Items}\"", group);
        // 每个分类一个全选：只写 IsChecked，勾选级联由 ViewModel 的 setter 负责
        Assert.Contains("Content=\"全选\"", group);
        Assert.Contains("IsChecked=\"{Binding AllSelected, Mode=TwoWay}\"", group);
        Assert.DoesNotContain("ToggleGroupCommand", group);
        // 清单只由外层 ScrollViewer 滚动，组内不再套一个会吞滚轮的 ListBox
        Assert.DoesNotContain("<ListBox", group);
    }

    /// <summary>清理清单工具行必须有全局全选（三态显示、两态点击），并保留提权清理入口。</summary>
    [Fact]
    public void MainWindow_HasGlobalSelectAllNextToElevatedCleanup()
    {
        var xaml = Read(Path.Combine(AppDir, "MainWindow.xaml"));
        int rowStart = xaml.IndexOf("<DockPanel Grid.Row=\"0\" Margin=\"2,0,2,8\"", StringComparison.Ordinal);
        Assert.True(rowStart > 0, "未找到清理清单工具行");
        var toolRow = xaml[rowStart..(xaml.IndexOf("</DockPanel>", rowStart, StringComparison.Ordinal) + 12)];

        Assert.Contains("Text=\"清理清单\"", toolRow);
        // 绑定的值可以是 null（渲染成横线），但控件声明成三态会让点击走 On→半选→Off，
        // 而半选会被级联写回全选，导致「勾着的时候点它清不掉」——必须有负向断言守着。
        Assert.DoesNotContain("IsThreeState=\"True\"", toolRow);
        Assert.Contains("IsChecked=\"{Binding AllSelected, Mode=TwoWay}\"", toolRow);
        Assert.Contains("提权清理 (UAC)", toolRow);
        // 全局全选 + 各分组全选，两处都绑同一个分组 / 全局属性
        Assert.Equal(2, Regex.Matches(xaml, @"IsChecked=""\{Binding AllSelected, Mode=TwoWay\}""").Count);
    }

    /// <summary>
    /// 相对占用改成「长方形 + 百分比」：矩形轨道、矩形填充、右侧百分比文本，
    /// 且 Value 必须显式 OneWay（ProgressBar.Value 默认双向，绑只读属性会炸）。
    /// </summary>
    [Fact]
    public void AnalysisUsageBar_IsRectangularAndShowsPercent()
    {
        var xaml = Read(Path.Combine(AppDir, "MainWindow.xaml"));

        int styleStart = xaml.IndexOf("x:Key=\"UsageBar\"", StringComparison.Ordinal);
        Assert.True(styleStart > 0, "未找到相对占用的条形样式");
        var style = xaml[styleStart..xaml.IndexOf("</Style>", styleStart, StringComparison.Ordinal)];
        Assert.Contains("x:Name=\"PART_Indicator\"", style);
        Assert.Contains("CornerRadius=\"2\"", style);
        Assert.DoesNotContain("999", style);
        Assert.DoesNotContain("Ellipse", style);

        Assert.Contains("Style=\"{StaticResource UsageBar}\"", xaml);
        Assert.Contains("Value=\"{Binding TotalBytes, Mode=OneWay}\"", xaml);
        Assert.Contains("Converter=\"{StaticResource PercentOfMax}\"", xaml);
        Assert.Contains("Header=\"占本次扫描\"", xaml);
        Assert.Contains("DataContext.ScannedTotalBytes", xaml);
        Assert.DoesNotContain("MaxCategoryBytes", xaml);
        // 数值列表头与单元格同一条右对齐线，避免「占用」标题和数字错位
        Assert.Equal(2, Regex.Matches(xaml, @"HorizontalContentAlignment"" Value=""Right""").Count);
    }

    /// <summary>
    /// 提权清理只对已勾选且需管理员的清理项生效，是清理项视图的动作按钮，
    /// 不应再作为侧边栏「工具」入口；命令仍由 RelayCommand 生成。
    /// </summary>
    [Fact]
    public void MainWindow_ElevatedCleanupSitsInsideCleanupView()
    {
        var xaml = Read(Path.Combine(AppDir, "MainWindow.xaml"));
        Assert.Contains("AutomationProperties.AutomationId=\"ElevatedCleanupInCleanupButton\"", xaml);
        Assert.Contains("Content=\"提权清理 (UAC)\"", xaml);
        Assert.Contains("Command=\"{Binding ElevatedCleanupCommand}\"", xaml);
        // 旧侧边栏按钮用的是 Text + Name="提权清理"（不带 UAC 后缀）
        Assert.DoesNotContain("Text=\"提权清理 (UAC)\"", xaml);
        Assert.DoesNotContain("AutomationProperties.Name=\"提权清理\"", xaml);

        int cleanup = xaml.IndexOf("<Grid x:Name=\"CleanupView\"", StringComparison.Ordinal);
        int analysis = xaml.IndexOf("<Grid x:Name=\"AnalysisView\"", StringComparison.Ordinal);
        int button = xaml.IndexOf("ElevatedCleanupInCleanupButton", StringComparison.Ordinal);
        Assert.True(cleanup >= 0 && analysis > cleanup && button > cleanup && button < analysis,
            "提权清理按钮必须位于清理项视图内部");
    }

    /// <summary>
    /// 排版红线：只允许六级字阶 11/12/13/16/20/24（外加品牌 34），
    /// 禁止再出现 10 / 10.5 / 11.5 / 12.5 / 13.5 这类靠 1px 递增堆出来的"伪层级"。
    /// </summary>
    [Fact]
    public void Typography_UsesOneCanonicalScale()
    {
        var styles = Read(Path.Combine(AppDir, "Themes", "Styles.xaml"));
        var main = Read(Path.Combine(AppDir, "MainWindow.xaml"));
        string[] allowed = { "11", "12", "13", "14", "16", "20", "24", "34" };

        foreach (var (name, xaml) in new[] { ("Styles.xaml", styles), ("MainWindow.xaml", main) })
        {
            foreach (Match m in Regex.Matches(xaml, @"FontSize=""([\d.]+)"""))
                Assert.True(Array.Exists(allowed, a => a == m.Groups[1].Value),
                    $"{name} 出现非标准字号 {m.Groups[1].Value}，请改用语义样式");

            // Setter 写法以前是守护盲区：<Setter Property="FontSize" Value="12.5"/> 不匹配属性语法，
            // 于是内联样式里的字号可以任意写而测试照绿。两种写法必须一起校验。
            foreach (Match m in Regex.Matches(xaml, @"Property=""FontSize""\s+Value=""([\d.]+)"""))
                Assert.True(Array.Exists(allowed, a => a == m.Groups[1].Value),
                    $"{name} 的 Setter 出现非标准字号 {m.Groups[1].Value}，请改用语义样式");
        }

        // 语义样式必须存在：微标签、小节标题、指标各有独立层级
        Assert.Contains("x:Key=\"MicroText\"", styles);
        Assert.Contains("x:Key=\"SectionHeading\"", styles);
        Assert.Contains("x:Key=\"MetricText\"", styles);
        // 主窗口的小节标题走 SectionHeading（16），弹窗仍用 SectionText（14）
        Assert.Equal(4, Regex.Matches(main, @"Style=""\{StaticResource SectionHeading\}""").Count);
        // 辅助文字必须过 WCAG AA：Caption 用 MutedBrush 而不是更浅的 FaintBrush
        int caption = styles.IndexOf("x:Key=\"CaptionText\"", StringComparison.Ordinal);
        var captionStyle = styles[caption..styles.IndexOf("</Style>", caption, StringComparison.Ordinal)];
        Assert.Contains("Value=\"{DynamicResource MutedBrush}\"", captionStyle);
        Assert.DoesNotContain("Value=\"{DynamicResource FaintBrush}\"", captionStyle);
    }

    /// <summary>
    /// 清理分组的子项必须虚拟化：空目录/空文件分组可达数千项，
    /// 无虚拟化的 ItemsControl 在展开时会一次性实例化全部项，造成明显卡顿。
    /// </summary>
    [Fact]
    public void CleanupGroupItems_AreVirtualized()
    {
        var xaml = Read(Path.Combine(AppDir, "MainWindow.xaml"));
        int start = xaml.IndexOf("x:Key=\"GroupTemplate\"", StringComparison.Ordinal);
        Assert.True(start > 0, "未找到分组模板");
        var group = xaml[start..xaml.IndexOf("</DataTemplate>", start, StringComparison.Ordinal)];

        Assert.Contains("VirtualizingStackPanel.IsVirtualizing=\"True\"", group);
        Assert.Contains("VirtualizationMode=\"Recycling\"", group);
        Assert.Contains("<VirtualizingStackPanel IsItemsHost=\"True\"/>", group);
        Assert.Contains("ItemsSource=\"{Binding Items}\"", group);
        // 内层需自带可滚动宿主，否则虚拟化面板找不到 scroll owner，不会真正虚拟化
        Assert.Contains("CanContentScroll=\"True\"", group);
    }

    /// <summary>空间分析表格提供维度筛选，且极小占比的进度条有最小可见宽度。</summary>
    [Fact]
    public void AnalysisView_HasDimensionFilterAndMinimumBarWidth()
    {
        var xaml = Read(Path.Combine(AppDir, "MainWindow.xaml"));
        Assert.Contains("AnalysisDimensionIndex", xaml);
        Assert.Contains("仅顶层目录", xaml);
        Assert.Contains("仅文件类型", xaml);
        Assert.Contains("MinWidth=\"3\"", xaml);   // UsageBar 极小占比仍可见
    }

    /// <summary>
    /// 启动即进主界面：不得再有阻断式的欢迎 / 同意窗口。
    /// 旧的首启向导会让 OnStartup 先切成手动退出模式，并在勾选同意前 ShowDialog 卡住启动，
    /// 这里守护启动路径、组合根工厂和相关文件都不复存在。
    /// </summary>
    [Fact]
    public void Startup_EntersMainWindowWithoutWizard()
    {
        var startup = Read(Path.Combine(AppDir, "App.xaml.cs"));
        Assert.DoesNotContain("FirstRunWindow", startup);
        Assert.DoesNotContain("FirstRunCompleted", startup);
        Assert.DoesNotContain("OnExplicitShutdown", startup);
        Assert.Contains("main.Show()", startup);
        Assert.Contains("ShutdownMode.OnMainWindowClose", startup);
        Assert.DoesNotContain("FirstRun", Read(Path.Combine(AppDir, "Services", "CompositionRoot.cs")));
        Assert.False(File.Exists(Path.Combine(AppDir, "Views", "FirstRunWindow.xaml")));
        Assert.False(File.Exists(Path.Combine(AppDir, "ViewModels", "FirstRunViewModel.cs")));
    }

    /// <summary>管理员重扫只能经 IAdminLauncher：UAC / Process / WindowsIdentity 不进 ViewModel。</summary>
    [Fact]
    public void RestartAsAdmin_StaysInServiceLayer()
    {
        var vm = Read(Path.Combine(AppDir, "ViewModels", "MainViewModel.cs"));
        foreach (var banned in new[] { "Process.Start", "WindowsIdentity", "WindowsPrincipal", "Verb = \"runas\"" })
            Assert.DoesNotContain(banned, vm);
        Assert.Contains("IAdminLauncher", vm);

        var launcher = Read(Path.Combine(AppDir, "Services", "AdminLauncher.cs"));
        Assert.Contains("Verb = \"runas\"", launcher);
        Assert.Contains("--scan", Read(Path.Combine(AppDir, "App.xaml.cs")));
    }
}
