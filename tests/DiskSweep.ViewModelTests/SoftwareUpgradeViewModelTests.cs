using DiskSweep.App.Services;
using DiskSweep.App.ViewModels;
using DiskSweep.Core.Software;
using Xunit;

namespace DiskSweep.ViewModelTests;

/// <summary>升级面板的假服务：不启动 winget，只记录请求与查询过的软件源。</summary>
internal sealed class FakeUpgradeService : ISoftwareUpgradeService
{
    public bool IsAvailable { get; set; } = true;
    public string ToolStatus => IsAvailable ? "winget 测试版" : "未检测到 winget";
    public List<WingetPackage> Packages { get; } = new();
    /// <summary>按名称搜索的候选包；自动匹配流程用它。</summary>
    public List<WingetPackage> SearchResults { get; } = new();
    public string? LastSearchQuery { get; private set; }
    public List<string> Versions { get; } = new();
    public Dictionary<string, IReadOnlyList<string>> VersionsBySource { get; } = new();
    /// <summary>按包 ID 给版本，用于「换一个软件后版本列表必须换掉」这类用例。</summary>
    public Dictionary<string, IReadOnlyList<string>> VersionsById { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<WingetSource> Sources { get; } = new()
    {
        new WingetSource("winget", "https://cdn.winget.microsoft.com/cache", false)
    };
    public bool CatalogOk { get; set; } = true;
    public bool VersionsOk { get; set; } = true;
    public SoftwareActionResult LaunchResult { get; set; } = new(true, "已打开 winget 控制台");
    public int LaunchCount { get; private set; }
    public UpgradeRequest? LastRequest { get; private set; }
    public string? LastQueriedId { get; private set; }
    public string? LastSource { get; private set; }
    public bool SourcesQueried { get; private set; }
    /// <summary>添加过哪些软件源；用于断言「启用该源」真的走了提权路径。</summary>
    public List<string> EnabledSources { get; } = new();
    public bool EnableOk { get; set; } = true;
    /// <summary>模拟 winget source list 读不出来。</summary>
    public bool SourcesFail { get; set; }
    /// <summary>测试竞态用：接管版本查询并挂起，按调用顺序分别控制完成时机。</summary>
    public Func<string?, CancellationToken, Task<VersionListResult>>? VersionsHandler { get; set; }

    public Task<SourceListResult> GetSourcesAsync(CancellationToken ct)
    {
        SourcesQueried = true;
        if (SourcesFail)
            return Task.FromResult(new SourceListResult(false, "读取软件源未完成：Win32Exception。",
                Array.Empty<WingetSource>()));
        return Task.FromResult(new SourceListResult(true, "已发现软件源。", Sources.ToArray()));
    }

    /// <summary>添加软件源：成功时把源加进清单，模拟 winget 侧的持久化效果。</summary>
    public Task<SourceActionResult> EnableSourceAsync(WingetSourcePreset preset, CancellationToken ct)
    {
        EnabledSources.Add(preset.Name);
        if (!EnableOk)
            return Task.FromResult(SourceActionResult.Fail(
                "提权窗口已结束，但 winget 里仍没有该源。", "winget source add --name ustc --arg https://x"));

        if (!Sources.Any(s => s.Name.Equals(preset.Name, StringComparison.OrdinalIgnoreCase)))
            Sources.Add(new WingetSource(preset.Name, preset.Url, true));
        return Task.FromResult(new SourceActionResult(true, $"已启用软件源「{preset.DisplayName}」。"));
    }

    public Task<UpgradeCatalogResult> GetUpgradableAsync(CancellationToken ct) =>
        Task.FromResult(new UpgradeCatalogResult(CatalogOk, "winget 报告 1 个软件有可用更新。", Packages.ToArray()));

    /// <summary>按名称搜索候选包：结果由 SearchResults 决定，模拟 winget search。</summary>
    public Task<UpgradeCatalogResult> SearchPackagesAsync(string query, CancellationToken ct)
    {
        LastSearchQuery = query;
        return Task.FromResult(SearchResults.Count == 0
            ? new UpgradeCatalogResult(true, $"winget 源里没有与「{query}」匹配的包。", Array.Empty<WingetPackage>())
            : new UpgradeCatalogResult(true, $"在 winget 源里匹配到 {SearchResults.Count} 个候选包。",
                SearchResults.ToArray()));
    }

    public Task<VersionListResult> GetVersionsAsync(string packageId, string? source, CancellationToken ct)
    {
        LastQueriedId = packageId;
        LastSource = source;
        if (!VersionsOk)
            return Task.FromResult(new VersionListResult(
                false, "读取失败。", Array.Empty<AvailableVersion>(), Array.Empty<string>()));
        if (VersionsHandler is not null) return VersionsHandler(source, ct);

        IEnumerable<KeyValuePair<string, IReadOnlyList<string>>> groups;
        if (source is not null)
        {
            IReadOnlyList<string> list = VersionsBySource.TryGetValue(source, out var found)
                ? found
                : source == "winget" ? VersionsFor(packageId) : Array.Empty<string>();
            groups = new[] { new KeyValuePair<string, IReadOnlyList<string>>(source, list) };
        }
        else if (VersionsBySource.Count > 0)
        {
            groups = VersionsBySource;
        }
        else
        {
            groups = new[] { new KeyValuePair<string, IReadOnlyList<string>>("winget", VersionsFor(packageId)) };
        }

        var merged = WingetSources.MergeVersions(groups);
        string message = $"已扫描 {groups.Count()} 个软件源，汇总 {merged.Count} 个可选版本。";
        return Task.FromResult(new VersionListResult(true, message, merged, Array.Empty<string>()));
    }

    private IReadOnlyList<string> VersionsFor(string packageId) =>
        VersionsById.TryGetValue((packageId ?? "").Trim(), out var byId) ? byId : Versions.ToArray();

    public SoftwareActionResult LaunchUpgrade(UpgradeRequest request)
    {
        LaunchCount++;
        LastRequest = request;
        return LaunchResult;
    }
}

public class SoftwareUpgradeViewModelTests
{
    private static SoftwareEntry Entry(string name, string version = "2.8.45") =>
        new(name, name, version, "Acme", @"C:\Program Files\Acme", "",
            "MsiExec.exe /X{TEST}", 0, SoftwareScope.Machine64, false);

    private static (SoftwareViewModel Vm, FakeDialogs Dialogs, FakeUpgradeService Upgrade) Create(bool winget = true)
    {
        var service = new FakeSoftwareService();
        var dialogs = new FakeDialogs();
        var upgrade = new FakeUpgradeService { IsAvailable = winget };
        var vm = new SoftwareViewModel(service, dialogs, new FakeDispatcher(), upgrade);
        service.Entries.Add(Entry("Apifox 2.8.45"));
        upgrade.Packages.Add(new WingetPackage("Apifox 2.8.45", "Ruihu.Apifox", "2.8.45", "2.8.55", "winget"));
        upgrade.Versions.AddRange(new[] { "2.8.55", "2.8.45", "2.8.40" });
        return (vm, dialogs, upgrade);
    }

    /// <summary>
    /// 按源名定位下拉索引：内置预置源的排列顺序会随目录调整变化，
    /// 测试里写死数字会在增删镜像时集体失效。
    /// </summary>
    private static int SourceIndex(SoftwareViewModel vm, string name) =>
        Enumerable.Range(0, vm.Upgrade.SourceOptions.Count)
            .First(i => !vm.Upgrade.SourceOptions[i].IsAll &&
                        vm.Upgrade.SourceOptions[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static async Task<(SoftwareViewModel Vm, FakeDialogs Dialogs, FakeUpgradeService Upgrade)> Prepared()
    {
        var (vm, dialogs, upgrade) = Create();
        await vm.LoadAsync();
        vm.CheckSoftwareUpgradesCommand.Execute(null);
        vm.SelectedItem = vm.Items[0];
        return (vm, dialogs, upgrade);
    }

    [Fact]
    public async Task CheckUpgrades_MarksMatchingSoftwareWithAvailableVersion()
    {
        var (vm, _, upgrade) = Create();
        await vm.LoadAsync();

        vm.CheckSoftwareUpgradesCommand.Execute(null);

        Assert.Equal("2.8.55", vm.Items[0].AvailableVersion);
        Assert.True(vm.Items[0].HasUpgrade);
        Assert.Contains("已关联 1 个", vm.UpgradeSummaryText);
        Assert.Equal("Ruihu.Apifox", upgrade.Packages[0].Id);
    }

    /// <summary>winget 报告的全部版本都在下拉里，默认选中第一项「最新版」。</summary>
    [Fact]
    public async Task OpenUpgrade_OffersEveryVersionInDropdownAndDefaultsToLatest()
    {
        var (vm, dialogs, upgrade) = await Prepared();

        vm.OpenUpgradeSelectedCommand.Execute(null);

        Assert.True(vm.IsUpgradeVisible);
        Assert.True(vm.Upgrade.IsPanelVisible);
        Assert.Equal("Ruihu.Apifox", vm.Upgrade.PackageId);
        Assert.True(upgrade.SourcesQueried);
        // 源下拉只列 winget 里真正可用的源：所有源 + 已启用的官方源。
        // 没有的镜像不进下拉（选它只会报错），改为「添加镜像」动作。
        Assert.Equal(2, vm.Upgrade.SourceOptions.Count);
        Assert.True(vm.Upgrade.SourceOptions[0].IsAll);
        Assert.DoesNotContain(vm.Upgrade.SourceOptions, c => !c.IsAll && !c.IsConfigured);
        Assert.Contains(vm.Upgrade.SourceOptions, c => c.Name == "winget" && c.IsConfigured);
        Assert.Equal("添加中科大镜像", vm.Upgrade.MirrorActionText);
        Assert.True(vm.Upgrade.CanAddMirror);
        // 国内镜像一个都没启用 → 默认落在「所有软件源」，而不是选一个查不出结果的源。
        Assert.Equal(0, vm.Upgrade.SelectedSourceIndex);
        Assert.True(vm.Upgrade.HasVersions);
        Assert.Equal(4, vm.Upgrade.VersionOptions.Count);      // 最新版 + 3 个真实版本
        Assert.Contains("最新版", vm.Upgrade.VersionOptions[0]);
        Assert.Contains("2.8.55", vm.Upgrade.VersionOptions[1]);
        Assert.Contains("winget", vm.Upgrade.VersionOptions[1]);
        Assert.Equal(0, vm.Upgrade.SelectedVersionIndex);
        Assert.Equal("2.8.55", vm.Upgrade.LatestVersion);
        Assert.Equal("Ruihu.Apifox", upgrade.LastQueriedId);
        Assert.Null(upgrade.LastSource);                      // 默认扫描全部源

        dialogs.ConfirmResult = true;
        vm.Upgrade.StartUpgradeCommand.Execute(null);

        Assert.Equal(1, upgrade.LaunchCount);
        Assert.NotNull(upgrade.LastRequest);
        Assert.True(upgrade.LastRequest!.IsLatest);
        Assert.Null(upgrade.LastRequest.TargetVersion);
    }

    /// <summary>下拉里的每一项都对应一个精确的 --version，并携带该项标注的来源。</summary>
    [Fact]
    public async Task EveryDropdownVersion_BecomesTheExactUpgradeTarget()
    {
        var (vm, dialogs, upgrade) = await Prepared();
        vm.OpenUpgradeSelectedCommand.Execute(null);
        dialogs.ConfirmResult = true;

        foreach (int index in new[] { 1, 2, 3 })
        {
            vm.Upgrade.SelectedVersionIndex = index;
            Assert.True(vm.Upgrade.StartUpgradeCommand.CanExecute(null));
            vm.Upgrade.StartUpgradeCommand.Execute(null);
            Assert.Equal("winget", upgrade.LastRequest!.Source);
            Assert.Contains("--source", upgrade.LastRequest.BuildArguments());
            Assert.Contains("--version", upgrade.LastRequest.BuildArguments());
        }
        Assert.Equal(3, upgrade.LaunchCount);
    }

    /// <summary>所有源并发扫描：同版本跨源各自成为选项，下拉标注来源，升级按所选源带 --source。</summary>
    [Fact]
    public async Task AllSources_MergesVersionsAndCarriesTheChosenSource()
    {
        var (vm, dialogs, upgrade) = await Prepared();
        upgrade.Sources.Add(new WingetSource("msstore", "https://store.example", false));
        upgrade.VersionsBySource["winget"] = new[] { "2.8.55", "2.8.45" };
        upgrade.VersionsBySource["msstore"] = new[] { "2.8.60", "2.8.45" };

        vm.OpenUpgradeSelectedCommand.Execute(null);

        Assert.Equal(3, vm.Upgrade.SourceOptions.Count);
        Assert.Contains(vm.Upgrade.VersionOptions, o => o.StartsWith("2.8.60 · msstore ·"));
        Assert.Contains(vm.Upgrade.VersionOptions, o => o.StartsWith("2.8.55 · winget ·"));
        Assert.Equal(2, vm.Upgrade.VersionOptions.Count(o => o.Contains("2.8.45")));
        Assert.Equal("2.8.60", vm.Upgrade.LatestVersion);

        int msstoreIndex = vm.Upgrade.VersionOptions
            .Select((label, index) => (label, index))
            .First(p => p.label.StartsWith("2.8.60 · msstore ·")).index;
        vm.Upgrade.SelectedVersionIndex = msstoreIndex;
        dialogs.ConfirmResult = true;
        vm.Upgrade.StartUpgradeCommand.Execute(null);

        Assert.Equal("msstore", upgrade.LastRequest!.Source);
        Assert.Equal("2.8.60", upgrade.LastRequest.TargetVersion);
        Assert.Equal(new[] { "upgrade", "--id", "Ruihu.Apifox", "--exact", "--source", "msstore",
                             "--version", "2.8.60", "--interactive" },
                     upgrade.LastRequest.BuildArguments());
    }

    /// <summary>切换软件源会立即按该源重新加载全部版本。</summary>
    [Fact]
    public async Task SwitchingSource_ReloadsVersionsForThatSource()
    {
        var (vm, _, upgrade) = await Prepared();
        upgrade.Sources.Add(new WingetSource("msstore", "https://store.example", false));
        upgrade.VersionsBySource["winget"] = new[] { "2.8.55" };
        upgrade.VersionsBySource["msstore"] = new[] { "2.8.60" };
        vm.OpenUpgradeSelectedCommand.Execute(null);

        vm.Upgrade.SelectedSourceIndex = SourceIndex(vm, "msstore");

        Assert.Equal("msstore", upgrade.LastSource);
        Assert.Contains(vm.Upgrade.VersionOptions, o => o.StartsWith("2.8.60 · msstore ·"));
        Assert.DoesNotContain(vm.Upgrade.VersionOptions, o => o.StartsWith("2.8.55 · winget ·"));
    }

    /// <summary>锁定单个源时，最新版选项也带 --source，但不带 --version。</summary>
    [Fact]
    public async Task SingleSourceLatest_KeepsSourceWithoutVersionArgument()
    {
        var (vm, dialogs, upgrade) = await Prepared();
        vm.OpenUpgradeSelectedCommand.Execute(null);
        vm.Upgrade.SelectedSourceIndex = SourceIndex(vm, "winget");
        dialogs.ConfirmResult = true;

        vm.Upgrade.StartUpgradeCommand.Execute(null);

        Assert.NotNull(upgrade.LastRequest);
        Assert.True(upgrade.LastRequest!.IsLatest);
        Assert.Equal("winget", upgrade.LastRequest.Source);
        Assert.Equal(new[] { "upgrade", "--id", "Ruihu.Apifox", "--exact", "--source", "winget", "--interactive" },
                     upgrade.LastRequest.BuildArguments());
    }

    /// <summary>
    /// 查询中切源：旧查询即使后完成，也不能把新查询的 IsBusy / 版本列表 / 状态覆盖掉。
    /// 这是「所有源并发扫描 + 切源自动重扫」引入的真实竞态，Fake 同步返回时不会暴露，
    /// 所以这里用 TaskCompletionSource 手动控制两次查询的完成顺序。
    /// </summary>
    [Fact]
    public async Task SwitchingSourceDuringQuery_StaleResultCannotOverwriteNewerOne()
    {
        var (vm, _, upgrade) = await Prepared();
        upgrade.Sources.Add(new WingetSource("msstore", "https://store.example", false));
        vm.OpenUpgradeSelectedCommand.Execute(null);

        var first = new TaskCompletionSource<VersionListResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<VersionListResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        upgrade.VersionsHandler = (source, ct) => ++calls == 1 ? first.Task : second.Task;

        vm.Upgrade.SelectedSourceIndex = SourceIndex(vm, "winget");   // winget → 查询 1（挂起）
        Assert.True(vm.Upgrade.IsBusy);
        vm.Upgrade.SelectedSourceIndex = SourceIndex(vm, "msstore");  // msstore → 查询 2（挂起，旧代码会因 IsBusy 直接丢弃）
        Assert.True(vm.Upgrade.IsBusy);
        Assert.Equal(2, calls);

        first.SetResult(new VersionListResult(
            true, "旧源的过期结果", new[] { new AvailableVersion("99.0", "winget") }, Array.Empty<string>()));
        await Task.Delay(30);

        Assert.True(vm.Upgrade.IsBusy);                                   // 新查询仍在跑
        Assert.DoesNotContain("99.0", string.Join("|", vm.Upgrade.VersionOptions));
        Assert.Contains("msstore", vm.Upgrade.StatusText);                // 状态没有被旧结果覆盖

        second.SetResult(new VersionListResult(
            true, "新源的结果", new[] { new AvailableVersion("2.8.60", "msstore") }, Array.Empty<string>()));
        await Task.Delay(30);

        Assert.False(vm.Upgrade.IsBusy);
        Assert.Contains(vm.Upgrade.VersionOptions, o => o.StartsWith("2.8.60 · msstore ·"));
        Assert.Contains("新源的结果", vm.Upgrade.StatusText);
    }

    /// <summary>版本清单没加载出来时不能升级，避免「没得选却直接按最新版启动」。</summary>
    [Fact]
    public async Task WithoutLoadedVersionList_UpgradeCannotStart()
    {
        var (vm, dialogs, upgrade) = await Prepared();
        upgrade.VersionsOk = false;
        vm.OpenUpgradeSelectedCommand.Execute(null);

        Assert.False(vm.Upgrade.HasVersions);
        Assert.Single(vm.Upgrade.VersionOptions);
        Assert.False(vm.Upgrade.StartUpgradeCommand.CanExecute(null));

        dialogs.ConfirmResult = true;
        vm.Upgrade.StartUpgradeCommand.Execute(null);   // 即便绕过 CanExecute 也不启动进程
        Assert.Equal(0, upgrade.LaunchCount);
        Assert.Contains("下拉", vm.Upgrade.StatusText);
    }

    /// <summary>
    /// 关联不上 winget 可升级清单时，按软件名自动搜候选包并直接选中它——
    /// 用户不需要知道包 ID 是什么，更不该被要求手打 "Ruihu.Apifox"。
    /// </summary>
    [Fact]
    public async Task UnlinkedSoftware_AutoSearchesByNameAndSelectsThePackage()
    {
        var (vm, dialogs, upgrade) = await Prepared();
        upgrade.Packages.Clear();
        upgrade.SearchResults.Add(new WingetPackage("Apifox", "Ruihu.Apifox", "2.8.45", "", "winget"));
        vm.CheckSoftwareUpgradesCommand.Execute(null);

        vm.OpenUpgradeSelectedCommand.Execute(null);

        Assert.Equal("Apifox 2.8.45", upgrade.LastSearchQuery);
        Assert.Equal("Ruihu.Apifox", vm.Upgrade.PackageId);
        Assert.Single(vm.Upgrade.PackageOptions);
        Assert.Contains("Ruihu.Apifox", vm.Upgrade.PackageOptions[0].Display);
        Assert.True(vm.Upgrade.HasVersions);
        Assert.Equal(4, vm.Upgrade.VersionOptions.Count);

        dialogs.ConfirmResult = true;
        vm.Upgrade.SelectedVersionIndex = 3;
        vm.Upgrade.StartUpgradeCommand.Execute(null);
        Assert.Equal("2.8.40", upgrade.LastRequest!.TargetVersion);
    }

    /// <summary>winget 源里没有这个软件时，要明确说清「它不由 winget 分发」，而不是留一个空面板。</summary>
    [Fact]
    public async Task UnmatchedSoftware_ExplainsThatWingetDoesNotDistributeIt()
    {
        var (vm, _, upgrade) = await Prepared();
        upgrade.Packages.Clear();
        vm.CheckSoftwareUpgradesCommand.Execute(null);

        vm.OpenUpgradeSelectedCommand.Execute(null);

        Assert.Equal("", vm.Upgrade.PackageId);
        Assert.Single(vm.Upgrade.PackageOptions);
        Assert.Equal("", vm.Upgrade.PackageOptions[0].Id);       // 说明项，不是可选包
        Assert.False(vm.Upgrade.HasVersions);
        Assert.False(vm.Upgrade.StartUpgradeCommand.CanExecute(null));
        Assert.Contains("不是由 winget 分发", vm.Upgrade.StatusText);
    }

    /// <summary>
    /// 换一个软件后，面板必须整体重置：旧查询即使后完成也不能把版本列表 / 状态写进新软件的面板，
    /// 否则用户看到「标题是 B、版本是 A」，点升级还会真的去升级 A。
    /// </summary>
    [Fact]
    public async Task SwitchingToAnotherSoftware_ResetsThePanelInsteadOfKeepingTheOldTarget()
    {
        var service = new FakeSoftwareService();
        var dialogs = new FakeDialogs();
        var upgrade = new FakeUpgradeService();
        var vm = new SoftwareViewModel(service, dialogs, new FakeDispatcher(), upgrade);
        service.Entries.Add(Entry("Apifox 2.8.45"));
        service.Entries.Add(Entry("OtherApp 1.0", "1.0"));
        upgrade.Packages.Add(new WingetPackage("Apifox 2.8.45", "Ruihu.Apifox", "2.8.45", "2.8.55", "winget"));
        upgrade.Versions.AddRange(new[] { "2.8.55", "2.8.45", "2.8.40" });
        upgrade.SearchResults.Add(new WingetPackage("OtherApp 1.0", "Acme.OtherApp", "1.0", "", "winget"));
        upgrade.VersionsById["Acme.OtherApp"] = new[] { "1.1" };
        await vm.LoadAsync();
        vm.CheckSoftwareUpgradesCommand.Execute(null);

        vm.SelectedItem = vm.Items.First(i => i.Name.StartsWith("Apifox"));
        vm.OpenUpgradeSelectedCommand.Execute(null);
        Assert.Equal("Ruihu.Apifox", vm.Upgrade.PackageId);
        Assert.Equal(4, vm.Upgrade.VersionOptions.Count);

        vm.SelectedItem = vm.Items.First(i => i.Name.StartsWith("OtherApp"));
        vm.OpenUpgradeSelectedCommand.Execute(null);

        Assert.Equal("Acme.OtherApp", vm.Upgrade.PackageId);
        Assert.Contains("OtherApp", vm.Upgrade.TargetSummary);
        Assert.Equal("Acme.OtherApp", upgrade.LastQueriedId);
        Assert.Contains(vm.Upgrade.VersionOptions, o => o.StartsWith("1.1 ·"));
        Assert.DoesNotContain(vm.Upgrade.VersionOptions, o => o.Contains("2.8.55"));   // 旧软件的版本不能残留

        dialogs.ConfirmResult = true;
        vm.Upgrade.StartUpgradeCommand.Execute(null);
        Assert.Equal("Acme.OtherApp", upgrade.LastRequest!.PackageId);                // 升级的是新软件，不是旧的
    }

    /// <summary>
    /// 默认源必须落在「已启用的国内镜像」上：官方源在境外，国内拉索引经常几十秒甚至超时。
    /// 一个国内源都没启用时退回「所有软件源」——选一个没配置的源只会让 winget 报错。
    /// </summary>
    [Fact]
    public async Task DefaultSource_IsTheEnabledDomesticMirror()
    {
        var (vm, _, upgrade) = await Prepared();
        upgrade.Sources.Add(new WingetSource("ustc", "https://mirrors.ustc.edu.cn/winget-source", true));
        upgrade.VersionsBySource["ustc"] = new[] { "2.8.55", "2.8.40" };

        vm.OpenUpgradeSelectedCommand.Execute(null);

        Assert.Equal(SourceIndex(vm, "ustc"), vm.Upgrade.SelectedSourceIndex);
        Assert.Equal("ustc", upgrade.LastSource);
        Assert.Contains(vm.Upgrade.VersionOptions, o => o.Contains("ustc"));
        Assert.Equal("中科大镜像 · 已启用", vm.Upgrade.SourceOptions[vm.Upgrade.SelectedSourceIndex].Display);
    }

    /// <summary>
    /// 没添加的镜像不进源下拉（那是「还没加进来」的东西，选它只会报错），
    /// 改为一个明确的「添加镜像」动作，一次只推荐下一个镜像。
    /// </summary>
    [Fact]
    public async Task UnconfiguredMirror_IsOfferedAsAnAddActionNotAsASourceOption()
    {
        var (vm, _, _) = await Prepared();
        vm.OpenUpgradeSelectedCommand.Execute(null);

        Assert.DoesNotContain(vm.Upgrade.SourceOptions, c => !c.IsAll && !c.IsConfigured);
        Assert.True(vm.Upgrade.CanAddMirror);
        Assert.Equal("添加中科大镜像", vm.Upgrade.MirrorActionText);
        Assert.True(vm.Upgrade.EnableSourceCommand.CanExecute(null));
    }

    /// <summary>添加镜像：二次确认 → 提权添加 → 新源出现在下拉里、切到它并按它重新加载版本。</summary>
    [Fact]
    public async Task EnableSource_AddsTheMirrorAndSwitchesToIt()
    {
        var (vm, dialogs, upgrade) = await Prepared();
        upgrade.VersionsBySource["ustc"] = new[] { "2.8.55", "2.8.40" };
        vm.OpenUpgradeSelectedCommand.Execute(null);

        dialogs.ConfirmResult = true;
        await vm.Upgrade.EnableSourceCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "ustc" }, upgrade.EnabledSources);
        Assert.Contains(vm.Upgrade.SourceOptions, c => c.Name == "ustc" && c.IsConfigured);
        Assert.Equal("ustc", upgrade.LastSource);                        // 已按新源重扫版本
        Assert.Equal("中科大镜像 · 已启用",
            vm.Upgrade.SourceOptions[vm.Upgrade.SelectedSourceIndex].Display);
        // 中科大加好了，下一个推荐自然换成教育网镜像。
        Assert.Equal("添加教育网联合镜像", vm.Upgrade.MirrorActionText);
        Assert.Contains("中科大镜像", vm.Upgrade.StatusText);
        Assert.Contains(vm.Upgrade.VersionOptions, o => o.Contains("ustc"));
    }

    [Fact]
    public async Task EnableSource_Cancelled_NeverCallsTheService()
    {
        var (vm, dialogs, upgrade) = await Prepared();
        vm.OpenUpgradeSelectedCommand.Execute(null);

        dialogs.ConfirmResult = false;
        await vm.Upgrade.EnableSourceCommand.ExecuteAsync(null);

        Assert.Empty(upgrade.EnabledSources);
        Assert.Contains("已取消", vm.Upgrade.StatusText);
        Assert.DoesNotContain(vm.Upgrade.SourceOptions, c => c.Name == "ustc");
        Assert.Equal("添加中科大镜像", vm.Upgrade.MirrorActionText);
    }

    /// <summary>提权被拒 / 命令失败时，把可手动执行的命令原样告诉用户，且不谎称已启用。</summary>
    [Fact]
    public async Task EnableSource_Failure_ShowsTheManualCommand()
    {
        var (vm, dialogs, upgrade) = await Prepared();
        upgrade.EnableOk = false;
        vm.OpenUpgradeSelectedCommand.Execute(null);

        dialogs.ConfirmResult = true;
        await vm.Upgrade.EnableSourceCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "ustc" }, upgrade.EnabledSources);
        Assert.DoesNotContain(vm.Upgrade.SourceOptions, c => c.Name == "ustc");
        Assert.Contains("winget source add", vm.Upgrade.StatusText);
        // 失败必须弹一次警告，且文案里带着可手动执行的完整命令。
        Assert.Contains(dialogs.Messages, m => m.Contains("winget source add"));
    }

    /// <summary>
    /// 连 winget 包都没关联上时也要读一次源清单：否则下拉停在上一次/构造时的状态，
    /// 官方源明明配置着却显示不出来，用户会被引导去重复添加。
    /// </summary>
    [Fact]
    public async Task OpeningPanelWithoutPackage_StillReadsSourceList()
    {
        var (vm, _, upgrade) = await Prepared();
        upgrade.Packages.Clear();
        vm.CheckSoftwareUpgradesCommand.Execute(null);

        vm.OpenUpgradeSelectedCommand.Execute(null);

        Assert.Equal("", vm.Upgrade.PackageId);
        Assert.True(upgrade.SourcesQueried);
        Assert.Contains(vm.Upgrade.SourceOptions, c => c.Display == "微软官方源 · 已启用");
        Assert.Single(vm.Upgrade.SourceOptions, c => !c.IsAll);
    }

    /// <summary>
    /// winget 源清单读不出来时，只能确认「所有软件源」这一项；
    /// 既不能把预置源伪装成可选项，也不能推荐添加镜像（不知道有没有已经加过）。
    /// </summary>
    [Fact]
    public async Task UnreadableSourceList_KeepsOnlyAllSourcesAndSaysStateUnknown()
    {
        var (vm, _, upgrade) = await Prepared();
        upgrade.SourcesFail = true;

        vm.OpenUpgradeSelectedCommand.Execute(null);

        Assert.Single(vm.Upgrade.SourceOptions);
        Assert.True(vm.Upgrade.SourceOptions[0].IsAll);
        Assert.False(vm.Upgrade.CanAddMirror);
        Assert.Equal("", vm.Upgrade.MirrorActionText);
        Assert.Contains("状态未知", vm.Upgrade.StatusText);
        Assert.Equal(0, vm.Upgrade.SelectedSourceIndex);            // 状态未知时不锁定任何源
    }

    /// <summary>手写版本号入口已彻底移除，版本只能由下拉决定。</summary>
    [Fact]
    public void UpgradeViewModel_HasNoFreeTextVersionProperty() =>
        Assert.Null(typeof(SoftwareUpgradeViewModel).GetProperty("CustomVersion"));

    [Fact]
    public async Task CancellingConfirmation_NeverLaunchesWinget()
    {
        var (vm, dialogs, upgrade) = await Prepared();
        vm.OpenUpgradeSelectedCommand.Execute(null);

        dialogs.ConfirmResult = false;
        vm.Upgrade.StartUpgradeCommand.Execute(null);

        Assert.Equal(0, upgrade.LaunchCount);
        Assert.Contains("已取消", vm.Upgrade.StatusText);
    }

    [Fact]
    public async Task CancelUpgrade_ClosesThePanel()
    {
        var (vm, _, _) = await Prepared();
        vm.OpenUpgradeSelectedCommand.Execute(null);

        vm.Upgrade.CancelUpgradeCommand.Execute(null);

        Assert.False(vm.IsUpgradeVisible);
        Assert.False(vm.Upgrade.IsPanelVisible);
    }

    [Fact]
    public async Task WithoutWinget_UpgradeCommandsAreDisabled()
    {
        var (vm, _, _) = Create(winget: false);
        await vm.LoadAsync();
        vm.SelectedItem = vm.Items[0];

        Assert.False(vm.CanUseUpgrade);
        Assert.False(vm.CheckSoftwareUpgradesCommand.CanExecute(null));
        Assert.False(vm.OpenUpgradeSelectedCommand.CanExecute(null));
    }

    [Fact]
    public void MainViewModel_WithoutUpgradeService_UsesNullImplementation()
    {
        var main = Fixture.CreateMain();

        Assert.NotNull(main.Software.Upgrade);
        Assert.False(main.Software.CanUseUpgrade);
        Assert.False(main.Software.Upgrade.StartUpgradeCommand.CanExecute(null));
    }
}
