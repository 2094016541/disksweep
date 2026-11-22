using System.IO;
using System.Diagnostics;
using System.Globalization;
using Sweep.Core.Recommendation;
using Sweep.Core.Safety;
using Sweep.Core.Storage;

namespace Sweep.App.Services;

/// <summary>4.7 / 8.4：结构化触发器 → 任务计划程序；最低权限注册，默认关闭，静默只清 Rule+Low。</summary>
public static class ScheduleService
{
    public const string TaskName = "Sweep Silent Cleanup";

    public static string Describe(AppSettings settings) =>
        settings.Schedule.Enabled
            ? $"已开启 · {settings.Schedule.Trigger} · 剩余空间低于 {settings.Schedule.MinFreePercent}% 时触发 · 只清低风险规则项"
            : "默认关闭（不注册任何计划任务、不写 Run 键、不开机自启）";

    /// <summary>8.1 红线 7：AI 建议、Review、高风险项一律不进入静默清理。</summary>
    public static IReadOnlyList<PlanItem> FilterSilent(IEnumerable<PlanItem> plans) =>
        plans.Where(p => !p.IsSystemOperation && SafetyGate.IsSilentCleanupAllowed(p.Recommendation)).ToArray();

    public static (bool Ok, string Message) Register(AppSettings settings, string appPath)
    {
        try
        {
            var args = $"/Create /TN \"{TaskName}\" /TR \"\\\"{appPath}\\\" --silent\" " +
                       MapTrigger(settings.Schedule.Trigger) + " /F";
            return RunSchtasks(args, "计划任务已注册（当前用户最低权限，不使用 SYSTEM）");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        { return (false, "注册计划任务失败：" + ex.Message); }
    }

    public static (bool Ok, string Message) Unregister() =>
        RunSchtasks($"/Delete /TN \"{TaskName}\" /F", "计划任务已移除");

    public static string MapTrigger(string trigger)
    {
        var t = (trigger ?? "").Trim();
        if (t.StartsWith("weekly:", StringComparison.OrdinalIgnoreCase))
        {
            var body = t[7..];
            var parts = body.Split('@');
            var day = parts.Length > 0 ? parts[0].ToUpperInvariant() : "MON";
            var time = parts.Length > 1 ? parts[1] : "03:00";
            return $"/SC WEEKLY /D {day} /ST {time}";
        }
        if (t.StartsWith("daily@", StringComparison.OrdinalIgnoreCase))
            return $"/SC DAILY /ST {t[6..]}";
        if (t.StartsWith("idle", StringComparison.OrdinalIgnoreCase))
            return "/SC ONIDLE /I 30";
        return "/SC WEEKLY /D MON /ST 03:00";
    }

    private static (bool Ok, string Message) RunSchtasks(string arguments, string successMessage)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe")
            {
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            if (p is null) return (false, "无法启动 schtasks.exe");
            var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(15_000);
            return p.ExitCode == 0
                ? (true, successMessage)
                : (false, $"schtasks 返回 {p.ExitCode}：{output.Trim()}");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        { return (false, "调用任务计划程序失败：" + ex.Message); }
    }
}
