using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using Sweep.Core.Software;

namespace Sweep.App.Services;

/// <summary>
/// 软件管理的 Windows 适配层：只读注册表、测量目录体积、启动官方卸载程序。
/// 这里绝不删除文件、绝不写注册表，也绝不执行 QuietUninstallString / 带静默参数的命令。
/// </summary>
public sealed class SoftwareService : ISoftwareService
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    private static readonly (RegistryHive Hive, RegistryView View, SoftwareScope Scope)[] Hives =
    {
        (RegistryHive.LocalMachine, RegistryView.Registry64, SoftwareScope.Machine64),
        (RegistryHive.LocalMachine, RegistryView.Registry32, SoftwareScope.Machine32),
        (RegistryHive.CurrentUser, RegistryView.Default, SoftwareScope.CurrentUser)
    };

    /// <summary>保护区只算一次：每条登记都会用它筛候选目录，重复取特殊文件夹既慢又无谓。</summary>
    private static readonly SoftwarePathGuard PathGuard = SoftwarePathGuard.Default;

    public IReadOnlyList<SoftwareEntry> ListInstalled()
    {
        var found = new List<SoftwareEntry>();
        foreach (var (hive, view, scope) in Hives)
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = root.OpenSubKey(UninstallPath);
                if (uninstall is null) continue;

                foreach (string keyName in uninstall.GetSubKeyNames())
                {
                    var entry = ReadOne(uninstall, keyName, scope);
                    if (entry is not null) found.Add(entry);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                           or System.Security.SecurityException or ArgumentException
                                           or PlatformNotSupportedException)
            {
                // 单个注册表视图不可读（权限 / 平台）不影响其它视图。
            }
        }
        return SoftwareEntryFilter.Normalize(found);
    }

    private static SoftwareEntry? ReadOne(RegistryKey parent, string keyName, SoftwareScope scope)
    {
        try
        {
            using var key = parent.OpenSubKey(keyName);
            if (key is null) return null;

            string name = Text(key, "DisplayName");
            if (string.IsNullOrWhiteSpace(name)) return null;
            // 父项下的补丁 / 子功能没有独立卸载入口，不列入管理清单。
            if (!string.IsNullOrWhiteSpace(Text(key, "ParentKeyName"))) return null;
            string releaseType = Text(key, "ReleaseType");
            if (releaseType.Contains("Update", StringComparison.OrdinalIgnoreCase)) return null;

            // 注册表里的 InstallLocation 常带引号或多余空格，先规范化再落进快照：
            // 测量与「打开目录」都直接用它，带引号的路径会被判成「目录不存在」。
            string installLocation = SoftwareLocation.NormalizePath(Text(key, "InstallLocation"));
            string displayIcon = Text(key, "DisplayIcon").Trim();
            string uninstallString = Text(key, "UninstallString").Trim();

            return new SoftwareEntry(
                RegistryKey: scope + ":" + keyName,
                Name: name.Trim(),
                Version: Text(key, "DisplayVersion").Trim(),
                Publisher: Text(key, "Publisher").Trim(),
                InstallLocation: installLocation,
                InstallDate: SoftwareEntryFilter.ParseInstallDate(Text(key, "InstallDate")),
                UninstallString: uninstallString,
                EstimatedSizeBytes: SoftwareEntryFilter.ParseEstimatedSize(key.GetValue("EstimatedSize")),
                Scope: scope,
                IsSystemComponent: Number(key, "SystemComponent") == 1,
                DisplayIcon: displayIcon,
                NoRemove: Number(key, "NoRemove") == 1,
                // 安装目录是测量与打开位置的前提：本机 85/146 项没登记，其中大半能从图标找回来。
                DiscoveredLocation: SoftwareLocation.Resolve(
                    installLocation, displayIcon, uninstallString,
                    File.Exists, Directory.Exists, PathGuard));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or System.Security.SecurityException or ArgumentException)
        {
            return null;
        }
    }

    private static string Text(RegistryKey key, string name) => key.GetValue(name) as string ?? "";

    /// <summary>DWORD 也可能被写成字符串（部分安装器如此），两种都认。</summary>
    private static int Number(RegistryKey key, string name) => key.GetValue(name) switch
    {
        int value => value,
        long value => (int)value,
        string value when int.TryParse(value, out var parsed) => parsed,
        _ => 0
    };

    /// <summary>
    /// 递归统计安装目录内普通文件的大小，跳过重解析点以免跟随链接走出目录或死循环。
    /// 顶层子目录并行遍历（SSD 上实测接近线性加速）；拒绝访问的目录直接跳过，不中断整体统计。
    /// 返回值带状态：「目录不存在 / 无权限 / 读失败」与「真的量到 0 字节」是不同结论，界面要分开说。
    /// </summary>
    public Task<MeasureOutcome> MeasureAsync(string path, CancellationToken ct) =>
        Task.Run(() => Measure(path, ct), ct);

    /// <summary>
    /// AttributesToSkip 必须显式置 0：EnumerationOptions 的默认值会跳过隐藏与系统文件，
    /// 而程序目录里恰好有大量隐藏文件（.pdb、配置），漏掉会让体积明显偏小。
    /// </summary>
    private static readonly EnumerationOptions EnumerateAll = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false
    };

    private static MeasureOutcome Measure(string path, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path)) return MeasureOutcome.Missing;

        long total = 0;
        // 根目录本身是重解析点时按 0 字节结案：跟进去会把链接目标的体积算成这个软件的。
        try
        {
            var root = new DirectoryInfo(path);
            if (!root.Exists) return MeasureOutcome.Missing;
            if ((root.Attributes & FileAttributes.ReparsePoint) != 0)
                return new MeasureOutcome(0, MeasureStatus.Measured);
        }
        catch (UnauthorizedAccessException) { return MeasureOutcome.Denied; }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
        {
            return MeasureOutcome.Failed;
        }

        var subdirectories = new List<string>(8);
        try
        {
            foreach (var info in new DirectoryInfo(path).EnumerateFileSystemInfos("*", EnumerateAll))
            {
                if (ct.IsCancellationRequested) return new MeasureOutcome(total, MeasureStatus.Measured);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (info is FileInfo file) total += file.Length;
                else subdirectories.Add(info.FullName);
            }
        }
        catch (UnauthorizedAccessException) { return MeasureOutcome.Denied; }
        catch (Exception ex) when (ex is IOException or ArgumentException)
        {
            return MeasureOutcome.Failed;
        }

        if (subdirectories.Count == 0) return new MeasureOutcome(total, MeasureStatus.Measured);
        if (subdirectories.Count == 1 || ct.IsCancellationRequested)
        {
            foreach (string sub in subdirectories) total += Walk(sub, ct);
            return new MeasureOutcome(total, MeasureStatus.Measured);
        }

        var options = new ParallelOptions
        {
            CancellationToken = ct,
            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 4)
        };
        try
        {
            Parallel.ForEach(subdirectories, options, sub =>
            {
                long part = Walk(sub, ct);
                if (part > 0) Interlocked.Add(ref total, part);
            });
        }
        catch (OperationCanceledException) { }
        return new MeasureOutcome(total, MeasureStatus.Measured);
    }

    /// <summary>单棵子树的迭代遍历（不跟随重解析点，被拒绝的目录跳过）。</summary>
    private static long Walk(string root, CancellationToken ct)
    {
        long total = 0;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            if (ct.IsCancellationRequested) break;
            string current = pending.Pop();
            try
            {
                var directory = new DirectoryInfo(current);
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                foreach (var info in directory.EnumerateFileSystemInfos("*", EnumerateAll))
                {
                    if (ct.IsCancellationRequested) break;
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if (info is FileInfo file) total += file.Length;
                    else pending.Push(info.FullName);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        return total;
    }

    public SoftwareActionResult OpenLocation(string path)
    {
        string target = SoftwareLocation.NormalizePath(path);
        if (target.Length == 0) return new(false, "该软件未登记安装目录，无法打开。");

        // 登记的目录可能已经被卸载/移动，而厂商写的路径还留着：
        // 直接报「目录不存在」等于什么都不给，改成退到最近一个真实存在的上级目录。
        string open = target;
        bool exact = Directory.Exists(open);
        if (!exact)
        {
            if (File.Exists(open))
            {
                try { open = Path.GetDirectoryName(open) ?? open; }
                catch (ArgumentException) { open = target; }
                exact = Directory.Exists(open);
            }
            if (!exact) open = NearestExistingDirectory(open);
        }
        if (open.Length == 0 || !Directory.Exists(open))
            return new(false, $"安装目录不存在：{target}（可能是移动到其它位置或已被卸载，可用「扫描残留」核对）");

        try
        {
            Process.Start(new ProcessStartInfo { FileName = open, UseShellExecute = true });
            return new(true, exact
                ? "已在资源管理器中打开安装目录。"
                : $"登记的目录已不存在，已打开最接近的上级目录：{open}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new(false, "打开安装目录失败：" + ex.Message);
        }
    }

    /// <summary>沿路径往上找第一个真实存在的目录；一直找不到返回空串（盘符都没了就放弃）。</summary>
    private static string NearestExistingDirectory(string path)
    {
        string current = path;
        for (int depth = 0; depth < 32 && current.Length > 0; depth++)
        {
            try
            {
                if (Directory.Exists(current)) return current;
                string? parent = Path.GetDirectoryName(current);
                if (string.IsNullOrEmpty(parent) || parent == current) return "";
                current = parent;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return "";
            }
        }
        return "";
    }

    /// <summary>启动软件自带的卸载向导；调用前 ViewModel 已做二次确认。</summary>
    public SoftwareActionResult LaunchUninstall(SoftwareEntry entry)
    {
        var command = UninstallCommand.Parse(entry.UninstallString);
        if (command is null)
            return new(false, "无法解析该软件登记的卸载命令，请在系统「应用和功能」中卸载。");
        if (command.IsSilent)
            return new(false, "该软件登记的卸载命令包含静默参数。为避免无提示卸载，Sweep 拒绝执行，请在系统「应用和功能」中手动卸载。");
        if (!command.IsSystemCommand && !File.Exists(command.FileName))
            return new(false, "卸载程序不存在（软件可能已被移除），请在系统「应用和功能」中卸载。");

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = command.FileName,
                Arguments = command.Arguments,
                UseShellExecute = true
            });
            return new(true, $"已启动「{entry.Name}」自带的卸载程序。后续步骤由该软件完成，Sweep 不会代替您确认。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new(false, "卸载程序启动失败：" + ex.Message);
        }
    }

    public SoftwareActionResult OpenAppsAndFeatures()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:appsfeatures") { UseShellExecute = true });
            return new(true, "已打开系统「应用和功能」设置。");
        }
        catch
        {
            try
            {
                Process.Start(new ProcessStartInfo("control.exe", "appwiz.cpl") { UseShellExecute = true });
                return new(true, "已打开控制面板「程序和功能」。");
            }
            catch { return new(false, "无法打开系统卸载设置，请手动运行 appwiz.cpl。"); }
        }
    }
}

/// <summary>
/// 无 WPF / 无注册表访问的空实现，只供未显式注入软件服务的构造路径使用。
/// 生产组合根总是注入 SoftwareService。
/// </summary>
public sealed class NullSoftwareService : ISoftwareService
{
    public static NullSoftwareService Instance { get; } = new();

    public IReadOnlyList<SoftwareEntry> ListInstalled() => Array.Empty<SoftwareEntry>();
    public Task<MeasureOutcome> MeasureAsync(string path, CancellationToken ct) =>
        Task.FromResult(MeasureOutcome.Missing);
    public SoftwareActionResult OpenLocation(string path) => new(false, "软件管理服务不可用。");
    public SoftwareActionResult LaunchUninstall(SoftwareEntry entry) => new(false, "软件管理服务不可用。");
    public SoftwareActionResult OpenAppsAndFeatures() => new(false, "软件管理服务不可用。");
}
