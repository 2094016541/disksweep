using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sweep.App.Services;
using Sweep.Core;
using Sweep.Core.Executors;
using Sweep.Core.Ipc;
using Sweep.Core.Recommendation;
using Sweep.Core.Scanning;
using Sweep.Core.Storage;

namespace Sweep.App.ViewModels;

/// <summary>磁盘总览的一行；IsCurrent 表示当前扫描根目录所在卷，由 ViewModel 回写。</summary>
public sealed partial class DriveItem : ObservableObject
{
    public string Root { get; }
    public string Name { get; }
    public string Label { get; }
    public string TypeLabel { get; }
    public double UsedPercent { get; }
    public string UsedPercentText { get; }
    public string CapacityText { get; }

    [ObservableProperty] private bool isCurrent;

    public DriveItem(DriveDescriptor descriptor, bool isCurrent)
    {
        Root = descriptor.Root;
        Name = descriptor.Root.Length >= 2 && descriptor.Root[1] == ':'
            ? descriptor.Root[..2]
            : descriptor.Root.TrimEnd('\\', '/');
        Label = string.IsNullOrWhiteSpace(descriptor.Label) ? descriptor.TypeLabel : descriptor.Label;
        TypeLabel = descriptor.TypeLabel;

        ulong usedBytes = descriptor.TotalBytes > descriptor.FreeBytes
            ? descriptor.TotalBytes - descriptor.FreeBytes : 0;
        UsedPercent = descriptor.TotalBytes == 0
            ? 0 : Math.Clamp(usedBytes * 100d / descriptor.TotalBytes, 0, 100);
        UsedPercentText = $"{UsedPercent:0.#}%";
        CapacityText = $"可用 {Format(descriptor.FreeBytes)} / 共 {Format(descriptor.TotalBytes)}";
        this.isCurrent = isCurrent;
    }

    private static string Format(ulong bytes) =>
        PlanItem.FormatSize((long)Math.Min(bytes, (ulong)long.MaxValue));
}

/// <summary>
/// 扫描路径下拉的预设项。重写 ToString 返回真实路径：可编辑 ComboBox 选中项后，
/// 文本框里必须是可直接扫描的路径，而不是带说明的展示文本。
/// Hint 可就地更新：磁盘可用空间变化时不重建集合，避免抹掉 ComboBox 的选中项。
/// </summary>
public sealed partial class ScanPathOption : ObservableObject
{
    public string Path { get; }

    [ObservableProperty] private string hint;

    public ScanPathOption(string path, string hint)
    {
        Path = path;
        this.hint = hint;
    }

    public override string ToString() => Path;
}

/// <summary>分组行：承载「全选」命令与聚合数值，纯粹的可观察状态，不含任何视图类型。</summary>
public sealed partial class CategoryGroup : ObservableObject
{
    public string Name { get; }
    public bool IsHighRisk { get; }
    public ObservableCollection<PlanItem> Items { get; } = new();

    [ObservableProperty] private bool isExpanded;
    [ObservableProperty] private bool? allSelected = false;

    /// <summary>由 Refresh 回写三态时置位，避免「状态同步」反过来又触发一次全选级联。</summary>
    private bool _syncingSelection;
    /// <summary>批量勾选时置位，抑制每项 IsSelected 变化触发的逐项 Refresh（否则全选是 O(n²)）。</summary>
    private bool _bulkUpdating;

    public string TotalText => PlanItem.FormatSize(Items.Sum(i => i.AllocationSize));
    public string SelectedText => PlanItem.FormatSize(SelectedBytes);
    public long SelectedBytes => Items.Where(i => i.IsSelected).Sum(i => i.AllocationSize);
    public int SelectedCount => Items.Count(i => i.IsSelected);
    public int ItemCount => Items.Count;

    /// <summary>
    /// 整组都是「系统接口项」（清空更新缓存、重启服务这类动作，没有可统计的字节）。
    /// 这类分组头的体积恒为 0 B，显示出来只会让人以为没东西可清，
    /// 因此改显示「需管理员 · 不可回退」，明确它是一组操作而不是一批文件。
    /// </summary>
    public bool IsSystemGroup => Items.Count > 0 && Items.All(i => i.IsSystemOperation);

    /// <summary>
    /// 该项能否被「全选」批量勾选：受保护（Keep）、CRITICAL 与系统接口项都不行。
    /// 全在「其他」分类里时整组都是 Keep，全选框必须置灰，否则点下去像坏了。
    /// </summary>
    public static bool IsBulkSelectable(PlanItem item) =>
        item.Action != RecommendationAction.Keep &&
        item.Risk != RiskLevel.Critical &&
        !item.IsSystemOperation;

    /// <summary>本分类里至少有一项可批量勾选；没有时全选框置灰。</summary>
    public bool CanSelectAll => Items.Any(IsBulkSelectable);

    /// <summary>
    /// 三态汇总只看「可批量勾选」的项：受保护 / CRITICAL / 系统接口项本来就选不中，
    /// 若把它们算进来，用户点完全选也会看到复选框停在横线，观感等于点了没反应。
    /// </summary>
    public static bool? TriStateOf(IEnumerable<PlanItem> items)
    {
        var selectable = items.Where(IsBulkSelectable).ToList();
        if (selectable.Count == 0) return false;
        return selectable.All(i => i.IsSelected) ? true
            : selectable.Any(i => i.IsSelected) ? null
            : false;
    }

    public CategoryGroup(string name, IEnumerable<PlanItem> items, bool highRisk)
    {
        Name = name;
        IsHighRisk = highRisk;
        isExpanded = !highRisk;              // 高风险项默认折叠（4.8）
        // 按体积降序：清单里最有价值的永远是最大的那几项，扫描顺序（目录遍历序）对用户没有意义。
        foreach (var item in items
                     .OrderByDescending(i => i.AllocationSize)
                     .ThenBy(i => i.Path, StringComparer.OrdinalIgnoreCase))
        {
            Items.Add(item);
            item.PropertyChanged += OnItemChanged;
        }
        Refresh();   // 初始勾选状态（规则默认只勾低风险）必须回写到分组头的三态
    }

    /// <summary>由主 ViewModel 注入：在资源管理器中定位该项（未注入时为空操作）。</summary>
    public Action<PlanItem>? OpenLocationRequested { get; set; }
    /// <summary>由主 ViewModel 注入：把该项加入白名单并持久化（未注入时为空操作）。</summary>
    public Action<PlanItem>? WhitelistRequested { get; set; }

    [RelayCommand]
    private void OpenLocation(PlanItem? item)
    {
        if (item is not null) OpenLocationRequested?.Invoke(item);
    }

    [RelayCommand]
    private void Whitelist(PlanItem? item)
    {
        if (item is not null) WhitelistRequested?.Invoke(item);
    }

    public void Add(PlanItem item)
    {
        Items.Add(item);
        item.PropertyChanged += OnItemChanged;
        Refresh();
    }

    /// <summary>
    /// 移除已成功清理的项并解绑事件。用批量抑制避免逐项触发 Refresh（O(n²)）。
    /// 返回移除数量，供调用方决定是否删掉空分组。
    /// </summary>
    public int RemoveWhere(IReadOnlySet<string> paths)
    {
        if (paths.Count == 0) return 0;
        _bulkUpdating = true;
        int removed = 0;
        try
        {
            for (int i = Items.Count - 1; i >= 0; i--)
            {
                if (!paths.Contains(Items[i].Path)) continue;
                Items[i].PropertyChanged -= OnItemChanged;
                Items.RemoveAt(i);
                removed++;
            }
        }
        finally { _bulkUpdating = false; }
        if (removed > 0) Refresh();
        return removed;
    }

    public void ToggleAll(bool selected)
    {
        // 逐项写 IsSelected 会触发 OnItemChanged→Refresh（每次 O(n) 汇总）。
        // 批量更新时先抑制，循环结束统一刷新一次，把 O(n²) 降到 O(n)。
        _bulkUpdating = true;
        try
        {
            foreach (var item in Items)
                item.IsSelected = selected && IsBulkSelectable(item);
        }
        finally { _bulkUpdating = false; }
        Refresh();
    }

    /// <summary>
    /// 分组头「全选」是三态复选框：用户点击时直接写 AllSelected，由 setter 级联到子项。
    /// 三态循环里点成「半选」（null）按全选处理，符合桌面清理工具的惯例。
    /// </summary>
    partial void OnAllSelectedChanged(bool? value)
    {
        if (_syncingSelection) return;
        ToggleAll(value != false);
    }

    /// <summary>分组头「全选」CheckBox 的命令；参数取自控件自身的 IsChecked（保留给脚本/测试调用）。</summary>
    [RelayCommand]
    private void ToggleGroup(bool? selected) => ToggleAll(selected == true);

    private void OnItemChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlanItem.IsSelected) && !_bulkUpdating) Refresh();
    }

    private void Refresh()
    {
        // 分组头复选框是三态：全选 / 全不选 / 部分选中。子项变化（含「全选低风险」
        // 这类跨组命令）必须回写该状态，否则 UI 会显示与实际勾选不一致。
        _syncingSelection = true;
        try
        {
            AllSelected = TriStateOf(Items);
        }
        finally
        {
            _syncingSelection = false;
        }
        OnPropertyChanged(nameof(TotalText));
        OnPropertyChanged(nameof(SelectedText));
        OnPropertyChanged(nameof(SelectedBytes));
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(ItemCount));
        OnPropertyChanged(nameof(CanSelectAll));
        OnPropertyChanged(nameof(IsSystemGroup));
    }
}

/// <summary>
/// 主界面 ViewModel：状态 + 全部交互命令。通过构造函数注入服务，不引用 Window / MessageBox /
/// Dispatcher / App.Services，因此可在无 WPF 消息循环的进程里构造并做单元测试。
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly IAppContext _app;
    private readonly IDialogService _dialogs;
    private readonly IUiDispatcher _dispatcher;
    private readonly IDiskInfoService _diskInfo;
    private readonly IAiCatalog _aiCatalog;
    private readonly IThemeService _theme;
    private readonly IShellLocator _locator;
    private readonly IAdminLauncher _adminLauncher;
    private CancellationTokenSource? _cts;

    public ObservableCollection<CategoryGroup> Groups { get; } = new();
    /// <summary>空间分析表格当前可见的分类（按维度筛选后的结果），就地 diff 更新以保留 DataGrid 行复用。</summary>
    public ObservableCollection<CategorySummary> Analysis { get; } = new();
    /// <summary>空间分析的完整分类集合（未筛选），维度切换时从这里重建 Analysis。</summary>
    private readonly List<CategorySummary> _analysisAll = new();
    /// <summary>维度筛选：0 全部 / 1 仅目录 / 2 仅类型。</summary>
    [ObservableProperty] private int analysisDimensionIndex;
    /// <summary>所有已就绪卷；点击某行即可把该盘设为扫描根目录。</summary>
    public ObservableCollection<DriveItem> Drives { get; } = new();
    /// <summary>扫描路径下拉的预设项；控件可编辑，用户仍可输入任意自定义路径。</summary>
    public ObservableCollection<ScanPathOption> ScanPathOptions { get; } = new();

    /// <summary>「所有磁盘」聚合项在文本框里的标记；不是真实路径，扫描时展开为全部就绪卷。</summary>
    public const string AllDrivesToken = "所有磁盘";

    /// <summary>
    /// 默认扫描根目录 = 系统盘根（通常是 C:\）。从 Windows 目录推导而不是硬编码，
    /// 系统装在非 C 盘时同样正确；回退值仅用于取不到系统目录的异常环境。
    /// </summary>
    public static string DefaultScanRoot { get; } =
        Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? @"C:\";

    /// <summary>内容区视图序号：0 空间分析 / 1 清理项 / 2 软件管理。</summary>
    public const int AnalysisTabIndex = 0;
    public const int CleanupTabIndex = 1;
    public const int SoftwareTabIndex = 2;

    /// <summary>软件管理子页；由主 ViewModel 持有，XAML 通过 DataContext 下钻。</summary>
    public SoftwareViewModel Software { get; }

    /// <summary>是否处于「所有磁盘」模式；该模式下所有磁盘块都算当前目标。</summary>
    public bool ScanAllDrives =>
        string.Equals(RootPath?.Trim(), AllDrivesToken, StringComparison.OrdinalIgnoreCase);

    public ScanResult? LastScan { get; set; }
    public string CurrentScanId { get; set; } = "";

    [ObservableProperty] private string driveLabel = "磁盘";
    [ObservableProperty] private string capacityText = "正在读取磁盘容量…";
    /// <summary>「系统所有磁盘」卡片头部的合计容量说明。</summary>
    [ObservableProperty] private string allDrivesSummaryText = "正在读取磁盘容量…";
    [ObservableProperty] private string statusText = "就绪 · 选择目录后开始扫描";
    [ObservableProperty] private string modeLabel = "兼容扫描（无需管理员）";
    [ObservableProperty] private string estimateText = "0 B";
    [ObservableProperty] private string selectedCountText = "0 项";
    [ObservableProperty] private string aiStatusText = "AI 未启用（默认关闭）";
    [ObservableProperty] private string themeLabel = "主题：跟随系统";
    [ObservableProperty] private string themeName = "跟随系统";
    [ObservableProperty] private int themeIndex;
    [ObservableProperty] private double usedPercent;
    [ObservableProperty] private string rootPath = DefaultScanRoot;
    [ObservableProperty] private int selectedModeIndex;

    /// <summary>主内容选择：0 空间分析 / 1 清理项 / 2 软件管理（由左侧固定导航栏切换）。</summary>
    [ObservableProperty] private int contentTabIndex;

    public bool IsAnalysisView => ContentTabIndex == AnalysisTabIndex;
    public bool IsCleanupView => ContentTabIndex == CleanupTabIndex;
    public bool IsSoftwareView => ContentTabIndex == SoftwareTabIndex;

    /// <summary>是否已有扫描结果，供空状态与列表互斥显示（View 侧只做布尔到可见性的转换）。</summary>
    [ObservableProperty] private bool hasResults;

    /// <summary>是否检测到就绪磁盘；为 false 时界面提示可直接手输路径。</summary>
    [ObservableProperty] private bool hasDrives;

    /// <summary>本次扫描的总字节，作为空间占用百分比的统一分母（不是 Top30 分类之和）。</summary>
    [ObservableProperty] private long scannedTotalBytes;

    /// <summary>
    /// 扫描完整性缺口（权限拒绝的目录、路径无法重建的项、整盘未扫描…）。
    /// 空列表表示「统计到的就是全部」，非空时分析页顶部显示提示条——
    /// 用户看到的占用数字是确定的，但真实占用可能更大，这一点必须主动说清楚。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasScanGaps))]
    [NotifyPropertyChangedFor(nameof(CanRestartAsAdmin))]
    private IReadOnlyList<string> scanGaps = Array.Empty<string>();

    public bool HasScanGaps => ScanGaps.Count > 0;

    /// <summary>有缺口且当前非管理员时才显示「以管理员身份重扫」。</summary>
    public bool CanRestartAsAdmin => HasScanGaps && !_adminLauncher.IsAdministrator;

    /// <summary>由命令行 --scan 注入：窗口 Loaded 后自动扫描该路径（管理员重启时用）。</summary>
    public string? StartupScanRoot { get; set; }

    /// <summary>
    /// 清理清单顶部的全局「全选」三态：写入时级联到每个分组，分组或子项变化时再汇总回来。
    /// 只勾「安全项」——高风险、CRITICAL 与系统接口项永远不会被全选打开。
    /// </summary>
    [ObservableProperty] private bool? allSelected = false;

    /// <summary>清单里是否至少有一项可批量勾选；全为受保护项时全局全选置灰。</summary>
    [ObservableProperty] private bool canSelectAll;

    /// <summary>由汇总回写三态时置位，阻断「汇总 → 级联」的递归。</summary>
    private bool _syncingAllSelected;
    /// <summary>全局全选批量写分组时置位，抑制每组逐次触发的全局汇总（否则 O(组数×项数)）。</summary>
    private bool _bulkSelecting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady))]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyCanExecuteChangedFor(nameof(StartScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(CleanupCommand))]
    [NotifyCanExecuteChangedFor(nameof(ElevatedCleanupCommand))]
    private bool isScanning;

    /// <summary>清理进行中：删除/还原是同步 IO，期间必须挡住重复点击与并发扫描。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyCanExecuteChangedFor(nameof(StartScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(CleanupCommand))]
    [NotifyCanExecuteChangedFor(nameof(ElevatedCleanupCommand))]
    private bool isCleaning;

    /// <summary>由 XAML 的 BooleanToVisibilityConverter 转成可见性；ViewModel 不认识 Visibility 类型。</summary>
    public bool IsReady => !IsScanning;

    /// <summary>扫描或清理进行中：状态条下方显示活动指示，避免长任务期间界面看起来“没反应”。</summary>
    public bool IsBusy => IsScanning || IsCleaning;

    /// <summary>侧边栏底部的版本标识；取自程序集版本，不引入任何 WPF 依赖。</summary>
    public string VersionText { get; } =
        "v" + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0");

    public long EstimatedReclaimBytes => Groups.Sum(g => g.SelectedBytes);
    public IEnumerable<PlanItem> AllItems => Groups.SelectMany(g => g.Items);
    public IReadOnlyList<PlanItem> SelectedItems => AllItems.Where(i => i.IsSelected).ToArray();

    /// <summary>全局全选被点击：级联写入各分组（null 是「半选」被点的中间态，按全选处理），再回算一次汇总。</summary>
    partial void OnAllSelectedChanged(bool? value)
    {
        if (_syncingAllSelected) return;
        _bulkSelecting = true;
        try
        {
            foreach (var group in Groups)
                group.AllSelected = value;
        }
        finally { _bulkSelecting = false; }
        RefreshEstimates();
        SyncAllSelectedState();
    }

    /// <summary>把各分组的勾选情况汇总成全局三态；调用方通常是分组自己发出的变更通知。</summary>
    private void SyncAllSelectedState()
    {
        _syncingAllSelected = true;
        try
        {
            CanSelectAll = Groups.Any(g => g.CanSelectAll);
            AllSelected = CategoryGroup.TriStateOf(AllItems);
        }
        finally
        {
            _syncingAllSelected = false;
        }
    }

    /// <summary>分组状态变化：刷新「预计可释放」，勾选态 / 可批量勾选能力变化时同步全局全选。</summary>
    private void OnGroupPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // 全局全选批量写分组期间，跳过每组逐次触发的汇总，循环结束后统一刷新一次。
        if (_bulkSelecting) return;
        RefreshEstimates();
        if (e.PropertyName is nameof(CategoryGroup.AllSelected) or nameof(CategoryGroup.CanSelectAll))
            SyncAllSelectedState();
    }

    private string ScanRoot
    {
        get
        {
            string value = string.IsNullOrWhiteSpace(RootPath)
                ? DefaultScanRoot
                : RootPath.Trim();
            // "D:" 在 .NET 里是 D 盘的当前目录；补成盘根才是用户想要的整盘扫描。
            if (value.Length == 2 && char.IsLetter(value[0]) && value[1] == ':') value += "\\";
            return value;
        }
    }

    /// <summary>本次扫描的根目录集合；「所有磁盘」展开为全部就绪卷，其余仍是单根。</summary>
    private IReadOnlyList<string> ScanRoots
    {
        get
        {
            if (!ScanAllDrives) return new[] { ScanRoot };
            var roots = Drives.Select(d => d.Root)
                .Where(r => !string.IsNullOrWhiteSpace(r)).ToArray();
            if (roots.Length > 0) return roots;
            try { return _diskInfo.GetAll().Select(d => d.Root).ToArray(); }
            catch { return Array.Empty<string>(); }
        }
    }

    /// <summary>手输过程中路径可能非法；卷解析必须容错，不能让每次按键都抛异常。</summary>
    private string VolumeRoot
    {
        get
        {
            string value = ScanAllDrives ? ScanRoots.FirstOrDefault() ?? "" : ScanRoot;
            if (string.IsNullOrWhiteSpace(value)) return @"C:\";
            try { return Path.GetPathRoot(Path.GetFullPath(value)) ?? @"C:\"; }
            catch { return @"C:\"; }
        }
    }

    private static bool SameVolume(string left, string right)
    {
        static string Normalize(string value)
        {
            try { return (Path.GetPathRoot(Path.GetFullPath(value)) ?? value).TrimEnd('\\', '/'); }
            catch { return value.TrimEnd('\\', '/'); }
        }
        return string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>不注入软件服务的构造路径（单元测试）：软件页使用空实现，仍可完整构造。</summary>
    public MainViewModel(
        IAppContext app,
        IDialogService dialogs,
        IUiDispatcher dispatcher,
        IDiskInfoService diskInfo,
        IAiCatalog aiCatalog,
        IThemeService theme)
        : this(app, dialogs, dispatcher, diskInfo, aiCatalog, theme,
               NullSoftwareService.Instance)
    {
    }

    public MainViewModel(
        IAppContext app,
        IDialogService dialogs,
        IUiDispatcher dispatcher,
        IDiskInfoService diskInfo,
        IAiCatalog aiCatalog,
        IThemeService theme,
        ISoftwareService software,
        ISoftwareUpgradeService? upgrade = null,
        IUninstallResidueService? residue = null,
        ISoftwareForceRemovalService? forceRemoval = null,
        IShellLocator? locator = null,
        IAdminLauncher? adminLauncher = null)
    {
        _app = app;
        _dialogs = dialogs;
        _dispatcher = dispatcher;
        _diskInfo = diskInfo;
        _aiCatalog = aiCatalog;
        _theme = theme;
        _locator = locator ?? NullShellLocator.Instance;
        _adminLauncher = adminLauncher ?? NullAdminLauncher.Instance;
        ThemeLabel = "主题：" + ThemeModes.Label(theme.Mode);
        ThemeName = ThemeModes.Label(theme.Mode);
        ThemeIndex = ThemeModes.ToIndex(theme.Mode);
        // 「所有磁盘」在窗口显示前即可用；各盘根目录在 Initialize / 刷新时并入。
        RebuildPathOptions(Array.Empty<DriveDescriptor>());

        Software = new SoftwareViewModel(software, dialogs, dispatcher, upgrade, residue, forceRemoval);
        // 「扫描安装目录」把路径回填到主扫描框，并切回空间分析视图（先看结果再决定清理）。
        Software.ScanPathRequested += path =>
        {
            RootPath = path;
            ContentTabIndex = AnalysisTabIndex;
            StatusText = $"扫描目录已切换为 {RootPath} · 点击「开始扫描」";
        };
    }

    /// <summary>侧边栏导航：刷新三个视图的选中态，切到软件管理时首次读取。</summary>
    partial void OnContentTabIndexChanged(int value)
    {
        ;
        OnPropertyChanged(nameof(IsAnalysisView));
        OnPropertyChanged(nameof(IsCleanupView));
        OnPropertyChanged(nameof(IsSoftwareView));
        if (value == SoftwareTabIndex && !Software.IsLoaded && !Software.IsLoading)
            _ = Software.EnsureLoadedAsync();
    }

    [RelayCommand]
    private void ShowAnalysisView() => ContentTabIndex = AnalysisTabIndex;

    [RelayCommand]
    private void ShowCleanupView() => ContentTabIndex = CleanupTabIndex;

    [RelayCommand]
    private void ShowSoftwareView() => ContentTabIndex = SoftwareTabIndex;

    /// <summary>窗口 Loaded 时由极薄桥接触发；窗口关闭时取消未完成的扫描与目录测量。</summary>
    public void OnClosing()
    {
        _cts?.Cancel();
        Software.CancelPendingWork();
    }

    [RelayCommand]
    private void Initialize()
    {
        RefreshDisk();
        AiStatusText = _aiCatalog.Status;
        // 管理员重启后由 --scan 参数自动续上扫描；普通启动路径不受影响。
        if (!string.IsNullOrWhiteSpace(StartupScanRoot))
        {
            RootPath = StartupScanRoot;
            _ = StartScanAsync();
        }
    }

    /// <summary>点击磁盘总览的一行：把该卷设为扫描根目录并刷新容量信息。</summary>
    [RelayCommand]
    private void SelectDrive(string? root)
    {
        if (string.IsNullOrWhiteSpace(root)) return;
        RootPath = root.Trim();
        RefreshDisk();
        StatusText = $"扫描目录已切换为 {RootPath} · 点击「开始扫描」";
    }

    /// <summary>路径手输时只更新磁盘高亮，不重新枚举磁盘，避免每次按键都查容量。</summary>
    partial void OnRootPathChanged(string value)
    {
        bool all = ScanAllDrives;
        string current = VolumeRoot;
        foreach (var drive in Drives)
            drive.IsCurrent = all || SameVolume(drive.Root, current);
    }

    private bool CanStartScan() => !IsScanning && !IsCleaning;
    private bool CanCancelScan() => IsScanning;
    private bool CanCleanup() => !IsScanning && !IsCleaning;

    /// <summary>
    /// 按当前维度把完整分类集合同步到 Analysis。0=全部；1=仅"目录 ·"；2=仅"类型 ·"。
    /// 分析表是纯展示（无选择状态需保留），Top30 规模很小，Clear+Add 最简单可靠。
    /// </summary>
    private void RefreshAnalysis()
    {
        var visible = AnalysisDimensionIndex switch
        {
            1 => _analysisAll.Where(c => c.Category.StartsWith("目录 ·", StringComparison.Ordinal)),
            2 => _analysisAll.Where(c => c.Category.StartsWith("类型 ·", StringComparison.Ordinal)),
            _ => _analysisAll
        };
        Analysis.Clear();
        foreach (var category in visible) Analysis.Add(category);
    }

    partial void OnAnalysisDimensionIndexChanged(int value) => RefreshAnalysis();

    [RelayCommand(CanExecute = nameof(CanStartScan))]
    private async Task StartScanAsync()
    {
        if (IsScanning) return;
        var roots = ScanRoots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (roots.Length == 0)
        {
            _dialogs.Notify(ScanAllDrives
                ? "没有可访问的磁盘，请直接输入扫描路径。"
                : "扫描目录不存在，请检查路径。", warning: true);
            return;
        }

        _cts = new CancellationTokenSource();
        IsScanning = true;
        // 清掉上一次的完整性缺口，避免旧的"未统计"提示挂在新结果上。
        ScanGaps = Array.Empty<string>();
        StatusText = ScanAllDrives ? $"正在扫描全部 {roots.Length} 个磁盘…" : "正在扫描…";
        var mode = SelectedModeIndex == 1 ? ScanMode.Mft : ScanMode.Walk;
        // 扫描收尾后 Progress<T> 可能还有报告在 UI 队列里；后处理阶段先把它们挡掉，
        // 否则「正在整理清单」会被旧进度覆盖，界面看起来又卡在扫描数字上。
        bool postProcessing = false;
        var progress = new Progress<ScanProgress>(p =>
        {
            if (p.Entries > 0 && !postProcessing)
                StatusText = $"{p.ModeLabel} · 已扫描 {p.Entries:N0} 个对象 / {PlanItem.FormatSize(p.Bytes)}";
        });

        try
        {
            // 单根时 ScanAllAsync 直接委托给 ScanAsync，行为与改造前完全一致。
            var scan = await Task.Run(() => ScanService.ScanAllAsync(
                roots, mode, _app.Rules, _cts.Token, progress,
                1000, _app.Settings.LargeFileThresholdBytes));

            // 空项检测要对每个目录做 Win32 长路径 / junction 解析，大盘上可达数千项。
            // 留在 UI 线程会冻结界面数秒，表现为「进度卡住然后突然扫描完成」。
            postProcessing = true;
            StatusText = "扫描完成，正在整理清理清单…";
            var (plan, emptyItems, systemOps) = await Task.Run(() =>
            {
                var rulePlan = RecommendationEngine.BuildRulePlan(scan, _app.Rules, _app.Safety);
                var empty = EmptyItemDetector.Build(scan, _app.Safety);
                var ops = RecommendationEngine.BuildSystemOperations(_app.Rules, _app.Safety);
                return (rulePlan, empty, ops);
            });

            LastScan = scan;
            CurrentScanId = scan.Snapshot.SnapshotId;
            // 空文件/空目录一律 Review + 默认不勾选，与传统候选共用同一删除链路。
            SetPlan(plan.Concat(emptyItems).Concat(systemOps));

            _analysisAll.Clear();
            _analysisAll.AddRange(scan.Categories);
            RefreshAnalysis();
            ScannedTotalBytes = scan.Snapshot.TotalBytes;
            ScanGaps = ScanCompleteness.Describe(scan);
            // 先分析再清理：扫描完成默认落在空间分析页，用户看完再主动切去清理。
            if (Analysis.Count > 0 || Groups.Count > 0) ContentTabIndex = AnalysisTabIndex;

            ModeLabel = scan.Degraded
                ? "兼容扫描（MFT 已降级：" + scan.DegradeReason + "）"
                : mode == ScanMode.Mft ? "MFT 快速扫描" : "兼容扫描";
            // 占用是确定数字，但可能小于真实占用：有缺口时在状态栏与提示条里都说清楚，
            // 不能让用户把「共 X GB」当成"这就是全部"。
            var gaps = ScanCompleteness.Summarize(scan);
            StatusText =
                $"扫描完成：{scan.Snapshot.TotalFiles:N0} 个对象 / {PlanItem.FormatSize(scan.Snapshot.TotalBytes)}" +
                (gaps is null ? "（统计完整）" : $"（不完整：{gaps}）") +
                $"；硬链接别名 {scan.Stats.Aliases}、重解析点 {scan.Stats.ReparsePoints}、" +
                $"明细按体积保留最大项并省略 {scan.OmittedDetailCount} 条。" +
                (scan.DuplicatesTruncated ? "重复文件检测已按时间预算收尾（覆盖最占空间的前若干组）。" : "") +
                (_cts.IsCancellationRequested ? "（扫描已取消，保留已扫描部分）" : "");

            // 流式结果落库（后台，不阻塞 UI）
            var items = scan.Items.ToList();
            var plans = AllItems.ToList();
            _ = Task.Run(() =>
            {
                try
                {
                    _app.Db.InsertScan(scan.Snapshot);
                    _app.Db.InsertItemsBatched(scan.Snapshot.SnapshotId, items);
                    _app.Db.InsertRecommendations(scan.Snapshot.SnapshotId, plans);
                }
                catch (Exception) { }
            });

            // AI 与扫描解耦：独立任务 + 超时兜底，任何网络等待都不阻塞列表渲染
            if (_app.Enricher is not null && !_cts.IsCancellationRequested)
                StartAiEnrichment(plans, _cts.Token);

            RefreshDisk();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            StatusText = "扫描失败：" + ex.Message;
            _dialogs.Notify(StatusText, warning: true);
        }
        finally
        {
            IsScanning = false;
            ;
        }
    }

    private void StartAiEnrichment(IReadOnlyList<PlanItem> plans, CancellationToken ct)
    {
        var batch = RecommendationEngine.CreateAiBatch(plans, _app.Safety,
            _app.Secrets.GetOrCreateSalt(), _app.Settings.Ai.PrivacyMode,
            _app.Settings.Ai.MaxCandidatesPerScan);
        if (batch is null) return;
        var enricher = _app.Enricher;
        if (enricher is null) return;

        _ = Task.Run(async () =>
        {
            try
            {
                var results = await enricher.EnrichAsync(batch.Digests, ct);
                if (results.Count == 0) return;
                _dispatcher.Invoke(() =>
                {
                    AddAiGroup(RecommendationEngine.MergeAiRecommendations(batch, results, _app.Safety));
                    AiStatusText = "AI 建议已生成 · 默认不勾选、需逐项确认";
                });
            }
            catch (Exception) { }
        });
    }

    [RelayCommand(CanExecute = nameof(CanCancelScan))]
    private void CancelScan()
    {
        _cts?.Cancel();
        StatusText = "正在取消…取消后保留已扫描部分";
    }

    [RelayCommand]
    private void SelectLowRisk()
    {
        foreach (var group in Groups) group.ToggleAll(!group.IsHighRisk);
        RefreshEstimates();
    }

    [RelayCommand(CanExecute = nameof(CanCleanup))]
    private async Task CleanupAsync()
    {
        if (IsScanning) { _dialogs.Notify("请等待扫描结束后再清理。", warning: true); return; }
        var selected = SelectedItems;
        if (selected.Count == 0)
        {
            _dialogs.Notify("请先勾选要清理的项目。高风险项与 AI 建议默认不勾选。");
            return;
        }

        var decision = _dialogs.ShowPreview(selected, _app.Settings);
        if (!decision.Confirmed) return;

        var normal = selected.Where(p => !p.IsSystemOperation).ToList();
        var systemOps = selected.Where(p => p.IsSystemOperation).ToList();
        var options = decision.Permanent
            ? new DeleteOptions(true, decision.ConfirmWord, true, VolumeRoot)
            : DeleteOptions.Recycle(VolumeRoot);

        var messages = new List<string>();
        IReadOnlyList<string> succeededPaths = Array.Empty<string>();
        var executedSystemOps = new List<PlanItem>();
        IsCleaning = true;
        StatusText = $"正在清理 {selected.Count} 项…";
        try
        {
            if (normal.Count > 0)
            {
                // 删除是同步 IO（回收站批量删除 + 逐项核验），留在 UI 线程会让界面
                // 在几秒内完全无响应。放到后台线程，状态栏先给出「正在清理」，并按批回报进度。
                var progress = new Progress<DeleteProgress>(p =>
                    StatusText = p.Done < p.Total
                        ? $"正在清理 {p.Done}/{p.Total} 项…"
                        : $"正在核验清理结果（共 {p.Total} 项）…");
                var outcome = await Task.Run(() => _app.Executor.Execute(normal, CurrentScanId, options, progress));
                succeededPaths = outcome.SucceededPaths ?? Array.Empty<string>();
                messages.Add(outcome.Message);
                foreach (var failure in outcome.Failures.Take(8))
                    messages.Add($"· {failure.Path}：{failure.Reason}");
                StatusText = outcome.Message;
            }

            foreach (var operation in systemOps)
            {
                var result = await _app.Executor.ExecuteSystemOperationAsync(
                    operation, CurrentScanId, acknowledged: decision.Confirmed);
                if (result.Ok) executedSystemOps.Add(operation);
                messages.Add($"[{operation.Description}] {result.Message}");
            }

            // 同步清单：删掉的项移除并取消选中，避免再次点清理时拿到同一批已失效项。
            RemoveCleanedItems(succeededPaths, executedSystemOps);
            RefreshDisk();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                       or ArgumentException or Microsoft.Data.Sqlite.SqliteException)
        {
            // 已成功的删除不回滚：明确告知用户，剩余项可以直接重试。
            ;
            messages.Add($"清理过程中出错：{ex.Message}\n已成功的项不会回滚，可重试剩余项。");
            StatusText = "清理出错，可重试剩余项";
        }
        finally
        {
            IsCleaning = false;
        }
        _dialogs.Report(string.Join("\n", messages), "清理结果");
    }

    [RelayCommand(CanExecute = nameof(CanCleanup))]
    private async Task ElevatedCleanupAsync()
    {
        // 与主清理一致：扫描进行中 Groups 还是旧快照，禁止据其发起 UAC 删除。
        if (IsScanning) { _dialogs.Notify("请等待扫描结束后再提权清理。", warning: true); return; }
        var adminItems = SelectedItems
            .Where(p => p.RequiresAdmin && !p.IsSystemOperation)
            .ToList();
        if (adminItems.Count == 0)
        {
            _dialogs.Notify("请先勾选「需管理员」的清理项，再使用提权清理。\n其他项目无需 UAC，直接点「清理选中项」即可。");
            return;
        }

        IsCleaning = true;
        StatusText = $"正在提权清理 {adminItems.Count} 项（等待 UAC 确认）…";
        try
        {
            var result = await HelperClient.RunAsync(adminItems);
            ;
            // 成功项 = 提交项减去 Helper 明确报失败的路径；与常规清理一样同步清单状态。
            if (result.Available && !result.Degraded && result.Deleted > 0)
            {
                var failed = new HashSet<string>(result.Failed.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
                var success = adminItems.Where(p => !failed.Contains(p.Path)).Select(p => p.Path);
                RemoveCleanedItems(success, Array.Empty<PlanItem>());
            }
            if (result.Degraded) _dialogs.Notify(result.Message, "提权清理", warning: true);
            else _dialogs.Report(result.Message, "提权清理");
            StatusText = result.Message;
            RefreshDisk();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                       or ArgumentException or System.ComponentModel.Win32Exception)
        {
            ;
            StatusText = "提权清理失败";
            _dialogs.Notify("提权清理失败：" + ex.Message, "提权清理", warning: true);
        }
        finally
        {
            IsCleaning = false;
        }
    }

    [RelayCommand]
    private void OpenSettings()
    {
        if (!_dialogs.ShowSettings()) return;
        _app.ReloadAll();
        AiStatusText = _aiCatalog.Status;
        ThemeLabel = "主题：" + ThemeModes.Label(_theme.Mode);
        ThemeName = ThemeModes.Label(_theme.Mode);
        ThemeIndex = ThemeModes.ToIndex(_theme.Mode);
        StatusText = "设置已保存并生效（AI 开关立即生效，无需重启）";
        RefreshDisk();
    }

    /// <summary>主界面一键轮换三态主题，即时换肤并持久化偏好，无需重启。</summary>
    [RelayCommand]
    private void ToggleTheme()
    {
        var next = ThemeModes.Next(_theme.Mode);
        _theme.Apply(next);
        try
        {
            _app.Settings.Theme = next;
            _app.SaveSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or InvalidOperationException or UnsupportedSettingsVersionException)
        {
            // 持久化失败不影响已生效的换肤；不把后台异常抛到 UI 线程。
            ;
        }
        ThemeLabel = "主题：" + ThemeModes.Label(next);
        ThemeName = ThemeModes.Label(next);
        ThemeIndex = ThemeModes.ToIndex(next);
        StatusText = $"已切换为{ThemeModes.Label(next)}（当前皮肤：{ThemeModes.Label(_theme.Variant)}）";
    }

    public void SetPlan(IEnumerable<PlanItem> plans)
    {
        // 旧分组要解绑事件，否则每扫一次都会留下一批再也用不到的分组继续响应勾选
        foreach (var old in Groups) old.PropertyChanged -= OnGroupPropertyChanged;
        Groups.Clear();
        foreach (var g in plans.GroupBy(p => p.Category).OrderByDescending(g => g.Sum(p => p.AllocationSize)))
        {
            bool highRisk = g.Any(p => p.Risk >= RiskLevel.High || p.IsSystemOperation ||
                                       p.Category.Contains("AI") || p.Category.Contains("系统维护"));
            var group = new CategoryGroup(g.Key, g, highRisk);
            AttachGroupActions(group);
            Groups.Add(group);
        }
        HasResults = Groups.Count > 0;
        RefreshEstimates();
        SyncAllSelectedState();
    }

    /// <summary>把「打开位置 / 加入白名单」挂到分组上：分组是每行按钮的 DataContext。</summary>
    private void AttachGroupActions(CategoryGroup group)
    {
        group.PropertyChanged += OnGroupPropertyChanged;
        group.OpenLocationRequested = OnOpenLocation;
        group.WhitelistRequested = OnWhitelist;
    }

    /// <summary>
    /// 展开说明本次扫描哪些内容没统计到。用户看到的占用是确定数字，
    /// 但真实占用可能更大——必须能查到具体是哪些目录/文件，而不是只看到一个计数。
    /// </summary>
    [RelayCommand]
    private void ShowScanGaps()
    {
        if (ScanGaps.Count == 0) return;
        _dialogs.Notify(string.Join("\n\n", ScanGaps), "扫描完整性", warning: true);
    }

    /// <summary>
    /// 弹系统 UAC 以管理员身份重启并自动重扫当前路径。
    /// 新建进程由服务层负责，这里只做决策与失败提示；用户拒绝 UAC 时当前实例保持打开。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRestartAsAdmin))]
    private void RestartAsAdmin()
    {
        if (!CanRestartAsAdmin) return;
        if (!_adminLauncher.RestartAsAdministrator(ScanRoot))
        {
            _dialogs.Notify("未能以管理员身份重启（可能取消了 UAC 提示）。本次扫描结果仍保留。",
                "管理员重扫", warning: true);
            return;
        }
        OnClosing();   // 新实例已启动，当前普通权限实例干净退出
    }

    /// <summary>在资源管理器中定位该项，方便下手前先看清楚是什么。</summary>
    private void OnOpenLocation(PlanItem item)
    {
        if (_locator.OpenLocation(item.Path)) return;
        // 文件可能已经被别的程序删掉了：明确告知，而不是让按钮看起来没反应。
        _dialogs.Notify($"无法打开位置：{item.Path}\n对象可能已被删除或当前不可访问。", "打开位置", warning: true);
    }

    /// <summary>
    /// 把一项加入白名单并立即从清单移除。白名单拥有最高否决权（任何规则与 AI 都不能覆盖），
    /// 因此后续扫描不会再把它列为候选；本次也直接撤销它的勾选，避免用户重复处理同一项。
    /// </summary>
    private void OnWhitelist(PlanItem item)
    {
        var pattern = item.IsDirectory
            ? item.Path.TrimEnd('\\') + "\\**"
            : item.Path;
        var whitelist = _app.Settings.Whitelist;
        if (whitelist.Any(p => string.Equals(p, pattern, StringComparison.OrdinalIgnoreCase)))
        {
            _dialogs.Notify($"{pattern}\n已在白名单中，本次清单里已移除该项。", "加入白名单");
            RemoveCleanedItems(new[] { item.Path }, Array.Empty<PlanItem>());
            return;
        }

        whitelist.Add(pattern);
        try
        {
            _app.SaveSettings();   // 保存后组合根会重载规则与闸门上下文
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or InvalidOperationException or UnsupportedSettingsVersionException)
        {
            whitelist.Remove(pattern);
            _dialogs.Notify("白名单保存失败：" + ex.Message, "加入白名单", warning: true);
            return;
        }
        RemoveCleanedItems(new[] { item.Path }, Array.Empty<PlanItem>());
        StatusText = $"已加入白名单：{pattern} · 后续扫描不再列为候选";
        ;
    }

    /// <summary>
    /// 清理成功后同步清单：真正删掉的项从分组移除并解绑；系统维护项（不在成功路径里）只取消勾选。
    /// 全程抑制逐项通知，结束时统一刷新三态与"预计可释放"，避免"已删项仍选中、再点清理一堆失败"。
    /// </summary>
    public void RemoveCleanedItems(IEnumerable<string> succeededPaths, IEnumerable<PlanItem> submittedSystemOps)
    {
        var success = new HashSet<string>(succeededPaths, StringComparer.OrdinalIgnoreCase);
        var systemPaths = new HashSet<string>(submittedSystemOps.Select(p => p.Path), StringComparer.OrdinalIgnoreCase);

        _bulkSelecting = true;
        try
        {
            for (int gi = Groups.Count - 1; gi >= 0; gi--)
            {
                var group = Groups[gi];
                group.RemoveWhere(success);

                // 系统维护项只取消勾选、保留入口（下次扫描前仍可见，避免用户找不到刚用过的操作）。
                foreach (var item in group.Items)
                    if (systemPaths.Contains(item.Path)) item.IsSelected = false;

                if (group.Items.Count == 0)
                {
                    group.PropertyChanged -= OnGroupPropertyChanged;
                    Groups.RemoveAt(gi);
                }
            }
        }
        finally { _bulkSelecting = false; }

        HasResults = Groups.Count > 0;
        RefreshEstimates();
        SyncAllSelectedState();
    }

    public void AddAiGroup(IEnumerable<PlanItem> plans)
    {
        var existing = Groups.FirstOrDefault(g => g.Name.Contains("AI 建议"));
        foreach (var p in plans)
        {
            if (existing is null)
            {
                existing = new CategoryGroup("AI 建议（逐项确认）", Array.Empty<PlanItem>(), true);
                AttachGroupActions(existing);
                Groups.Add(existing);
            }
            existing.Add(p);
        }
        RefreshEstimates();
    }

    public void RefreshEstimates()
    {
        EstimateText = PlanItem.FormatSize(EstimatedReclaimBytes);
        SelectedCountText = $"{SelectedItems.Count} 项";
        OnPropertyChanged(nameof(EstimatedReclaimBytes));
    }

    public void SetDisk(DiskUsage usage)
    {
        // 无符号数相减：FreeBytes > TotalBytes 时会下溢，先比较再减。
        ulong usedBytes = usage.TotalBytes > usage.FreeBytes ? usage.TotalBytes - usage.FreeBytes : 0;
        double used = usage.TotalBytes == 0 ? 0 : usedBytes * 100d / usage.TotalBytes;
        UsedPercent = Math.Clamp(used, 0, 100);
        DriveLabel = "磁盘 " + usage.Root.TrimEnd('\\').ToUpperInvariant();
        CapacityText = $"已用 {FormatBytes(usedBytes)} / " +
                       $"共 {FormatBytes(usage.TotalBytes)} · " +
                       $"可用 {FormatBytes(usage.FreeBytes)} ({UsedPercent:0.#}%)";
    }

    private static string FormatBytes(ulong bytes) =>
        PlanItem.FormatSize((long)Math.Min(bytes, (ulong)long.MaxValue));

    /// <summary>刷新全部磁盘总览、路径预设与当前盘容量；单次枚举失败不应影响其它功能。</summary>
    private void RefreshDisk()
    {
        IReadOnlyList<DriveDescriptor> drives;
        try { drives = _diskInfo.GetAll(); }
        catch (Exception)
        {
            drives = Array.Empty<DriveDescriptor>();
        }

        string current = VolumeRoot;
        bool all = ScanAllDrives;
        Drives.Clear();
        foreach (var descriptor in drives)
            Drives.Add(new DriveItem(descriptor, all || SameVolume(descriptor.Root, current)));
        HasDrives = Drives.Count > 0;

        ulong totalBytes = 0, freeBytes = 0;
        foreach (var descriptor in drives)
        {
            totalBytes += descriptor.TotalBytes;
            freeBytes += Math.Min(descriptor.FreeBytes, descriptor.TotalBytes);
        }
        AllDrivesSummaryText = drives.Count == 0
            ? "未检测到可用磁盘"
            : $"{drives.Count} 个磁盘 · 共 {FormatBytes(totalBytes)} · 可用 {FormatBytes(freeBytes)}";

        RebuildPathOptions(drives);
        SetDisk(_diskInfo.Get(VolumeRoot));
    }

    /// <summary>
    /// 下拉预设只有「所有磁盘」聚合项 + 各磁盘根目录；控件本身可编辑，
    /// 因此列表之外的任意路径依旧可以直接输入扫描。
    /// 关键：路径集合不变时绝不 Clear —— 可编辑 ComboBox 在 ItemsSource 被清空时
    /// 会把 Text 回写成空串，进而抹掉用户手输的路径和刚点选的磁盘。
    /// </summary>
    private void RebuildPathOptions(IReadOnlyList<DriveDescriptor> drives)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var options = new List<ScanPathOption>
        {
            new(AllDrivesToken, "一次扫描全部就绪磁盘")
        };

        foreach (var descriptor in drives)
        {
            string value = descriptor.Root.Trim();
            if (value.Length == 2 && char.IsLetter(value[0]) && value[1] == ':') value += "\\";
            if (string.IsNullOrWhiteSpace(value) || !seen.Add(value)) continue;
            options.Add(new ScanPathOption(value,
                $"{descriptor.TypeLabel} · 可用 {FormatBytes(descriptor.FreeBytes)}"));
        }

        bool samePaths = ScanPathOptions.Count == options.Count &&
                         ScanPathOptions.Select(o => o.Path)
                             .SequenceEqual(options.Select(o => o.Path), StringComparer.OrdinalIgnoreCase);
        if (samePaths)
        {
            // 同一批卷：只就地更新说明文字，保留选项实例与 ComboBox 的选中项。
            for (int i = 0; i < options.Count; i++)
                if (!string.Equals(ScanPathOptions[i].Hint, options[i].Hint, StringComparison.Ordinal))
                    ScanPathOptions[i].Hint = options[i].Hint;
            return;
        }

        // 卷集合真的变了（插拔移动盘等），重建后把 Clear 期间被回写掉的 Text 还原。
        string currentPath = RootPath;
        ScanPathOptions.Clear();
        foreach (var option in options) ScanPathOptions.Add(option);
        if (!string.Equals(RootPath, currentPath, StringComparison.Ordinal))
            RootPath = currentPath;
    }

}
