using System.Windows;
using Sweep.App.Services;
using Sweep.App.ViewModels;

namespace Sweep.App.Views;

/// <summary>
/// 极薄桥接：绑定 ViewModel；PasswordBox.Password 不是依赖属性，由 IPasswordSupplier 单独读取，
/// 密钥不进入 WPF 绑定系统，也不写进 ViewModel 状态。
/// </summary>
public partial class SettingsWindow : Window, IWindowController, IPasswordSupplier
{
    public SettingsWindow()
    {
        InitializeComponent();
        ThemeService.ApplyWindowChrome(this);
    }

    public void Bind(SettingsViewModel vm) => DataContext = vm;

    public string ReadPassword() => TxtApiKey.Password;

    public void Close(bool? dialogResult)
    {
        DialogResult = dialogResult;
        Close();
    }
}
