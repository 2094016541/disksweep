using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using DiskSweep.App.Services;
using DiskSweep.App.Views;
using DiskSweep.Core;
using DiskSweep.Core.Executors;
using DiskSweep.Core.Recommendation;
using DiskSweep.Core.Scanning;

namespace DiskSweep.App;

public partial class App : Application
{
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private static Mutex? _mutex;
    public static AppServices? Services { get; private set; }

    /// <summary>单一组合根：ViewModel 的依赖注入入口，View 不再直接访问 App.Services。</summary>
    public static CompositionRoot? Composition { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 同一用户只跑一个实例；第二个实例把焦点转交给第一个（5.3）
        _mutex = new Mutex(true, @"Local\DiskSweep." + Environment.UserName, out bool createdNew);
        if (!createdNew)
        {
            FocusExistingInstance();
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandledException;

        try
        {
            Services = AppServices.Initialize();
            Composition = new CompositionRoot(Services);
            // 任何窗口构造之前应用持久化主题；跟随系统时按 Windows 偏好解析实际皮肤。
            Composition.Theme.Apply(Services.Settings.Theme);
            _ = Task.Run(() =>
            {
                try { Services.Db.PruneOldData(); Services.Db.VacuumIfNeeded(); }
                catch (Exception) { }
            });

            if (e.Args.Contains("--silent", StringComparer.OrdinalIgnoreCase))
            {
                RunSilentCleanup();
                return;
            }

            var main = new MainWindow();
            // --scan <path>：管理员重启后自动扫描指定路径，无需再点一次按钮。
            var startupScan = ScanArgument(e.Args);
            if (startupScan is not null && main.ViewModel is not null)
                main.ViewModel.StartupScanRoot = startupScan;
            ThemeService.AttachSystemListener(main, Composition!.Theme);
            main.Show();
            // 显式指定主窗口与退出模式：关闭主窗口即结束进程。
            MainWindow = main;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
        }
        catch (Exception ex)
        {
            MessageBox.Show("DiskSweep 启动失败：" + ex.Message, "DiskSweep",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    private static string? ScanArgument(string[] args)
    {
        for (int i = 0; i + 1 < args.Length; i++)
            if (string.Equals(args[i], "--scan", StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show("发生未处理的错误，操作已中止。\n" + e.Exception.Message,
            "DiskSweep", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }

    /// <summary>静默清理：默认关闭；只接受 Source=Rule && Risk=Low；绝不弹 UAC / UI（4.7 / 8.1 红线 7）。</summary>
    private async void RunSilentCleanup()
    {
        try
        {
            if (Services is null || !Services.Settings.Schedule.Enabled) { Shutdown(); return; }
            var root = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            // 文档 4.7：只有剩余空间比例低于阈值才真正清理；磁盘健康或容量未知时静默任务直接退出。
            var usage = DiskInfo.Get(Path.GetPathRoot(Path.GetFullPath(root)) ?? @"C:\");
            if (!SchedulePolicy.ShouldRun(usage, Services.Settings.Schedule.MinFreePercent))
            {
                Shutdown();
                return;
            }

            var scan = await ScanService.ScanAsync(root, ScanMode.Walk, Services.Rules,
                CancellationToken.None, null, 1000, Services.Settings.LargeFileThresholdBytes);
            var plan = RecommendationEngine.BuildRulePlan(scan, Services.Rules, Services.Safety);
            var eligible = ScheduleService.FilterSilent(plan);
            if (eligible.Count == 0)
            {
                Shutdown();
                return;
            }
            Services.Executor.Execute(eligible, scan.Snapshot.SnapshotId,
                DeleteOptions.Recycle(root));
        }
        catch (Exception)
        {
        }
        finally { Shutdown(); }
    }

    private static void FocusExistingInstance()
    {
        try
        {
            var other = Process.GetProcessesByName("DiskSweep.App")
                .FirstOrDefault(p => p.Id != Environment.ProcessId && p.MainWindowHandle != IntPtr.Zero);
            if (other is not null)
            {
                ShowWindow(other.MainWindowHandle, 9);   // SW_RESTORE
                SetForegroundWindow(other.MainWindowHandle);
            }
        }
        catch { /* 焦点转交失败时第二个实例直接退出 */ }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
