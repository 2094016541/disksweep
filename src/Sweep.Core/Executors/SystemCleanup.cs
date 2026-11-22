using System.ComponentModel;
using System.Diagnostics;

namespace Sweep.Core.Executors;

public sealed record SystemOperationResult(bool Ok, string Message, bool UacRejected);

/// <summary>高风险项只调用系统接口，禁止直接删目录（8.1 红线 2 / 8.5 高风险告知）。</summary>
public static class SystemCleanup
{
    public static readonly IReadOnlyDictionary<string, string> Consequences = new Dictionary<string, string>
    {
        ["dism-startcomponentcleanup"] = "执行后已安装的 Windows 更新将无法卸载，操作不可回退。建议先创建系统还原点。",
        ["windows-old"] = "本产品不直接删除该目录，只会打开 Windows 存储感知让你自行决定。若你在其中删除，将无法回退到上一版本 Windows。",
        ["powercfg-hibernate-off"] = "将失去休眠与快速启动能力，休眠文件不可恢复（只能重新开启休眠）。",
        ["vss-delete-shadows"] = "删除后无法执行系统还原。为避免全部丢失，仅删除最旧的一个还原点。",
        ["recyclebin-empty"] = "清空后回收站内所有文件将无法从回收站还原，且无法回退。"
    };

    public static async Task<SystemOperationResult> ExecuteAsync(string handling, string systemDrive,
        CancellationToken ct = default)
    {
        switch (handling)
        {
            case "recyclebin-empty":
            {
                int hr = ShellExtraNative.SHEmptyRecycleBin(IntPtr.Zero, null,
                    ShellExtraNative.SHERB_NOCONFIRMATION | ShellExtraNative.SHERB_NOPROGRESSUI | ShellExtraNative.SHERB_NOSOUND);
                return hr == 0
                    ? new SystemOperationResult(true, "回收站已清空", false)
                    : new SystemOperationResult(false, $"清空回收站失败（HRESULT 0x{hr:X8}）", false);
            }

            case "dism-startcomponentcleanup":
                return await RunElevated("dism.exe", "/Online /Cleanup-Image /StartComponentCleanup", ct);

            case "powercfg-hibernate-off":
                return await RunElevated("powercfg.exe", "/h off", ct);

            case "vss-delete-shadows":
            {
                var volume = systemDrive.TrimEnd('\\');
                return await RunElevated("vssadmin.exe", $"delete shadows /for={volume} /oldest", ct);
            }

            case "windows-old":
            {
                try
                {
                    Process.Start(new ProcessStartInfo("ms-settings:storagesense") { UseShellExecute = true });
                    return new SystemOperationResult(true,
                        "已打开 Windows「存储感知」。请在其中选择「以前的 Windows 安装」进行删除；本产品不直接删除该目录。", false);
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
                {
                    return new SystemOperationResult(false, "无法打开存储感知设置：" + ex.Message, false);
                }
            }

            default:
                return new SystemOperationResult(false, "未知的系统维护操作，已拒绝执行", false);
        }
    }

    private static async Task<SystemOperationResult> RunElevated(string fileName, string arguments, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo(fileName)
            {
                Arguments = arguments,
                Verb = "runas",
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            using var process = Process.Start(psi);
            if (process is null) return new SystemOperationResult(false, "未启动系统工具（UAC 可能被拒绝）", true);
            await process.WaitForExitAsync(ct);
            return process.ExitCode == 0
                ? new SystemOperationResult(true, $"{fileName} 执行完成", false)
                : new SystemOperationResult(false, $"{fileName} 返回码 {process.ExitCode}", false);
        }
        catch (Win32Exception)
        {
            // UAC 被拒：按未提权降级，不弹错误框（4.6.4）
            return new SystemOperationResult(false, "用户取消了 UAC 提权，已跳过该操作，其余功能不受影响", true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException)
        {
            return new SystemOperationResult(false, "系统工具调用被取消：" + ex.Message, false);
        }
    }
}
