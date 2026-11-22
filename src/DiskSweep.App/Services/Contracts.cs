using DiskSweep.Ai.Abstractions;
using DiskSweep.Core;
using DiskSweep.Core.Executors;
using DiskSweep.Core.Recommendation;
using DiskSweep.Core.Rules;
using DiskSweep.Core.Storage;
using DiskSweep.Core.Safety;
using DiskSweep.Core.Software;

namespace DiskSweep.App.Services;

/// <summary>
/// 组合根的只读/受控门面。ViewModel 只认识这个接口，不认识 App 静态类、Window 或 Dispatcher，
/// 因此可以在没有 WPF 消息循环的测试进程里直接构造（文档 3.2「App │ WPF / MVVM」）。
/// </summary>
public interface IAppContext
{
    DiskSweepDb Db { get; }
    SecretStore Secrets { get; }
    AiStorageAdapter AiStorage { get; }
    LoadedSettings Loaded { get; }
    AppSettings Settings { get; }
    RuleEngine Rules { get; }
    SafetyContext Safety { get; }
    DeleteExecutor Executor { get; }
    IRecommendationEnricher? Enricher { get; }
    bool AiEnabled { get; }
    string ProviderOverridesPath { get; }
    void ReloadAll();
    void SaveSettings();
    void DisableAiImmediately();
}

/// <summary>预览清单的结果。View 只负责展示，确认词与永久删除开关由 ViewModel 校验。</summary>
public sealed record PreviewDecision(bool Confirmed, bool Permanent, string? ConfirmWord)
{
    public static PreviewDecision Cancelled { get; } = new(false, false, null);
}

/// <summary>所有弹窗、消息框与子窗口的唯一出口；ViewModel 不直接 new Window。</summary>
public interface IDialogService
{
    void Notify(string message, string title = "DiskSweep", bool warning = false);
    void Report(string message, string title = "DiskSweep");
    bool Confirm(string message, string title = "DiskSweep");
    PreviewDecision ShowPreview(IReadOnlyList<PlanItem> items, AppSettings settings);
    bool ShowSettings();
}

/// <summary>把后台任务的结果切回 UI 线程。</summary>
public interface IUiDispatcher
{
    void Invoke(Action action);
}

/// <summary>
/// 以管理员身份重启应用并自动扫描指定路径。进程/UAC 细节留在服务层，
/// ViewModel 只决定「要不要重启」，测试用假实现替换。
/// </summary>
public interface IAdminLauncher
{
    bool IsAdministrator { get; }
    /// <summary>返回 false 表示用户拒绝了 UAC 或无法启动；调用方需给出提示且不关闭当前实例。</summary>
    bool RestartAsAdministrator(string scanRoot);
}

/// <summary>
/// 在资源管理器中定位路径（文件选中、目录打开）。清理清单里「打开位置」用它，
/// ViewModel 因此不需要认识 Process / explorer，测试里可替换成空实现。
/// </summary>
public interface IShellLocator
{
    bool OpenLocation(string path);
}

/// <summary>无 Windows 会话时的空实现（单元测试 / 服务不可用）。</summary>
public sealed class NullShellLocator : IShellLocator
{
    public static NullShellLocator Instance { get; } = new();
    public bool OpenLocation(string path) => false;
}

/// <summary>子窗口 ViewModel 请求关闭窗口的通道，由极薄的 View 桥接实现。</summary>
public interface IWindowController
{
    void Close(bool? dialogResult);
}

/// <summary>PasswordBox.Password 不是依赖属性；由 View 以三行桥接提供，避免密钥进入绑定系统。</summary>
public interface IPasswordSupplier
{
    string ReadPassword();
}


public interface IDiskInfoService
{
    DiskUsage Get(string root);
    /// <summary>所有已就绪卷，用于主界面磁盘总览与扫描路径预设。</summary>
    IReadOnlyList<DriveDescriptor> GetAll();
}

public interface IAiCatalog
{
    string Status { get; }
    IReadOnlyList<ProviderDescriptor> GetProviders();
    string TranslateRule(string naturalLanguage);
}

public interface IScheduleService
{
    string Describe(AppSettings settings);
    string Register(AppSettings settings, string processPath);
    string Unregister();
}

/// <summary>运行时换肤：替换应用级资源字典，无需重启窗口。Mode 是用户偏好，Variant 是实际皮肤。</summary>
public interface IThemeService
{
    string Mode { get; }
    string Variant { get; }
    void Apply(string mode);
}

/// <summary>软件管理动作的结果；Message 直接展示给用户，不把 Win32 异常泄漏到界面。</summary>
public sealed record SoftwareActionResult(bool Ok, string Message);

/// <summary>
/// 软件管理的唯一 Windows 系统出口：读取卸载键、测量安装目录、打开目录与启动官方卸载程序。
/// ViewModel 只认识这个接口，因此注册表与进程启动都可在测试中替换成假实现。
/// </summary>
public interface ISoftwareService
{
    IReadOnlyList<SoftwareEntry> ListInstalled();
    /// <summary>测量安装目录；返回状态而不是裸 long，才能区分「量到 0 字节」和「目录已经不在了」。</summary>
    Task<MeasureOutcome> MeasureAsync(string path, CancellationToken ct);
    SoftwareActionResult OpenLocation(string path);
    SoftwareActionResult LaunchUninstall(SoftwareEntry entry);
    SoftwareActionResult OpenAppsAndFeatures();
}

/// <summary>winget 的一次只读查询结果；Message 可直接展示给用户。</summary>
public sealed record UpgradeCatalogResult(
    bool Ok, string Message, IReadOnlyList<WingetPackage> Packages);

/// <summary>winget 当前配置的软件源清单；源名用于 --source 参数。</summary>
public sealed record SourceListResult(
    bool Ok, string Message, IReadOnlyList<WingetSource> Sources);

/// <summary>
/// 指定包在软件源中的可用版本。Versions 从新到旧、逐源保留；
/// FailedSources 记录本次扫描失败的源，供界面单独提示而不掩盖成功结果。
/// </summary>
public sealed record VersionListResult(
    bool Ok, string Message, IReadOnlyList<AvailableVersion> Versions, IReadOnlyList<string> FailedSources);

/// <summary>
/// 软件版本升级的唯一系统出口：发现软件源、查询可升级包与可用版本、启动可见的 winget 升级向导。
/// 查询可后台执行；启动升级必须由 ViewModel 在用户二次确认之后调用。
/// GetVersionsAsync 的 source 为空表示扫描全部可用源。
/// </summary>
public interface ISoftwareUpgradeService
{
    bool IsAvailable { get; }
    string ToolStatus { get; }
    Task<SourceListResult> GetSourcesAsync(CancellationToken ct);
    Task<UpgradeCatalogResult> GetUpgradableAsync(CancellationToken ct);
    /// <summary>
    /// 按名称在 winget 源里查找候选包。<c>winget upgrade</c> 只报告「有更新」的包，
    /// 用户想给某个没出现在那份清单里的软件选版本时，必须能先按名字找到它的包 ID。
    /// </summary>
    Task<UpgradeCatalogResult> SearchPackagesAsync(string query, CancellationToken ct);
    Task<VersionListResult> GetVersionsAsync(string packageId, string? source, CancellationToken ct);
    /// <summary>
    /// 把预置源加入 winget（<c>winget source add</c>）。这一步需要管理员权限，
    /// 实现在用户确认后以 UAC 提权启动，失败时把可手动执行的命令原样告诉用户。
    /// </summary>
    Task<SourceActionResult> EnableSourceAsync(WingetSourcePreset preset, CancellationToken ct);
    SoftwareActionResult LaunchUpgrade(UpgradeRequest request);
}

/// <summary>添加软件源的结果；Command 是失败时给用户手动执行的完整命令。</summary>
public sealed record SourceActionResult(bool Ok, string Message, string Command = "")
{
    public static SourceActionResult Fail(string message, string command = "") => new(false, message, command);
}

/// <summary>一条“失效卸载登记”的展示信息；删除动作只作用于这类已确认为残留的键。</summary>
public sealed record ResidueEntry(
    string RegistryKey,
    string Name,
    string Publisher,
    string UninstallString,
    string InstallLocation,
    string Reason);

/// <summary>
/// 卸载残留清理的唯一系统出口：只读扫描失效卸载登记；删除前导出 .reg 备份，
/// 并在删除瞬间重新判定仍为失效（TOCTOU），只删失效键、绝不碰正常软件项与安装目录。
/// </summary>
public interface IUninstallResidueService
{
    bool IsAvailable { get; }
    IReadOnlyList<ResidueEntry> Scan();
    SoftwareActionResult RemoveOrphan(ResidueEntry entry, string backupDirectory);
}

/// <summary>
/// 强制删除（Geek 式扫描全关联）的唯一系统出口。默认不使用——仅当用户主动选择、
/// 且软件自带卸载程序失效时才走这里。扫描只读；删除时磁盘项进回收站（可从系统回收站还原），
/// 注册表键先导出 .reg 备份；系统级软件/共享运行时/安全区外路径由 Core 判定一律拒绝。
/// </summary>
public interface ISoftwareForceRemovalService
{
    bool IsAvailable { get; }
    /// <summary>只读扫描该软件的关联候选（安装目录 + AppData/ProgramData 命名匹配 + 自己的卸载键）。</summary>
    IReadOnlyList<ForceRemovalCandidate> Scan(SoftwareEntry entry);
    /// <summary>删除勾选的候选；只有 CanDelete=true 的项会被处理。磁盘项走回收站，注册表项先备份。</summary>
    ForceRemovalResult Remove(IReadOnlyList<ForceRemovalCandidate> candidates, string backupDirectory);
}
