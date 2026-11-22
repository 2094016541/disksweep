using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using Sweep.Ai.Abstractions;
using Sweep.App.Views;
using Sweep.Core;
using Sweep.Core.Recommendation;
using Sweep.Core.Scanning;
using Sweep.Core.Storage;

namespace Sweep.App.Services;

/// <summary>
/// WPF 侧适配器：MessageBox / Window / Dispatcher 集中在这里。
/// 这是 App 工程中唯一允许出现 System.Windows 的服务文件（View 的极薄桥接除外）。
/// </summary>
public sealed class WpfDialogService : IDialogService
{
    private readonly CompositionRoot _composition;

    public WpfDialogService(CompositionRoot composition) => _composition = composition;

    private static Window? Owner =>
        Application.Current?.MainWindow is { IsLoaded: true } main ? main : null;

    public void Notify(string message, string title = "Sweep", bool warning = false) =>
        MessageBox.Show(message, title, MessageBoxButton.OK,
            warning ? MessageBoxImage.Warning : MessageBoxImage.Information);

    public void Report(string message, string title = "Sweep") => Notify(message, title);

    public bool Confirm(string message, string title = "Sweep") =>
        MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question)
        == MessageBoxResult.Yes;

    public PreviewDecision ShowPreview(IReadOnlyList<PlanItem> items, AppSettings settings)
    {
        var window = new PreviewWindow { Owner = Owner };
        var vm = _composition.CreatePreviewViewModel(window, items, settings);
        window.Bind(vm);
        window.ShowDialog();
        return vm.Result ?? PreviewDecision.Cancelled;
    }

    public bool ShowSettings()
    {
        var window = new SettingsWindow { Owner = Owner };
        var vm = _composition.CreateSettingsViewModel(window, window);
        window.Bind(vm);
        return window.ShowDialog() == true;
    }

}

public sealed class WpfDispatcher : IUiDispatcher
{
    public void Invoke(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.Invoke(action);
    }
}

public sealed class DiskInfoService : IDiskInfoService
{
    public DiskUsage Get(string root) => DiskInfo.Get(root);
    public IReadOnlyList<DriveDescriptor> GetAll() => DiskInfo.GetAll();
}

/// <summary>
/// 资源管理器定位：文件用 /select 直接选中，目录直接打开。
/// 用 explorer 的原生行为而不是自绘文件列表，用户能顺手看清楚"这条到底是什么"。
/// </summary>
public sealed class WpfShellLocator : IShellLocator
{
    public bool OpenLocation(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            bool isDirectory;
            try { isDirectory = System.IO.Directory.Exists(path); }
            catch { isDirectory = false; }
            if (!isDirectory && !System.IO.File.Exists(path)) return false;

            // explorer 对 /select,"<path>" 这种整体形式解析最稳；路径里不允许出现双引号，否则会被截断。
            var info = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = isDirectory ? "\"" + path + "\"" : "/select,\"" + path + "\"",
                UseShellExecute = true
            };
            System.Diagnostics.Process.Start(info);
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException
                                       or System.IO.IOException or ArgumentException)
        {
            return false;
        }
    }
}

public sealed class AiCatalog : IAiCatalog
{
    public string Status => AiPluginLoader.Status;
    public IReadOnlyList<ProviderDescriptor> GetProviders() => AiPluginLoader.GetProviders();
    public string TranslateRule(string naturalLanguage) => AiPluginLoader.TranslateRule(naturalLanguage);
}

public sealed class ScheduleServiceImpl : IScheduleService
{
    public string Describe(AppSettings settings) => ScheduleService.Describe(settings);
    public string Register(AppSettings settings, string processPath) =>
        ScheduleService.Register(settings, processPath).Message;
    public string Unregister() => ScheduleService.Unregister().Message;
}

/// <summary>
/// 主题适配层：只替换 App.Resources.MergedDictionaries[0] 的令牌字典，
/// 结构样式里的 DynamicResource 引用会自动重解析，所有已打开的窗口即时换肤。
/// </summary>
public sealed class ThemeService : IThemeService
{
    private const int WmSettingChange = 0x001A;
    private const string ImmersiveColorSet = "ImmersiveColorSet";
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>DWM 深色标题栏：20 是 20H1+，19 是 1809–1909 的旧编号。</summary>
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmUseImmersiveDarkModeLegacy = 19;
    private const uint SwpNoMoveNoSizeNoZOrderNoActivateFrameChanged = 0x0037;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter,
        int x, int y, int width, int height, uint flags);

    private static readonly Uri LightTokens = new("Themes/Tokens.Light.xaml", UriKind.Relative);
    private static readonly Uri DarkTokens = new("Themes/Tokens.Dark.xaml", UriKind.Relative);

    public string Mode { get; private set; } = ThemeModes.System;
    public string Variant { get; private set; } = ThemeModes.Dark;
    private bool _applied;

    public void Apply(string mode)
    {
        var normalized = ThemeModes.Normalize(mode);
        var variant = normalized == ThemeModes.System
            ? (IsSystemLight() ? ThemeModes.Light : ThemeModes.Dark)
            : normalized;

        // 幂等：同一偏好解析出同一皮肤时不重复替换字典。
        // 这同时切断了 WM_SETTINGCHANGE 广播可能引发的重入，避免换肤风暴。
        if (_applied && Mode == normalized && Variant == variant) return;

        Mode = normalized;
        Variant = variant;
        _applied = true;

        var dictionaries = Application.Current?.Resources.MergedDictionaries;
        if (dictionaries is null) return;

        var tokens = new ResourceDictionary
        {
            Source = Variant == ThemeModes.Light ? LightTokens : DarkTokens
        };

        SwapTokens(dictionaries, tokens);

        // 非客户区由 Windows 绘制，必须单独告诉 DWM 当前是深色还是浅色，否则深色界面会顶着白色标题栏。
        foreach (Window window in Application.Current!.Windows) ApplyWindowChrome(window);

        // 记录实际生效的画刷值：冒烟时可直接核对换肤确实生效，而非仅进程未崩溃。
        var background = (Application.Current?.Resources["BgBrush"] as SolidColorBrush)?.Color.ToString() ?? "n/a";
        ;
    }

    /// <summary>
    /// 让系统标题栏跟随当前皮肤。判断依据是「实际生效的 BgBrush」而不是用户偏好，
    /// 因此「跟随系统」也能得到一致的标题栏；任何 DWM 调用失败都静默忽略（旧系统上退化为默认标题栏）。
    /// 窗口创建早期 DWM 会话可能尚未就绪，因此 Show 之后再补一次（幂等，不影响已生效的状态）。
    /// 注意：标题栏的具体底色由系统决定——开启「在标题栏上显示强调色」时一律使用强调色，
    /// 应用侧只负责深浅，不去覆盖用户的个性化选择。
    /// </summary>
    public static void ApplyWindowChrome(Window window)
    {
        SetChromeAttributes(window);
        if (window.IsLoaded) return;

        void OnFirstLoaded(object? sender, RoutedEventArgs e)
        {
            window.Loaded -= OnFirstLoaded;
            SetChromeAttributes(window);
        }

        window.Loaded += OnFirstLoaded;
    }

    private static void SetChromeAttributes(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).EnsureHandle();
            if (hwnd == IntPtr.Zero) return;

            bool dark = IsDarkResourceApplied();
            int flag = dark ? 1 : 0;
            if (DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkMode, ref flag, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkModeLegacy, ref flag, sizeof(int));

            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SwpNoMoveNoSizeNoZOrderNoActivateFrameChanged);
        }
        catch
        {
            // 非 Windows 10 1809+ 或 DWM 被禁用时保持系统默认标题栏。
        }
    }

    private static bool IsDarkResourceApplied()
    {
        if (Application.Current?.Resources["BgBrush"] is SolidColorBrush brush)
            return brush.Color.R < 0x60;     // 浅色底 #F4F5F7 → 244；深色底 #0C1015 → 12
        return false;
    }

    /// <summary>
    /// 就地替换令牌字典（按内容探测而非文件名/索引），保留结构样式字典不动。
    /// 这是运行时换肤的核心：替换后所有 DynamicResource 引用会重新解析到新画刷。
    /// 提取为公开纯方法以便在无 Application 的测试进程里直接验证。
    /// </summary>
    public static void SwapTokens(IList<ResourceDictionary> dictionaries, ResourceDictionary tokens)
    {
        const string probeKey = "BgBrush";
        int index = -1;
        for (int i = 0; i < dictionaries.Count; i++)
        {
            if (dictionaries[i].Contains(probeKey)) { index = i; break; }
        }
        if (index >= 0) dictionaries[index] = tokens;
        else dictionaries.Insert(0, tokens);
    }

    /// <summary>AppsUseLightTheme: 1=浅色, 0=深色；注册表不可读时保守回落到浅色。</summary>
    public static bool IsSystemLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("AppsUseLightTheme") is int value ? value != 0 : true;
        }
        catch { return true; }
    }

    /// <summary>
    /// 仅当用户选择「跟随系统」时响应 Windows 主题广播，自动切换皮肤。
    /// 通过窗口的 HwndSource 挂钩，不引入 WinForms 依赖、不创建隐藏窗口。
    /// </summary>
    public static void AttachSystemListener(Window window, IThemeService theme)
    {
        HwndSourceHook hook = (IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (msg == WmSettingChange && theme.Mode == ThemeModes.System)
            {
                try
                {
                    if (Marshal.PtrToStringUni(lParam) == ImmersiveColorSet)
                        _ = window.Dispatcher.BeginInvoke(new Action(() => theme.Apply(ThemeModes.System)));
                }
                catch { /* 系统广播参数异常时忽略 */ }
            }
            return IntPtr.Zero;
        };

        if (PresentationSource.FromVisual(window) is HwndSource source) source.AddHook(hook);
        else window.SourceInitialized += (_, _) =>
        {
            if (PresentationSource.FromVisual(window) is HwndSource created) created.AddHook(hook);
        };
    }
}
