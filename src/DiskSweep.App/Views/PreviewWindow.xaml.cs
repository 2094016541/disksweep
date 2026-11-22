using System.Windows;
using DiskSweep.App.Services;
using DiskSweep.App.ViewModels;

namespace DiskSweep.App.Views;

/// <summary>极薄桥接：绑定 ViewModel，并把确认 / 取消请求转成 DialogResult。</summary>
public partial class PreviewWindow : Window, IWindowController
{
    public PreviewWindow()
    {
        InitializeComponent();
        ThemeService.ApplyWindowChrome(this);
    }

    public void Bind(PreviewViewModel vm) => DataContext = vm;

    public void Close(bool? dialogResult)
    {
        DialogResult = dialogResult;
        Close();
    }
}
