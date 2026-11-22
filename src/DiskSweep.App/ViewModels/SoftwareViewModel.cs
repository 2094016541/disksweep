using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiskSweep.App.Services;
using DiskSweep.Core.Recommendation;
using DiskSweep.Core.Software;

namespace DiskSweep.App.ViewModels;

/// <summary>软件管理列表的一行；MeasuredBytes 为按需测量的体积，0 表示尚未测量。</summary>
public sealed partial class SoftwareItem : ObservableObject
{
    public SoftwareEntry Entry { get; }

    public string Name => Entry.Name;
    public string Publisher => Entry.PublisherText;
    public string Version => Entry.VersionText;
    public string InstallDate => Entry.DateText;
    public string Location => Entry.LocationText;
    /// <summary>测量与打开位置用的目录：注册值缺失时用推断值。</summary>
    public string EffectiveLocation => Entry.EffectiveLocation;
    public string ScopeText => Entry.ScopeText;
    public string UninstallString => Entry.UninstallString;
    public bool HasLocation => Entry.HasLocation;
    public bool CanUninstall => Entry.CanUninstall;

    /// <summary>不可卸载的具体原因（有卸载命令且未被 NoRemove 标记时为空）。</summary>
    public string UninstallHint => Entry.UninstallHint;

    /// <summary>实际会执行的卸载命令；msiexec 的 /I 已规范化为 /X，让用户看到的就是真正要跑的。</summary>
    public string UninstallPreview
    {
        get
        {
            var command = UninstallCommand.Parse(Entry.UninstallString);
            if (command is null) return Entry.UninstallString;
            return string.IsNullOrWhiteSpace(command.Arguments)
                ? command.FileName : command.FileName + " " + command.Arguments;
        }
    }

    /// <summary>
    /// 占用列的三种状态必须分开：实测（数字）、仅登记估值（≈ 前缀）、测不出来（目录不存在 / 无权限 / 未登记）。
    /// 以前只判断「体积 &gt; 0」，于是真的量到 0 字节、目录已经不存在、从来没量过，在界面上一模一样。
    /// </summary>
    public bool SizeIsMeasured => SizeStatus == MeasureStatus.Measured;
    public bool HasSizeData => SizeIsMeasured || Entry.EstimatedSizeBytes > 0;
    public long EffectiveBytes => SizeIsMeasured ? MeasuredBytes : Entry.EstimatedSizeBytes;

    public string SizeText => SizeStatus switch
    {
        MeasureStatus.Measured => PlanItem.FormatSize(MeasuredBytes),
        MeasureStatus.DirectoryMissing => "目录不存在",
        MeasureStatus.AccessDenied => "无权限",
        MeasureStatus.Failed => "读取失败",
        _ => Entry.EstimatedSizeBytes > 0
            ? "≈ " + PlanItem.FormatSize(Entry.EstimatedSizeBytes)
            : "未登记"
    };

    public string SizeHint => SizeStatus switch
    {
        MeasureStatus.Measured => "实测：安装目录内文件合计（跳过链接与无权限子目录）",
        MeasureStatus.DirectoryMissing => "登记的安装目录不存在，这条登记可能是卸载残留；可用「扫描残留」核对",
        MeasureStatus.AccessDenied => "目录存在但没有读取权限，可在管理员身份下重试",
        MeasureStatus.Failed => "目录读取失败（路径非法或设备未就绪）",
        _ => Entry.EstimatedSizeBytes > 0
            ? (Entry.HasLocation
                ? "≈ 是注册表里的厂商登记值，不是盘上实测值；点「测量占用」可获取真实占用"
                : "≈ 是注册表里的厂商登记值；这条登记没有安装目录，无法实测真实占用")
            : (Entry.HasLocation
                ? "还没有测量过这条软件；点「测量占用」或「测量全部占用」"
                : "卸载登记与图标都没有给出安装目录，无法测量（多为系统共享组件）")
    };

    /// <summary>winget 报告的最新版本；空串表示没有可用更新或尚未检查。</summary>
    public bool HasUpgrade => !string.IsNullOrWhiteSpace(AvailableVersion);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpgrade))]
    private string availableVersion = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeText))]
    [NotifyPropertyChangedFor(nameof(SizeHint))]
    [NotifyPropertyChangedFor(nameof(EffectiveBytes))]
    [NotifyPropertyChangedFor(nameof(SizeIsMeasured))]
    [NotifyPropertyChangedFor(nameof(HasSizeData))]
    private long measuredBytes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeText))]
    [NotifyPropertyChangedFor(nameof(SizeHint))]
    [NotifyPropertyChangedFor(nameof(EffectiveBytes))]
    [NotifyPropertyChangedFor(nameof(SizeIsMeasured))]
    [NotifyPropertyChangedFor(nameof(HasSizeData))]
    private MeasureStatus sizeStatus = MeasureStatus.NotMeasured;

    /// <summary>把一次测量的结果整体落到行上；字节数与状态必须同时更新，不能只写其中一个。</summary>
    public void ApplyMeasurement(MeasureOutcome outcome)
    {
        MeasuredBytes = outcome.Bytes;
        SizeStatus = outcome.Status;
    }

    public SoftwareItem(SoftwareEntry entry) => Entry = entry;
}

/// <summary>卸载残留清单的一行；只有经复核确认为失效的登记才会出现。</summary>
public sealed partial class ResidueItem : ObservableObject
{
    public ResidueEntry Entry { get; }
    public string Name => Entry.Name;
    public string Publisher => string.IsNullOrWhiteSpace(Entry.Publisher) ? "未知发布者" : Entry.Publisher;
    public string Reason => Entry.Reason;
    public string LocationText => string.IsNullOrWhiteSpace(Entry.InstallLocation)
        ? "未登记安装目录" : Entry.InstallLocation;

    [ObservableProperty] private bool isSelected;

    public ResidueItem(ResidueEntry entry) => Entry = entry;
}

/// <summary>强制删除候选的一行；CanDelete=false 的项只展示供核对，不可勾选删除。</summary>
public sealed partial class ForceRemovalItem : ObservableObject
{
    public ForceRemovalCandidate Candidate { get; }
    public string DisplayName => Candidate.DisplayName;
    public string Path => Candidate.Path;
    public string Reason => Candidate.Reason;
    public bool CanDelete => Candidate.CanDelete;
    public string KindText => Candidate.Kind switch
    {
        ForceRemovalKind.Registry => "卸载登记",
        ForceRemovalKind.Directory => "安装目录",
        ForceRemovalKind.AppData => "应用数据",
        _ => "文件"
    };

    [ObservableProperty] private bool isSelected;

    /// <summary>勾选变化时通知父 ViewModel 刷新「强制删除」命令的可执行状态。</summary>
    public event Action? SelectionChanged;

    partial void OnIsSelectedChanged(bool value) => SelectionChanged?.Invoke();

    public ForceRemovalItem(ForceRemovalCandidate candidate) => Candidate = candidate;
}

/// <summary>
/// 软件管理页：读取已安装软件、搜索 / 排序 / 查看安装目录与实际占用、启动官方卸载程序。
/// 所有系统动作经 ISoftwareService，所有弹窗经 IDialogService，ViewModel 可在无 WPF 进程中构造。
/// </summary>
public sealed partial class SoftwareViewModel : ObservableObject
{
    private readonly ISoftwareService _service;
    private readonly ISoftwareUpgradeService _upgrade;
    private readonly IUninstallResidueService _residue;
    private readonly ISoftwareForceRemovalService _forceRemoval;
    private readonly IDialogService _dialogs;
    private readonly IUiDispatcher _dispatcher;
    private readonly List<SoftwareItem> _all = new();
    private IReadOnlyList<WingetPackage> _packages = Array.Empty<WingetPackage>();
    private CancellationTokenSource? _measureCts;
    private CancellationTokenSource? _upgradeCts;

    /// <summary>
    /// 已测结果缓存，键是安装目录（忽略大小写、忽略结尾反斜杠）；值带状态，
    /// 「目录不存在 / 无权限」也缓存下来，刷新列表后不会又变回「未登记」。
    /// 刷新列表、切换排序都会重建 SoftwareItem，但目录体积不会因此变化——
    /// 没有这层缓存，用户每次刷新都要重新走一遍盘。只在本次会话内有效。
    /// </summary>
    private readonly Dictionary<string, MeasureOutcome> _sizes = new(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<SoftwareItem> Items { get; } = new();

    /// <summary>版本升级折叠面板；由软件列表的「升级」按钮打开。</summary>
    public SoftwareUpgradeViewModel Upgrade { get; }
    public bool IsUpgradeVisible => Upgrade.IsPanelVisible;
    public bool CanUseUpgrade => _upgrade.IsAvailable;
    public string UpgradeToolStatus => _upgrade.ToolStatus;

    /// <summary>卸载残留（失效登记）清单：只含卸载程序与安装目录均已不存在的项。</summary>
    public ObservableCollection<ResidueItem> Residues { get; } = new();

    /// <summary>强制删除候选清单：Geek 式全关联扫描结果，逐项勾选（不可删项只展示）。</summary>
    public ObservableCollection<ForceRemovalItem> ForceRemovalCandidates { get; } = new();

    /// <summary>残留清理服务在本机是否可用（无注册表访问时禁用入口）。</summary>
    public bool CanUseResidueCleanup => _residue.IsAvailable;
    public bool HasResidues => Residues.Count > 0;

    /// <summary>强制删除服务可用，且有候选清单时显示面板。</summary>
    public bool CanUseForceRemoval => _forceRemoval.IsAvailable;
    public bool HasForceRemovalCandidates => ForceRemovalCandidates.Count > 0;

    /// <summary>是否成功读取过一次，供主窗口在切到该页签时懒加载。</summary>
    public bool IsLoaded { get; private set; }

    /// <summary>请求主界面把某个安装目录设为扫描根并切回「清理项」页。</summary>
    public event Action<string>? ScanPathRequested;

    [ObservableProperty] private string searchText = "";
    [ObservableProperty] private int sortIndex;
    /// <summary>0 全部 / 1 可卸载 / 2 可升级 / 3 未登记卸载命令 / 4 无占用数据。</summary>
    [ObservableProperty] private int filterIndex;
    [ObservableProperty] private bool isLoading;
    [ObservableProperty] private bool hasItems;
    [ObservableProperty] private string summaryText = "尚未读取已安装软件";
    [ObservableProperty] private string statusText = "点击「刷新列表」读取注册表中登记的已安装软件";
    [ObservableProperty] private string upgradeSummaryText = "尚未检查升级";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MeasureAllSizesCommand), nameof(MeasureSoftwareSizeCommand))]
    private bool isMeasuringAll;

    /// <summary>批量测量的进度文案，直接进状态栏，不改变按钮宽度。</summary>
    [ObservableProperty] private string measureProgressText = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckSoftwareUpgradesCommand))]
    private bool isCheckingUpgrades;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanResiduesCommand), nameof(CleanResiduesCommand))]
    private bool isScanningResidues;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanForceRemovalCommand), nameof(ForceRemoveSelectedCommand))]
    private bool isScanningForceRemoval;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyPropertyChangedFor(nameof(SelectionDetailText))]
    [NotifyPropertyChangedFor(nameof(SelectionLocationText))]
    [NotifyPropertyChangedFor(nameof(SelectionHintText))]
    [NotifyPropertyChangedFor(nameof(HasSelectionHint))]
    [NotifyPropertyChangedFor(nameof(UninstallToolTip))]
    [NotifyCanExecuteChangedFor(nameof(MeasureSoftwareSizeCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenSoftwareLocationCommand))]
    [NotifyCanExecuteChangedFor(nameof(UninstallSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(ScanSoftwareLocationCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenUpgradeSelectedCommand))]
    // 漏掉这个曾让「扫描残留」按钮从启动到关闭一直是灰的：CanExecute 读 SelectedItem，
    // 但选中项变化时没人通知它重新求值。
    [NotifyCanExecuteChangedFor(nameof(ScanForceRemovalCommand))]
    private SoftwareItem? selectedItem;

    /// <summary>
    /// 升级面板打开时切换选中软件，面板必须跟着切到新软件：
    /// 否则用户点完 A 的升级，再在列表里点 B，面板标题、版本列表和「开始升级」都还停留在 A，
    /// 点下去会真的升级 A。这里等价于对新软件重新点了一次「升级」。
    /// </summary>
    partial void OnSelectedItemChanged(SoftwareItem? value)
    {
        if (value is not null && Upgrade.IsPanelVisible)
        {
            Upgrade.Open(value, UpgradeCatalog.Resolve(value.Entry, _packages));
            OnPropertyChanged(nameof(IsUpgradeVisible));
        }
    }

    public bool HasSelection => SelectedItem is not null;

    /// <summary>选中软件后才显示的详情：发布者 / 安装日期 / 来源。</summary>
    public string SelectionDetailText
    {
        get
        {
            var item = SelectedItem;
            if (item is null) return "";
            return string.Join("   ·   ",
                item.Name,
                "发布者 " + Dash(item.Publisher),
                "安装日期 " + Dash(item.InstallDate),
                "来源 " + Dash(item.ScopeText));
        }
    }

    /// <summary>选中软件后才显示的安装目录；未登记时给出简短说明而不是整句占位。</summary>
    public string SelectionLocationText
    {
        get
        {
            var item = SelectedItem;
            if (item is null) return "";
            return item.HasLocation ? "安装目录  " + item.Location : "安装目录  未登记";
        }
    }

    /// <summary>不可卸载时的原因，单独一行显示——灰掉的按钮不解释原因等于没说。</summary>
    public string SelectionHintText => SelectedItem?.UninstallHint ?? "";
    public bool HasSelectionHint => SelectionHintText.Length > 0;

    /// <summary>卸载按钮的提示：可卸载时说明会做什么，不可卸载时说明为什么点不了。</summary>
    public string UninstallToolTip => HasSelectionHint
        ? SelectionHintText
        : "启动软件自带的卸载向导；DiskSweep 不会代替您确认，也不会静默卸载";

    private static string Dash(string? value)
    {
        string text = (value ?? "").Trim();
        return text.Length == 0 || text == "未知发布者" ? "—" : text;
    }

    public SoftwareViewModel(
        ISoftwareService service,
        IDialogService dialogs,
        IUiDispatcher dispatcher,
        ISoftwareUpgradeService? upgrade = null,
        IUninstallResidueService? residue = null,
        ISoftwareForceRemovalService? forceRemoval = null)
    {
        _service = service;
        _dialogs = dialogs;
        _dispatcher = dispatcher;
        _upgrade = upgrade ?? NullSoftwareUpgradeService.Instance;
        _residue = residue ?? NullUninstallResidueService.Instance;
        _forceRemoval = forceRemoval ?? NullSoftwareForceRemovalService.Instance;
        Upgrade = new SoftwareUpgradeViewModel(_upgrade, dialogs);
        Upgrade.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(SoftwareUpgradeViewModel.IsPanelVisible))
                OnPropertyChanged(nameof(IsUpgradeVisible));
        };
    }

    /// <summary>页面生命周期结束时取消尚未完成的目录测量与升级查询（由主窗口转调）。</summary>
    public void CancelPendingWork()
    {
        _measureCts?.Cancel();
        _upgradeCts?.Cancel();
        Upgrade.CancelPendingWork();
    }

    /// <summary>
    /// 页面首次进入时的完整加载：先把清单读出来，再把「还没量过的安装目录」补测掉。
    ///
    /// 为什么首次进入就自动测：注册表里 148 条登记有 65 条没有安装目录、36 条连估值都没有，
    /// 刚打开页面时一列「未登记」，用户根本不知道要再去点「测量全部占用」。
    /// Geek / IObit 也是打开就量。按目录缓存兜底，第二次进来只补新增的目录，耗时接近 0。
    /// 「刷新列表」仍然只重读注册表——刷新按钮不该偷偷跑一遍盘。
    /// </summary>
    public async Task EnsureLoadedAsync()
    {
        if (!IsLoaded) await LoadAsync();
        bool hasUnmeasured = _all.Any(i => i.HasLocation &&
                                           !_sizes.ContainsKey(NormalizeKey(i.EffectiveLocation)));
        if (hasUnmeasured && CanMeasureAll()) await MeasureAllSizesAsync();
    }

    /// <summary>注册表枚举在后台线程执行，结果再回到 UI 线程写入列表。</summary>
    public async Task LoadAsync()
    {
        if (IsLoading) return;
        if (IsMeasuringAll)
        {
            // 静默无反应会被当成卡死：明确告诉用户为什么现在不能刷新。
            StatusText = "正在测量占用，完成或取消后再刷新列表。";
            return;
        }
        IsLoading = true;
        StatusText = "正在读取注册表中的已安装软件…";
        try
        {
            var entries = await Task.Run(_service.ListInstalled);
            _all.Clear();
            int cached = 0;
            foreach (var entry in entries)
            {
                var item = new SoftwareItem(entry);
                // 刷新列表不应该丢掉已经花时间测出来的体积（含「目录不存在」这类结论）。
                if (item.HasLocation && _sizes.TryGetValue(NormalizeKey(item.EffectiveLocation), out var cachedOutcome))
                {
                    item.ApplyMeasurement(cachedOutcome);
                    if (cachedOutcome.IsMeasured) cached++;
                }
                _all.Add(item);
            }
            IsLoaded = true;
            ApplyView();
            if (_packages.Count > 0) ApplyUpgradeInfo();
            int noUninstall = _all.Count(i => !i.CanUninstall);
            StatusText = _all.Count == 0
                ? "未读取到可管理的软件，可打开系统「应用和功能」查看。"
                : $"已读取 {_all.Count} 个软件" +
                  (noUninstall > 0 ? $"（{noUninstall} 项不能在这里卸载）" : "") +
                  (cached > 0 ? $" · 复用 {cached} 项已测体积" : "");
            ;
        }
        catch (Exception ex)
        {
            StatusText = "读取已安装软件失败：" + ex.GetType().Name;
            _dialogs.Notify(StatusText, warning: true);
        }
        finally
        {
            IsLoading = false;
            // 必须在 IsLoading 复位之后再通知：CanMeasureAll 会读 IsLoading，
            // 在 try 里提前通知的话按钮会一直停在「禁用」，直到下一次选中项变化才恢复。
            MeasureAllSizesCommand.NotifyCanExecuteChanged();
            MeasureSoftwareSizeCommand.NotifyCanExecuteChanged();
        }
    }

    partial void OnSearchTextChanged(string value) => ApplyView();
    partial void OnSortIndexChanged(int value) => ApplyView();
    partial void OnFilterIndexChanged(int value) => ApplyView();

    /// <summary>
    /// 就地增量更新可见集合：整体 Clear 会让 DataGrid 丢掉选中项，也会抹掉用户正在看的位置。
    /// 排序 / 搜索都只是重新排列同一批 SoftwareItem 实例，实测体积不会被重置。
    /// </summary>
    private void ApplyView()
    {
        string query = SearchText.Trim();
        IEnumerable<SoftwareItem> view = _all.Where(i => i.Entry.Matches(query));
        view = FilterIndex switch
        {
            1 => view.Where(i => i.CanUninstall),
            2 => view.Where(i => i.HasUpgrade),
            3 => view.Where(i => !i.CanUninstall),
            // 「无占用数据」= 既没实测、也没有登记估值。只筛「没实测」会把 65 条只有估值的行
            // 一起算进来，那批是有数字的，混进去反而找不到真正空缺的几行。
            4 => view.Where(i => !i.HasSizeData),
            _ => view
        };
        view = SortIndex switch
        {
            1 => view.OrderByDescending(i => i.EffectiveBytes).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase),
            2 => view.OrderByDescending(i => i.Entry.InstallDate, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase),
            3 => view.OrderBy(i => i.Publisher, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase),
            _ => view.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
        };
        var ordered = view.ToList();

        for (int i = 0; i < ordered.Count; i++)
        {
            if (i < Items.Count && ReferenceEquals(Items[i], ordered[i])) continue;
            if (i < Items.Count) Items[i] = ordered[i];
            else Items.Add(ordered[i]);
        }
        while (Items.Count > ordered.Count) Items.RemoveAt(Items.Count - 1);

        HasItems = Items.Count > 0;
        SummaryText = BuildSummary(ordered);
        if (SelectedItem is not null && !_all.Contains(SelectedItem)) SelectedItem = null;
    }

    /// <summary>
    /// 合计口径必须说清楚：登记值是厂商写在注册表里的体积（本机 36 项连登记值都没有），
    /// 实测才是盘上真实占用。混在一起报一个数会让人误以为总量已经量准了。
    /// 三种状态各占一句：实测 / 只有登记值 / 连登记值都没有。
    /// 措辞用「登记值」而非「估值」：这不是 DiskSweep 在猜，而是厂商自己登记的参考值。
    /// </summary>
    private string BuildSummary(IReadOnlyList<SoftwareItem> ordered)
    {
        if (_all.Count == 0) return "尚未读取到已安装软件";

        int measured = ordered.Count(i => i.SizeIsMeasured);
        long measuredBytes = ordered.Where(i => i.SizeIsMeasured).Sum(i => i.MeasuredBytes);
        var estimatedItems = ordered.Where(i => !i.SizeIsMeasured && i.Entry.EstimatedSizeBytes > 0).ToArray();
        long estimatedBytes = estimatedItems.Sum(i => i.Entry.EstimatedSizeBytes);
        int noData = ordered.Count(i => !i.HasSizeData);

        // 没有筛选时不必重复「显示 148 / 148 个」，把宽度留给后面的口径说明。
        string head = ordered.Count == _all.Count
            ? $"{_all.Count} 个"
            : $"显示 {ordered.Count} / {_all.Count} 个";

        if (measured == 0)
        {
            // 一项都没量成时别写「尚未测量 · 无数据 N 项」——筛选到几条没有数据的行时，
            // 这个说法会让人以为整份清单都没测。直接说这几条是什么情况。
            if (estimatedItems.Length == 0) return $"{head} · 均无占用数据";
            string text0 = $"{head} · 登记值 {estimatedItems.Length} 项 {PlanItem.FormatSize(estimatedBytes)}";
            return noData > 0 ? text0 + $" · 无数据 {noData} 项" : text0;
        }

        string text = $"{head} · 实测 {measured} 项 {PlanItem.FormatSize(measuredBytes)}";
        if (estimatedItems.Length > 0)
            text += $" · 登记值 {estimatedItems.Length} 项 {PlanItem.FormatSize(estimatedBytes)}";
        if (noData > 0) text += $" · 无数据 {noData} 项";
        return text;
    }

    private bool CanMeasure() => SelectedItem?.HasLocation == true && !IsLoading && !IsMeasuringAll;
    private bool CanMeasureAll() => !IsLoading && !IsMeasuringAll && _all.Any(i => i.HasLocation);
    private bool CanOpenLocation() => SelectedItem?.HasLocation == true;
    private bool CanUninstall() => SelectedItem?.CanUninstall == true;
    private bool CanCheckUpgrades() => _upgrade.IsAvailable && !IsCheckingUpgrades;
    private bool CanOpenUpgrade() => SelectedItem is not null && _upgrade.IsAvailable;

    [RelayCommand]
    private async Task RefreshSoftwareAsync() => await LoadAsync();

    /// <summary>只做只读查询：把 winget 的可升级包与注册表清单按名称关联，不启动任何安装。</summary>
    [RelayCommand(CanExecute = nameof(CanCheckUpgrades))]
    private async Task CheckSoftwareUpgradesAsync()
    {
        if (IsCheckingUpgrades) return;
        IsCheckingUpgrades = true;
        _upgradeCts?.Cancel();
        _upgradeCts = new CancellationTokenSource();
        var ct = _upgradeCts.Token;
        StatusText = "正在通过 winget 检查可用升级…";
        try
        {
            var result = await _upgrade.GetUpgradableAsync(ct);
            if (ct.IsCancellationRequested) return;
            _packages = result.Ok ? result.Packages : Array.Empty<WingetPackage>();
            int matched = ApplyUpgradeInfo();
            UpgradeSummaryText = result.Ok
                ? $"{_packages.Count} 个包可升级 · 已关联 {matched} 个"
                : "升级检查未完成";
            StatusText = result.Ok
                ? result.Message + " 选中软件后点「升级」可选择版本。"
                : result.Message;
            if (!result.Ok) _dialogs.Notify(result.Message, warning: true);
        }
        catch (OperationCanceledException) { StatusText = "已取消升级检查。"; }
        finally { IsCheckingUpgrades = false; }
    }

    private int ApplyUpgradeInfo()
    {
        int matched = 0;
        foreach (var item in _all)
        {
            var package = UpgradeCatalog.Resolve(item.Entry, _packages);
            item.AvailableVersion = package?.AvailableVersion ?? "";
            if (package is not null) matched++;
        }
        ApplyView();
        return matched;
    }

    /// <summary>打开版本选择面板：winget 报告的全部版本都在下拉中，由用户选择目标版本。</summary>
    [RelayCommand(CanExecute = nameof(CanOpenUpgrade))]
    private void OpenUpgradeSelected()
    {
        var item = SelectedItem;
        if (item is null) return;
        if (_packages.Count == 0)
            StatusText = "尚未取得 winget 可升级清单；正在按软件名匹配候选包。";
        Upgrade.Open(item, UpgradeCatalog.Resolve(item.Entry, _packages));
        OnPropertyChanged(nameof(IsUpgradeVisible));
    }

    [RelayCommand(CanExecute = nameof(CanMeasure))]
    private async Task MeasureSoftwareSizeAsync()
    {
        var item = SelectedItem;
        if (item is null || !item.HasLocation) return;

        _measureCts?.Cancel();
        _measureCts = new CancellationTokenSource();
        var ct = _measureCts.Token;
        StatusText = $"正在测量「{item.Name}」的安装目录…";
        try
        {
            var outcome = await _service.MeasureAsync(item.EffectiveLocation, ct);
            _dispatcher.Invoke(() =>
            {
                item.ApplyMeasurement(outcome);
                _sizes[NormalizeKey(item.EffectiveLocation)] = outcome;
                ApplyView();
                StatusText = outcome.IsMeasured
                    ? $"「{item.Name}」安装目录实测 {item.SizeText}。"
                    : $"「{item.Name}」未能测量：{outcome.ProblemText}";
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or ArgumentException or OperationCanceledException)
        {
            StatusText = "目录测量未完成：" + ex.GetType().Name;
        }
        finally { _measureCts = null; }
    }

    /// <summary>
    /// 批量测量：只测有目录的项，已测过的直接复用缓存。
    /// 逐个执行而不是并发发起——单个测量内部已按子目录并行，再叠一层并发只会把盘压满。
    /// 单个失败不中断整批；随时可取消，已测结果保留。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanMeasureAll))]
    private async Task MeasureAllSizesAsync()
    {
        var targets = _all.Where(i => i.HasLocation).ToList();
        if (targets.Count == 0)
        {
            StatusText = "没有登记或推断出安装目录的软件，无法测量。";
            return;
        }

        IsMeasuringAll = true;
        _measureCts?.Cancel();
        _measureCts = new CancellationTokenSource();
        var ct = _measureCts.Token;
        int done = 0, measured = 0, missing = 0, denied = 0, failed = 0;
        try
        {
            foreach (var item in targets)
            {
                if (ct.IsCancellationRequested) break;
                string path = item.EffectiveLocation;
                string key = NormalizeKey(path);
                if (_sizes.TryGetValue(key, out var cached))
                {
                    item.ApplyMeasurement(cached);
                    if (cached.IsMeasured) measured++;
                    done++;
                    continue;
                }

                MeasureProgressText = $"正在测量 {done + 1} / {targets.Count}：{item.Name}";
                StatusText = MeasureProgressText;
                try
                {
                    var outcome = await _service.MeasureAsync(path, ct);
                    if (ct.IsCancellationRequested) break;
                    _sizes[key] = outcome;
                    item.ApplyMeasurement(outcome);
                    switch (outcome.Status)
                    {
                        case MeasureStatus.Measured: measured++; break;
                        case MeasureStatus.DirectoryMissing: missing++; break;
                        case MeasureStatus.AccessDenied: denied++; break;
                        default: failed++; break;
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    failed++;
                }
                done++;
            }

            ApplyView();
            var parts = new List<string> { $"实测 {measured} 项" };
            if (missing > 0) parts.Add($"{missing} 项目录不存在");
            if (denied > 0) parts.Add($"{denied} 项无权限");
            if (failed > 0) parts.Add($"{failed} 项读取失败");
            StatusText = ct.IsCancellationRequested
                ? $"已取消批量测量：完成 {done} / {targets.Count} 项，结果已保留（{string.Join(" · ", parts)}）。"
                : $"批量测量完成：{targets.Count} 项中{string.Join(" · ", parts)}。";
            ;
        }
        finally
        {
            IsMeasuringAll = false;
            MeasureProgressText = "";
            _measureCts = null;
        }
    }

    private static string NormalizeKey(string path) => path.TrimEnd('\\', '/');

    [RelayCommand(CanExecute = nameof(CanOpenLocation))]
    private void OpenSoftwareLocation()
    {
        var item = SelectedItem;
        if (item is null || !item.HasLocation) return;
        var result = _service.OpenLocation(item.EffectiveLocation);
        StatusText = result.Message;
        if (!result.Ok) _dialogs.Notify(result.Message, warning: true);
    }

    [RelayCommand(CanExecute = nameof(CanOpenLocation))]
    private void ScanSoftwareLocation()
    {
        var item = SelectedItem;
        if (item is null || !item.HasLocation) return;
        ScanPathRequested?.Invoke(item.EffectiveLocation);
        StatusText = $"已把「{item.Name}」的安装目录设为扫描路径，切到「清理项」即可开始扫描。";
    }

    /// <summary>卸载是外部副作用：必须显式二次确认，且只启动软件自带的（非静默）卸载向导。</summary>
    [RelayCommand(CanExecute = nameof(CanUninstall))]
    private void UninstallSelected()
    {
        var item = SelectedItem;
        if (item is null || !item.CanUninstall) return;

        bool confirmed = _dialogs.Confirm(
            $"即将启动「{item.Name}」自带的卸载程序。\n\n" +
            "DiskSweep 不会代替您删除文件，也不会静默卸载；后续步骤由该软件的卸载向导完成，" +
            "卸载结果可能无法恢复。\n\n" +
            $"将执行：{item.UninstallPreview}\n\n是否继续？",
            "卸载软件");
        if (!confirmed)
        {
            StatusText = "已取消卸载，未执行任何操作。";
            return;
        }

        var result = _service.LaunchUninstall(item.Entry);
        // 优先走自带卸载程序；卸载向导完成后用户可点「扫描残留」清理遗留文件/登记。
        StatusText = result.Ok
            ? result.Message + " 卸载向导完成后，可点详情区「扫描残留」清理遗留项。"
            : result.Message;
        if (!result.Ok) _dialogs.Notify(result.Message, warning: true);
    }

    [RelayCommand]
    private void OpenAppsAndFeatures()
    {
        var result = _service.OpenAppsAndFeatures();
        StatusText = result.Message;
        if (!result.Ok) _dialogs.Notify(result.Message, warning: true);
    }

    private bool CanScanResidues() => CanUseResidueCleanup && !IsScanningResidues;

    /// <summary>只读扫描：找出“卸载程序与安装目录均已不存在”的失效登记，不删除任何东西。</summary>
    [RelayCommand(CanExecute = nameof(CanScanResidues))]
    private async Task ScanResiduesAsync()
    {
        IsScanningResidues = true;
        StatusText = "正在扫描失效卸载登记…";
        try
        {
            // 注册表枚举 + 文件存在性检查可能耗时，放到后台线程，避免 UI 卡住。
            var entries = await Task.Run(_residue.Scan);
            Residues.Clear();
            foreach (var entry in entries) Residues.Add(new ResidueItem(entry));
            OnPropertyChanged(nameof(HasResidues));
            CleanResiduesCommand.NotifyCanExecuteChanged();
            StatusText = entries.Count == 0
                ? "未发现失效卸载登记；正常软件项不会被列入。"
                : $"发现 {entries.Count} 条失效卸载登记；请逐项确认后再清理，正常软件项不在清单内。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = "扫描失效卸载登记失败：" + ex.Message;
        }
        finally { IsScanningResidues = false; }
    }

    /// <summary>
    /// 清理勾选的失效登记：逐项二次确认，删除前导出 .reg 备份，且服务层删除瞬间再次复核失效状态。
    /// 只删注册表登记项，绝不删除安装目录或执行卸载命令。
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasResidues))]
    private async Task CleanResiduesAsync()
    {
        var selected = Residues.Where(r => r.IsSelected).ToList();
        if (selected.Count == 0)
        {
            _dialogs.Notify("请先勾选要清理的失效登记。");
            return;
        }

        bool confirmed = _dialogs.Confirm(
            $"即将清理 {selected.Count} 条失效卸载登记。\n\n" +
            "仅删除注册表中“卸载程序与安装目录均已不存在”的残留登记；不会删除任何文件、" +
            "不会卸载正在使用的软件。删除前会自动导出 .reg 备份，删除瞬间会再次复核是否仍为失效项。\n\n" +
            "是否继续？",
            "清理失效卸载登记");
        if (!confirmed)
        {
            StatusText = "已取消清理，未删除任何注册表项。";
            return;
        }

        string backupDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DiskSweep", "residue-backups");
        // 每项都要导出 .reg 备份并二次复核，放后台线程避免 UI 冻结。
        var messages = await Task.Run(() =>
        {
            var result = new List<string>();
            foreach (var item in selected)
            {
                var outcome = _residue.RemoveOrphan(item.Entry, backupDir);
                result.Add($"· {item.Name}：{outcome.Message}");
            }
            return result;
        });
        await ScanResiduesAsync();   // 重新读取，移除已删除项
        StatusText = string.Join("\n", messages);
        _dialogs.Report(StatusText, "清理结果");
        foreach (var m in messages) ;
    }

    private bool CanScanForceRemoval() =>
        CanUseForceRemoval && SelectedItem is not null && !IsScanningForceRemoval;

    /// <summary>
    /// Geek 式全关联扫描（只读）：安装目录 + AppData/ProgramData 命名匹配 + 该软件自己的卸载键。
    /// 系统级软件/共享运行时/无关联证据的候选只展示不可勾选，由 Core 判定守护。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanScanForceRemoval))]
    private async Task ScanForceRemovalAsync()
    {
        var item = SelectedItem;
        if (item is null) return;
        IsScanningForceRemoval = true;
        StatusText = $"正在扫描「{item.Name}」的关联残留（只读）…";
        try
        {
            var candidates = await Task.Run(() => _forceRemoval.Scan(item.Entry));
            ForceRemovalCandidates.Clear();
            foreach (var c in candidates)
            {
                var candidateItem = new ForceRemovalItem(c);
                candidateItem.SelectionChanged += () => ForceRemoveSelectedCommand.NotifyCanExecuteChanged();
                ForceRemovalCandidates.Add(candidateItem);
            }
            OnPropertyChanged(nameof(HasForceRemovalCandidates));
            ForceRemoveSelectedCommand.NotifyCanExecuteChanged();

            int deletable = candidates.Count(c => c.CanDelete);
            StatusText = candidates.Count == 0
                ? $"未发现「{item.Name}」的关联残留。"
                : $"发现 {candidates.Count} 项关联（可删 {deletable} / 保留 {candidates.Count - deletable}）；" +
                  "系统级与无明确关联的项只展示、不可勾选。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = "扫描关联残留失败：" + ex.Message;
        }
        finally { IsScanningForceRemoval = false; }
    }

    private bool CanForceRemove() => CanUseForceRemoval && HasForceRemovalCandidates &&
        ForceRemovalCandidates.Any(c => c.IsSelected && c.CanDelete) && !IsScanningForceRemoval;

    /// <summary>
    /// 强制删除（最后手段）：仅处理用户勾选且 CanDelete=true 的项。
    /// 磁盘项进回收站可从系统回收站还原；注册表项先导出 .reg 备份再删。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanForceRemove))]
    private async Task ForceRemoveSelectedAsync()
    {
        var selected = ForceRemovalCandidates.Where(c => c.IsSelected && c.CanDelete).ToList();
        if (selected.Count == 0)
        {
            _dialogs.Notify("请先勾选要强制删除的候选项（系统级与保留项无法勾选）。");
            return;
        }

        int diskCount = selected.Count(c => c.Candidate.Kind != ForceRemovalKind.Registry);
        bool confirmed = _dialogs.Confirm(
            $"即将强制删除 {selected.Count} 项（文件/目录 {diskCount} 项、卸载登记 {selected.Count - diskCount} 项）。\n\n" +
            "⚠ 这是最后手段，仅在软件自带卸载程序失效时使用：\n" +
            "· 文件与目录会移入回收站（可从系统回收站还原，但共享组件仍可能被误删，请逐项核对）\n" +
            "· 注册表项先导出 .reg 备份再删除\n" +
            "· 系统级软件、共享运行时与安全目录外的项已被安全判定排除\n\n" +
            "强制删除不会停止正在运行的进程，被占用文件可能删除失败，建议重启后重试。是否继续？",
            "强制删除（最后手段）");
        if (!confirmed)
        {
            StatusText = "已取消强制删除，未执行任何操作。";
            return;
        }

        string backupDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DiskSweep", "force-removal-backups");
        var candidates = selected.Select(s => s.Candidate).ToList();
        var result = await Task.Run(() => _forceRemoval.Remove(candidates, backupDir));

        StatusText = result.Message;
        _dialogs.Report(result.Message + (result.Messages.Count > 0
            ? "\n" + string.Join("\n", result.Messages.Take(12)) : ""), "强制删除结果");
        foreach (var m in result.Messages) ;

        // 重新扫描，移除已删项
        await ScanForceRemovalAsync();
        if (SelectedItem is not null) await LoadAsync();
    }
}
