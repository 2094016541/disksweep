using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sweep.App.Services;
using Sweep.Core.Software;

namespace Sweep.App.ViewModels;

/// <summary>
/// 「版本升级」折叠面板：先确认这次要升级的是哪个 winget 包，再从该源（或全部源）扫描到的
/// 所有版本中下拉选择。每个下拉项都对应一个完整的 UpgradeRequest（含源与版本），
/// 不存在手写版本号旁路；索引 0 是不带 --version 的「最新版」，但仍然携带用户选中的源。
///
/// 包 ID 的来源只有一条：自动匹配。打开面板时先用 winget 的可升级清单关联，
/// 关联不上就按软件名去 winget 源里搜候选包，把候选放进下拉让用户挑——
/// 让普通用户手打 "Ruihu.Apifox" 这种标识符是不可能的，所以界面上没有输入框。
///
/// 软件源只列 winget 里真正可用的源；没添加的国内镜像走单独的「添加镜像」动作，
/// 不在下拉里摆一堆「未启用」。只读查询与向导启动全部经 ISoftwareUpgradeService，
/// 启动前必须二次确认。ViewModel 不引用任何 WPF 类型。
/// </summary>
public sealed partial class SoftwareUpgradeViewModel : ObservableObject
{
    private readonly ISoftwareUpgradeService _service;
    private readonly IDialogService _dialogs;
    /// <summary>与 VersionOptions 一一平行；每项就是启动升级时要用的完整请求。</summary>
    private readonly List<UpgradeRequest> _versionRequests = new();
    private CancellationTokenSource? _queryCts;
    private CancellationTokenSource? _sourceCts;
    private bool _suppressSourceReload;
    private bool _suppressPackageReload;
    /// <summary>最近一次 winget source list 是否读取失败；失败时界面要明说「状态未知」。</summary>
    private bool _sourceStatusUnknown;
    /// <summary>当前面板对应的注册表条目，按名字匹配候选包时要用它做名称归一化。</summary>
    private SoftwareEntry? _entry;
    /// <summary>推荐添加的国内镜像；为 null 表示镜像都已添加（或状态未知）。</summary>
    private WingetSourcePreset? _mirror;

    /// <summary>索引 0 = 所有软件源；其余是 winget 里真正已启用的源。</summary>
    public ObservableCollection<SourceChoice> SourceOptions { get; } = new();

    /// <summary>候选 winget 包；关联不到任何包时放一条说明项（Id 为空）。</summary>
    public ObservableCollection<WingetPackage> PackageOptions { get; } = new();

    /// <summary>winget 报告的所有可用版本都在这里，没有任何旁路输入。</summary>
    public ObservableCollection<string> VersionOptions { get; } = new();

    public string ToolStatus => _service.ToolStatus;
    public string TargetSummary => $"升级「{TargetName}」· 当前版本 {CurrentVersion}";

    public string VersionHintText => string.IsNullOrWhiteSpace(LatestVersion) || LatestVersion == "未知"
        ? "全部可用版本都在下拉中；未匹配到包或未加载版本前不能升级。"
        : $"最新可用版本 {LatestVersion}；全部版本（含来源）在下拉中选择，默认选中最新版。";

    /// <summary>包 ID 存在的意义：它是 winget 用来定位这个软件的唯一标识，版本查询与升级都靠它。</summary>
    public string PackageHintText
    {
        get
        {
            var package = SelectedPackage;
            if (package is null) return "正在匹配 winget 包…";
            if (string.IsNullOrWhiteSpace(package.Id))
                return "没有可用的 winget 包：这个软件不是由 winget 分发的，没有版本清单可选";
            return $"winget 用包 ID 定位软件：{package.Id}；换一个候选会按新包重新读版本";
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetSummary))]
    private string targetName = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetSummary))]
    private string currentVersion = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VersionHintText))]
    private string latestVersion = "";

    /// <summary>当前选中的 winget 包 ID；由上面的候选下拉决定，界面上没有手输入口。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartUpgradeCommand))]
    private string packageId = "";

    [ObservableProperty]
    private int selectedPackageIndex = -1;

    /// <summary>切换源后自动重新扫描该源的全部版本，保持「源 → 版本」联动。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadVersionsCommand))]
    [NotifyCanExecuteChangedFor(nameof(EnableSourceCommand))]
    [NotifyPropertyChangedFor(nameof(SourceHintText))]
    [NotifyPropertyChangedFor(nameof(SelectedSourceLabel))]
    private int selectedSourceIndex;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartUpgradeCommand))]
    private int selectedVersionIndex;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartUpgradeCommand))]
    private bool hasVersions;

    [ObservableProperty] private string statusText = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartUpgradeCommand))]
    [NotifyCanExecuteChangedFor(nameof(LoadVersionsCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelUpgradeCommand))]
    [NotifyCanExecuteChangedFor(nameof(EnableSourceCommand))]
    private bool isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartUpgradeCommand))]
    private bool isPanelVisible;

    public SoftwareUpgradeViewModel(
        ISoftwareUpgradeService service,
        IDialogService dialogs)
    {
        _service = service;
        _dialogs = dialogs;
        RebuildSources(Array.Empty<WingetSource>(), preserveSelection: false);
        RebuildOptions(Array.Empty<AvailableVersion>());
        RefreshDerived();
    }

    /// <summary>当前选中的一项；索引越界时为 null。</summary>
    private SourceChoice? SelectedChoice =>
        SelectedSourceIndex >= 0 && SelectedSourceIndex < SourceOptions.Count
            ? SourceOptions[SelectedSourceIndex]
            : null;

    /// <summary>当前选中的候选包；没有候选时为 null。</summary>
    private WingetPackage? SelectedPackage =>
        SelectedPackageIndex >= 0 && SelectedPackageIndex < PackageOptions.Count
            ? PackageOptions[SelectedPackageIndex]
            : null;

    /// <summary>当前选中的源名；null 表示扫描并合并全部已启用源。</summary>
    private string? SelectedSource
    {
        get
        {
            var choice = SelectedChoice;
            return choice is null || choice.IsAll || !WingetSources.IsValidName(choice.Name)
                ? null
                : choice.Name;
        }
    }

    /// <summary>下拉里当前选中项的可读名字，用于状态文案。</summary>
    public string SelectedSourceLabel => SelectedChoice?.Display ?? "所有软件源";

    /// <summary>选中项的说明，直接作为下拉的悬停提示。</summary>
    public string SourceHintText => SelectedChoice?.Note ?? "选择一个软件源；默认优先国内镜像。";

    /// <summary>推荐添加的国内镜像；都添加过了就是 null。</summary>
    public string MirrorActionText => _mirror is null ? "" : "添加" + _mirror.DisplayName;

    /// <summary>只有「还有国内镜像没添加」时才出现这个动作。</summary>
    public bool CanAddMirror => _mirror is not null && _service.IsAvailable && !IsBusy;

    /// <summary>页面关闭 / 切走时取消尚未完成的版本查询与源列表刷新。</summary>
    public void CancelPendingWork()
    {
        _queryCts?.Cancel();
        _sourceCts?.Cancel();
    }

    /// <summary>由软件列表的「升级」按钮打开；known 为空时按名称自动匹配候选包。</summary>
    public void Open(SoftwareItem item, WingetPackage? known)
    {
        // 换一个软件必须先把上一个软件的状态清干净：旧查询还在飞的时候，
        // 它的结果会落到新软件的面板上，用户看到的就是「标题是 B、版本列表是 A」，
        // 点「开始升级」还会真的去升级 A。
        CancelPendingWork();
        _queryCts = null;
        _sourceCts = null;
        _entry = item.Entry;
        TargetName = item.Name;
        CurrentVersion = item.Version;
        LatestVersion = "未知";
        PackageOptions.Clear();
        SelectedPackageIndex = -1;
        PackageId = "";
        IsPanelVisible = true;
        RebuildOptions(Array.Empty<AvailableVersion>());
        HasVersions = false;
        StatusText = "";

        if (!_service.IsAvailable)
        {
            StatusText = _service.ToolStatus + "；可继续使用「系统卸载设置」手动更新。";
            return;
        }

        if (known is not null)
        {
            SetPackages(new[] { known });
            SelectPackage(known.Id);
            StatusText = $"已关联 {known.Id}，正在读取软件源与该源的全部版本…";
            _ = InitializeAsync();
            return;
        }

        StatusText = $"正在 winget 软件源里按名称匹配「{item.Name}」…";
        _ = ResolvePackageAsync(item);
    }

    /// <summary>
    /// 按名称找候选包：先查 winget 的可升级清单（只含「有更新」的包），再查全量搜索。
    /// 找到就用名称相似度挑最像的那个并直接加载版本，用户不需要知道包 ID 是什么。
    /// </summary>
    private async Task ResolvePackageAsync(SoftwareItem item)
    {
        _queryCts?.Cancel();
        var cts = new CancellationTokenSource();
        _queryCts = cts;
        UpgradeCatalogResult search;
        try
        {
            search = await _service.SearchPackagesAsync(item.Name, cts.Token);
        }
        catch (OperationCanceledException) { return; }
        if (!ReferenceEquals(_queryCts, cts)) return;
        _queryCts = null;

        if (!search.Ok || search.Packages.Count == 0)
        {
            SetPackages(Array.Empty<WingetPackage>());
            PackageOptions.Add(new WingetPackage(
                search.Ok ? $"没有匹配「{item.Name}」的 winget 包" : "winget 查询失败", "", "", "", ""));
            SelectedPackageIndex = 0;
            PackageId = "";
            await LoadSourcesAsync(preserveSelection: false);
            StatusText = search.Ok
                ? $"winget 源里没有与「{item.Name}」匹配的包：它不是由 winget 分发的，所以没有版本清单可选。" +
                  "可以改用软件自带的检查更新，或打开「系统卸载设置」。"
                : search.Message + " 可稍后重试。";
            return;
        }

        SetPackages(search.Packages);
        var best = UpgradeCatalog.Resolve(item.Entry, search.Packages) ?? search.Packages[0];
        SelectPackage(best.Id);
        StatusText = search.Message;
        await InitializeAsync();
    }

    /// <summary>把候选包写进下拉：就地更新，避免整体 Clear 让 ComboBox 把选中项回写成 -1。</summary>
    private void SetPackages(IReadOnlyList<WingetPackage> packages)
    {
        _suppressPackageReload = true;
        PackageOptions.Clear();
        foreach (var package in packages) PackageOptions.Add(package);
        SelectedPackageIndex = -1;
        _suppressPackageReload = false;
        OnPropertyChanged(nameof(PackageHintText));
    }

    private void SelectPackage(string id)
    {
        int index = -1;
        for (int i = 0; i < PackageOptions.Count; i++)
            if (PackageOptions[i].Id.Equals(id, StringComparison.OrdinalIgnoreCase)) { index = i; break; }
        _suppressPackageReload = true;
        SelectedPackageIndex = index;
        PackageId = index >= 0 ? PackageOptions[index].Id : "";
        _suppressPackageReload = false;
        OnPropertyChanged(nameof(PackageHintText));
    }

    /// <summary>用户换了一个候选包：按新包重新读全部版本，别让版本列表停留在旧包上。</summary>
    partial void OnSelectedPackageIndexChanged(int value)
    {
        OnPropertyChanged(nameof(PackageHintText));
        if (_suppressPackageReload || !IsPanelVisible) return;
        var package = SelectedPackage;
        PackageId = package?.Id ?? "";
        if (!UpgradeRequest.IsValidId(PackageId) || !_service.IsAvailable) return;
        _ = LoadVersionsAsync();
    }

    /// <summary>先发现源再扫描版本：这样「所有源」的确切范围来自 winget 自己的配置。</summary>
    private async Task InitializeAsync()
    {
        await LoadSourcesAsync(preserveSelection: false);
        // 一个国内镜像都没添加时主动说一句：国内用户走官方源拉索引经常几十秒甚至超时，
        // 「有这么个更快的源，只是还没加进来」这件事必须让人看得见。
        if (_mirror is not null)
        {
            string hint = $"尚未添加国内镜像（推荐{_mirror.DisplayName}：点「{MirrorActionText}」）";
            if (!StatusText.Contains("国内镜像"))
                StatusText = StatusText.Length > 0 ? hint + "。" + StatusText : hint + "。";
        }

        if (UpgradeRequest.IsValidId(PackageId)) await LoadVersionsAsync();

        // 版本查询会改写状态栏，把「源状态未知」这条结论补回去，别让它被覆盖掉。
        if (_sourceStatusUnknown && !StatusText.Contains("状态未知"))
            StatusText = "软件源状态未知（winget source list 读取失败）。" + StatusText;
    }

    private bool CanLoadVersions() =>
        _service.IsAvailable && !IsBusy && UpgradeRequest.IsValidId(PackageId);

    private bool CanEnableSource() => CanAddMirror;

    /// <summary>
    /// 读取 winget 的源清单，就地更新下拉。preserveSelection=true 时按源名找回原来的选中项
    /// ——添加镜像之后要停在刚加进来的那个源上。
    /// </summary>
    private async Task LoadSourcesAsync(bool preserveSelection)
    {
        _sourceCts?.Cancel();
        var cts = new CancellationTokenSource();
        _sourceCts = cts;
        try
        {
            var result = await _service.GetSourcesAsync(cts.Token);
            if (!ReferenceEquals(_sourceCts, cts)) return;
            RebuildSources(result.Ok ? result.Sources : null, preserveSelection, result.Ok);
            _sourceStatusUnknown = !result.Ok;
            if (!result.Ok)
                StatusText = result.Message + " 软件源状态未知，可在管理员终端里执行 winget source list 核对。";
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(_sourceCts, cts)) _sourceCts = null;
        }
    }

    /// <summary>
    /// 就地更新源下拉：顺序固定为 所有源 → 国内镜像 → 官方源 → 商店源 → 其它已启用源。
    /// 未添加的预置源不进下拉（选它只会报错），改为「添加镜像」动作；
    /// 默认落在已启用的国内镜像上，没有就退回「所有软件源」。
    /// </summary>
    private void RebuildSources(IEnumerable<WingetSource>? configured, bool preserveSelection,
        bool statusKnown = true)
    {
        string previousName = preserveSelection ? SelectedSource ?? "" : "";
        var enabled = (configured ?? Array.Empty<WingetSource>()).Select(s => s.Name).ToArray();
        var choices = WingetSources.BuildChoices(configured, WingetSourceCatalog.Presets, statusKnown);
        _mirror = statusKnown ? WingetSourceCatalog.FirstUnconfiguredDomestic(enabled) : null;

        _suppressSourceReload = true;
        for (int i = 0; i < choices.Count; i++)
        {
            if (i < SourceOptions.Count) SourceOptions[i] = choices[i];
            else SourceOptions.Add(choices[i]);
        }
        while (SourceOptions.Count > choices.Count) SourceOptions.RemoveAt(SourceOptions.Count - 1);

        int index = 0;
        if (previousName.Length > 0)
        {
            int found = IndexOf(previousName);
            if (found > 0) index = found;
        }
        else
        {
            string? recommended = WingetSourceCatalog.Recommend(enabled);
            int found = recommended is null ? -1 : IndexOf(recommended);
            if (found > 0) index = found;
        }

        SelectedSourceIndex = index;
        _suppressSourceReload = false;
        RefreshDerived();
    }

    /// <summary>镜像推荐与按钮文案都依赖 RebuildSources 的结果，改完要统一通知一遍。</summary>
    private void RefreshDerived()
    {
        OnPropertyChanged(nameof(MirrorActionText));
        OnPropertyChanged(nameof(CanAddMirror));
        OnPropertyChanged(nameof(SourceHintText));
        OnPropertyChanged(nameof(SelectedSourceLabel));
        EnableSourceCommand.NotifyCanExecuteChanged();
    }

    private int IndexOf(string name)
    {
        for (int i = 0; i < SourceOptions.Count; i++)
            if (!SourceOptions[i].IsAll &&
                SourceOptions[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    /// <summary>
    /// 把推荐的国内镜像加入 winget。用 runas 提权打开可见控制台执行 winget source add，
    /// 用户在 UAC 弹窗里点「是」才是真正授权；成功后自动切到该源并按它重新加载版本。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanEnableSource))]
    private async Task EnableSourceAsync()
    {
        var preset = _mirror;
        if (preset is null) return;

        bool confirmed = _dialogs.Confirm(
            $"即将把软件源「{preset.DisplayName}」加入 winget。\n\n" +
            $"名称：{preset.Name}\n地址：{preset.Url}\n" +
            $"命令：winget {string.Join(" ", WingetSourceCatalog.AddArguments(preset, null))}\n\n" +
            "这一步会弹出 UAC 提权窗口，需要管理员权限；winget 的源列表是全局配置，" +
            "添加后对当前用户的所有 winget 操作生效。Sweep 不会代替您同意任何协议。\n\n是否继续？",
            "添加国内镜像");
        if (!confirmed)
        {
            StatusText = "已取消添加镜像，未做任何修改。";
            return;
        }

        IsBusy = true;
        StatusText = $"正在添加软件源「{preset.DisplayName}」，请在 UAC 窗口确认…";
        try
        {
            var result = await _service.EnableSourceAsync(preset, CancellationToken.None);
            StatusText = result.Message;
            if (!result.Ok)
            {
                if (result.Command.Length > 0)
                    StatusText += $" 可手动执行：{result.Command}";
                _dialogs.Notify(StatusText, warning: true);
                return;
            }

            ;
            await LoadSourcesAsync(preserveSelection: true);
            // 添加后直接把选中项切到刚加进来的源，后续版本查询就走它。
            int index = IndexOf(preset.Name);
            if (index > 0) SelectedSourceIndex = index;
            await LoadVersionsAsync();
            // 版本查询会改写状态栏：把「已启用」这条结论补回去，
            // 否则用户只看到版本数量，不知道源到底加成功了没有。
            StatusText = result.Message + " " + StatusText;
        }
        catch (OperationCanceledException) { StatusText = "已取消添加软件源。"; }
        finally
        {
            IsBusy = false;
            RefreshDerived();
        }
    }

    /// <summary>
    /// 切换源后立即重新扫描该源的全部版本；由 RebuildSources 触发时用守卫跳过。
    /// 即使上一次查询还在进行也要发起新查询：LoadVersionsAsync 会取消旧查询，
    /// 若在这里因 IsBusy 直接返回，下拉会显示新源而版本列表仍是旧源的，两者不一致。
    /// </summary>
    partial void OnSelectedSourceIndexChanged(int value)
    {
        OnPropertyChanged(nameof(SourceHintText));
        OnPropertyChanged(nameof(SelectedSourceLabel));
        if (_suppressSourceReload || !IsPanelVisible) return;
        if (!UpgradeRequest.IsValidId(PackageId) || !_service.IsAvailable) return;
        _ = LoadVersionsAsync();
    }

    /// <summary>没有加载出版本清单、或下拉没有有效选中项时，不允许启动升级。</summary>
    private bool CanStartUpgrade() =>
        IsPanelVisible && !IsBusy && _service.IsAvailable && UpgradeRequest.IsValidId(PackageId)
        && HasVersions && SelectedVersionIndex >= 0 && SelectedVersionIndex < _versionRequests.Count;

    /// <summary>扫描选中源（或全部源）中所有可用版本，全部填入下拉供用户选择。</summary>
    [RelayCommand(CanExecute = nameof(CanLoadVersions))]
    private async Task LoadVersionsAsync()
    {
        string id = PackageId.Trim();
        if (!UpgradeRequest.IsValidId(id))
        {
            StatusText = "还没有匹配到可用的 winget 包，无法读取版本清单。";
            return;
        }

        string? source = SelectedSource;
        _queryCts?.Cancel();
        var cts = new CancellationTokenSource();
        _queryCts = cts;
        var ct = cts.Token;
        IsBusy = true;
        StatusText = $"正在从{(source is null ? "全部软件源" : "软件源 " + source)}读取 {id} 的所有版本…";
        try
        {
            var result = await _service.GetVersionsAsync(id, source, ct);
            // 只有「当前这一次」查询能改动结果：被新查询取代后，旧结果不得覆盖新源的版本列表。
            if (!ReferenceEquals(_queryCts, cts) || ct.IsCancellationRequested) return;

            string newest = result.Versions.FirstOrDefault()?.Version ?? "";
            if (!string.IsNullOrWhiteSpace(newest)) LatestVersion = newest;
            RebuildOptions(result.Versions);
            StatusText = result.Ok
                ? result.Message + " 请从下拉选择目标版本（含来源，默认最新版）。"
                : result.Message + " 请换一个软件源或候选包后重试；目标版本只能从下拉选择。";
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_queryCts, cts)) StatusText = "已取消读取可用版本。";
        }
        finally
        {
            // 旧查询被取消时不能把新查询的 IsBusy 提前清掉，否则用户能在查询未完成时点「开始升级」。
            if (ReferenceEquals(_queryCts, cts)) IsBusy = false;
        }
    }

    /// <summary>
    /// 就地更新下拉项：整体 Clear 会让 ComboBox 把 SelectedIndex 回写成 -1，也会丢失用户选择。
    /// 重建后显式把索引归零，恢复「默认选中最新版」。
    /// </summary>
    private void RebuildOptions(IReadOnlyList<AvailableVersion> versions)
    {
        string sourceLabel = SelectedSource ?? "所有源";
        string latestLabel = string.IsNullOrWhiteSpace(LatestVersion) || LatestVersion == "未知"
            ? $"最新版 · {sourceLabel} · 由 winget 决定"
            : $"最新版（{LatestVersion}）· {sourceLabel} · 由 winget 决定";

        var labels = new List<string> { latestLabel };
        var requests = new List<UpgradeRequest>();
        if (UpgradeRequest.TryCreate(PackageId, null, SelectedSource, out _) is { } latest)
            requests.Add(latest);

        foreach (var choice in versions)
        {
            string relation = SoftwareVersion.DescribeRelation(choice.Version, CurrentVersion);
            labels.Add($"{choice.Version} · {choice.Source} · {relation}");
            if (UpgradeRequest.TryCreate(PackageId, choice.Version, choice.Source, out _) is { } request)
                requests.Add(request);
        }

        for (int i = 0; i < labels.Count; i++)
        {
            if (i < VersionOptions.Count) VersionOptions[i] = labels[i];
            else VersionOptions.Add(labels[i]);
        }
        while (VersionOptions.Count > labels.Count) VersionOptions.RemoveAt(VersionOptions.Count - 1);

        _versionRequests.Clear();
        _versionRequests.AddRange(requests);
        SelectedVersionIndex = 0;
        HasVersions = versions.Count > 0 && requests.Count > versions.Count;
    }

    /// <summary>升级是外部副作用：显式二次确认后，只启动可见的 winget 交互向导。</summary>
    [RelayCommand(CanExecute = nameof(CanStartUpgrade))]
    private void StartUpgrade()
    {
        if (!CanStartUpgrade())
        {
            StatusText = "请先匹配到 winget 包并加载版本，再从下拉选择目标版本。";
            return;
        }

        var request = _versionRequests[SelectedVersionIndex];
        string target = request.IsLatest
            ? $"最新版（{LatestVersion}，由 winget 决定）"
            : $"指定版本 {request.TargetVersion}";
        string source = string.IsNullOrWhiteSpace(request.Source) ? "所有源中由 winget 决定" : request.Source;
        bool confirmed = _dialogs.Confirm(
            $"即将从软件源「{source}」升级「{TargetName}」到 {target}。\n\n" +
            $"命令：{request.Describe()}\n\n" +
            "Sweep 只会在可见控制台中启动 winget 交互式升级：不会静默安装、不会自动接受协议、" +
            "不会代替您确认，也不会自行下载或替换文件。后续下载与安装由 winget 和软件安装向导完成，" +
            "可能需要管理员权限，且结果可能无法恢复。\n\n是否继续？",
            "升级软件");
        if (!confirmed)
        {
            StatusText = "已取消升级，未执行任何操作。";
            return;
        }

        var result = _service.LaunchUpgrade(request);
        StatusText = result.Message;
        if (!result.Ok) _dialogs.Notify(result.Message, warning: true);
    }

    [RelayCommand]
    private void CancelUpgrade()
    {
        CancelPendingWork();
        IsPanelVisible = false;
        StatusText = "";
    }
}
