using DiskSweep.App.ViewModels;
using DiskSweep.Core.Recommendation;
using DiskSweep.Core.Storage;

namespace DiskSweep.App.Services;

/// <summary>
/// 单一组合根：所有 ViewModel 都在这里用构造函数注入依赖创建，View 只负责绑定与生命周期桥接。
/// 手工 DI（不引第三方容器）即可保证 ViewModel 可在无 WPF 环境构造并单测。
/// </summary>
public sealed class CompositionRoot
{
    private readonly IAppContext _app;
    private readonly IDialogService _dialogs;
    private readonly IUiDispatcher _dispatcher;
    private readonly IDiskInfoService _diskInfo;
    private readonly IAiCatalog _aiCatalog;
    private readonly IScheduleService _schedule;
    private readonly IThemeService _theme;
    private readonly ISoftwareService _software;
    private readonly ISoftwareUpgradeService _upgrade;
    private readonly IUninstallResidueService _residue;
    private readonly ISoftwareForceRemovalService _forceRemoval;
    private readonly IShellLocator _locator;

    /// <summary>供 App 启动与系统主题广播复用同一个实例。</summary>
    public IThemeService Theme => _theme;

    public CompositionRoot(IAppContext app)
    {
        _app = app;
        _dispatcher = new WpfDispatcher();
        _diskInfo = new DiskInfoService();
        _aiCatalog = new AiCatalog();
        _schedule = new ScheduleServiceImpl();
        _theme = new ThemeService();
        _software = new SoftwareService();
        _upgrade = new WingetUpgradeService();
        _residue = new UninstallResidueService();
        _forceRemoval = new SoftwareForceRemovalService();
        _locator = new WpfShellLocator();
        _dialogs = new WpfDialogService(this);
    }

    public MainViewModel CreateMainViewModel() =>
        new(_app, _dialogs, _dispatcher, _diskInfo, _aiCatalog, _theme, _software, _upgrade, _residue,
            _forceRemoval, _locator, new AdminLauncher());

    public SettingsViewModel CreateSettingsViewModel(IWindowController window, IPasswordSupplier password) =>
        new(_app, _dialogs, _aiCatalog, _schedule, window, password, _theme);

    public PreviewViewModel CreatePreviewViewModel(
        IWindowController window, IReadOnlyList<PlanItem> items, AppSettings settings) =>
        new(_dialogs, window, items, settings);
}
