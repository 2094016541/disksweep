using Sweep.Ai.Abstractions;
using Sweep.App.Services;
using Sweep.App.ViewModels;
using Sweep.Core;
using Sweep.Core.Executors;
using Sweep.Core.Recommendation;
using Sweep.Core.Rules;
using Sweep.Core.Safety;
using Sweep.Core.Software;
using Sweep.Core.Storage;

namespace Sweep.ViewModelTests;

/// <summary>ViewModel 只需这些接口即可构造；无需 Application、Dispatcher 或任何 Window。</summary>
internal sealed class FakeContext : IAppContext
{
    public int ReloadCount { get; private set; }
    public int DisableAiCount { get; private set; }
    public int SaveCount { get; private set; }

    public SweepDb Db => null!;
    public SecretStore Secrets => null!;
    public AiStorageAdapter AiStorage => null!;
    public LoadedSettings Loaded => null!;
    public AppSettings Settings { get; } = new();
    public RuleEngine Rules => null!;
    public SafetyContext Safety => null!;
    public DeleteExecutor Executor => null!;
    public IRecommendationEnricher? Enricher => null;
    public bool AiEnabled => false;
    public string ProviderOverridesPath => "providers.json";

    public void ReloadAll() => ReloadCount++;
    public void SaveSettings()
    {
        if (SaveThrows) throw new InvalidOperationException("模拟保存失败");
        SaveCount++;
    }
    public void DisableAiImmediately() => DisableAiCount++;

    /// <summary>让保存失败，用于验证白名单写入失败时不会留下半截状态。</summary>
    public bool SaveThrows { get; set; }
}

internal sealed class FakeShellLocator : IShellLocator
{
    public List<string> Opened { get; } = new();
    public bool Result { get; set; } = true;
    public bool OpenLocation(string path)
    {
        Opened.Add(path);
        return Result;
    }
}

internal sealed class FakeDialogs : IDialogService
{
    public List<string> Messages { get; } = new();
    /// <summary>最近一次确认框的正文，供“确认文案里显示了什么”的断言使用。</summary>
    public string? LastConfirmMessage { get; private set; }
    public bool ConfirmResult { get; set; }
    public bool SettingsResult { get; set; }
    public PreviewDecision PreviewResult { get; set; } = PreviewDecision.Cancelled;

    public void Notify(string message, string title = "Sweep", bool warning = false) => Messages.Add(message);
    public void Report(string message, string title = "Sweep") => Messages.Add(message);
    public bool Confirm(string message, string title = "Sweep")
    {
        LastConfirmMessage = message;
        return ConfirmResult;
    }
    public PreviewDecision ShowPreview(IReadOnlyList<PlanItem> items, AppSettings settings) => PreviewResult;
    public bool ShowSettings() => SettingsResult;
}

internal sealed class FakeWindow : IWindowController
{
    public bool? ClosedWith { get; private set; }
    public int CloseCount { get; private set; }
    public void Close(bool? dialogResult) { ClosedWith = dialogResult; CloseCount++; }
}

internal sealed class FakeDispatcher : IUiDispatcher
{
    public void Invoke(Action action) => action();
}

internal sealed class FakeDiskInfo : IDiskInfoService
{
    public List<DriveDescriptor> Drives { get; } = new()
    {
        new(@"C:\", "系统盘", "本地磁盘", 100, 40),
        new(@"D:\", "数据盘", "本地磁盘", 200, 150),
        new(@"E:\", "备份盘", "本地磁盘", 500, 500)
    };

    public DiskUsage Get(string root) => new(root, 100, 40);
    public IReadOnlyList<DriveDescriptor> GetAll() => Drives.ToArray();
}

internal sealed class FakeAiCatalog : IAiCatalog
{
    public string Status => "AI 未启用（测试）";
    public IReadOnlyList<ProviderDescriptor> GetProviders() => Array.Empty<ProviderDescriptor>();
    public string TranslateRule(string naturalLanguage) => "dsl";
}

internal sealed class FakeSchedule : IScheduleService
{
    public string Describe(AppSettings settings) => "默认关闭";
    public string Register(AppSettings settings, string processPath) => "已注册";
    public string Unregister() => "已移除";
}

internal sealed class FakePassword : IPasswordSupplier
{
    public string ReadPassword() => "";
}

internal sealed class FakeTheme : IThemeService
{
    public string Mode { get; private set; } = ThemeModes.System;
    public string Variant => Mode == ThemeModes.System ? ThemeModes.Dark : Mode;
    public int ApplyCount { get; private set; }
    public void Apply(string mode) { Mode = ThemeModes.Normalize(mode); ApplyCount++; }
}

internal static class Fixture
{
    public static PlanItem Plan(
        string path,
        string category = "临时文件",
        RiskLevel risk = RiskLevel.Low,
        RecommendationSource source = RecommendationSource.Rule,
        RecommendationAction action = RecommendationAction.Delete,
        bool isSystem = false,
        long size = 4096,
        bool isDirectory = false) => new()
    {
        Path = path,
        Recommendation = new Recommendation(path, source, action, risk, 1.0, "测试依据", "temp-user"),
        Category = category,
        AllocationSize = size,
        VolumeSerial = 0,
        FileId = 0,
        IsDirectory = isDirectory,
        Handling = "glob-delete",
        RequiresAdmin = false,
        Description = "测试项",
        ReclaimNote = "",
        IsSystemOperation = isSystem,
        LowReclaimEstimate = false,
        AgeDays = 30,
        DefaultSelected = true,
        IsSelected = true
    };

    public static MainViewModel CreateMain(FakeContext? context = null, FakeDialogs? dialogs = null,
        FakeTheme? theme = null, FakeDiskInfo? diskInfo = null, FakeShellLocator? locator = null,
        FakeAdminLauncher? adminLauncher = null) =>
        new(context ?? new FakeContext(), dialogs ?? new FakeDialogs(),
            new FakeDispatcher(), diskInfo ?? new FakeDiskInfo(), new FakeAiCatalog(),
            theme ?? new FakeTheme(), NullSoftwareService.Instance, null, null, null, locator, adminLauncher);
}

internal sealed class FakeAdminLauncher : IAdminLauncher
{
    public bool IsAdministrator { get; set; }
    public bool RestartResult { get; set; } = true;
    public int RestartCount { get; private set; }
    public string? LastRoot { get; private set; }

    public bool RestartAsAdministrator(string scanRoot)
    {
        RestartCount++;
        LastRoot = scanRoot;
        return RestartResult;
    }
}

/// <summary>强制删除服务的假实现：记录扫描/删除调用，返回可配置的候选与结果。</summary>
internal sealed class FakeForceRemovalService : ISoftwareForceRemovalService
{
    public bool IsAvailable => true;
    public IReadOnlyList<ForceRemovalCandidate> Candidates { get; set; } = Array.Empty<ForceRemovalCandidate>();
    public ForceRemovalResult RemoveResult { get; set; } =
        new(true, "已删除 1 项", 1, 0, Array.Empty<string>());
    public int ScanCount { get; private set; }
    public int RemoveCount { get; private set; }
    public IReadOnlyList<ForceRemovalCandidate>? RemovedCandidates { get; private set; }

    public IReadOnlyList<ForceRemovalCandidate> Scan(SoftwareEntry entry)
    {
        ScanCount++;
        return Candidates;
    }

    public ForceRemovalResult Remove(IReadOnlyList<ForceRemovalCandidate> candidates, string backupDirectory)
    {
        RemoveCount++;
        RemovedCandidates = candidates;
        return RemoveResult;
    }
}
