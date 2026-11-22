using System.Windows;
using Sweep.App.ViewModels;

namespace Sweep.App;

/// <summary>
/// 极薄的 View：只做 DataContext 装配与窗口生命周期事件转发。
/// 状态与全部交互命令都在 MainViewModel，删除相关的安全逻辑在 Core 层。
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>供 App.OnStartup 注入 --scan 参数（管理员重启后自动续扫）。</summary>
    public MainViewModel? ViewModel => DataContext as MainViewModel;

    public MainWindow()
    {
        InitializeComponent();
        Services.ThemeService.ApplyWindowChrome(this);
        var vm = App.Composition!.CreateMainViewModel();
        DataContext = vm;
        Loaded += (_, _) => vm.InitializeCommand.Execute(null);
        Closing += (_, _) => vm.OnClosing();
    }
}
