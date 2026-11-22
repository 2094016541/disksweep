using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace DiskSweep.App.Services;

/// <summary>
/// 以管理员身份重启：走 ShellExecute(runas) 弹系统 UAC，应用自身不绕过、不静默提权。
/// 只负责启动新实例；当前实例由 ViewModel 在收到成功信号后关闭。
/// </summary>
public sealed class AdminLauncher : IAdminLauncher
{
    public bool IsAdministrator
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }
    }

    public bool RestartAsAdministrator(string scanRoot)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe)) return false;
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas",                 // 系统 UAC，用户明确确认后才继续
                Arguments = "--scan \"" + scanRoot.Replace("\"", "") + "\""
            };
            Process.Start(psi);
            return true;
        }
        catch (Win32Exception)      // 用户点了 UAC 的「否」
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}

/// <summary>测试与无 UI 场景的默认实现：始终以「已是管理员」呈现，不提供重启。</summary>
public sealed class NullAdminLauncher : IAdminLauncher
{
    public static readonly NullAdminLauncher Instance = new();
    public bool IsAdministrator => true;
    public bool RestartAsAdministrator(string scanRoot) => false;
}
