using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using DiskSweep.Core;
using DiskSweep.Core.Executors;
using DiskSweep.Core.Software;

namespace DiskSweep.App.Services;

/// <summary>
/// 强制删除（Geek 式"扫描全关联"）的服务实现。默认不用——只在软件自带卸载程序失效时兜底。
/// 扫描只读；删除时磁盘项走回收站（可从系统回收站还原）、注册表键先导出 .reg 备份。
/// 安全判定全部委托给 Core 的 ForceRemovalClassifier（纯函数），本类只做系统 API 调用。
/// </summary>
public sealed class SoftwareForceRemovalService : ISoftwareForceRemovalService
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private static readonly char[] KeySeparator = { ':' };
    private readonly Func<IReadOnlyList<string>, bool, (int Hr, bool Aborted)> _deleteBatch;
    private readonly Func<string[]> _safeRootsProvider;

    public bool IsAvailable => true;

    /// <param name="deleteBatch">可注入的批量删除（测试用），生产路径走 Win32Shell 回收站删除。</param>
    /// <param name="safeRootsProvider">可注入的安全根（测试用临时目录），生产路径用系统标准目录。</param>
    public SoftwareForceRemovalService(
        Func<IReadOnlyList<string>, bool, (int Hr, bool Aborted)>? deleteBatch = null,
        Func<string[]>? safeRootsProvider = null)
    {
        _deleteBatch = deleteBatch ?? ((paths, allowUndo) => Win32Shell.DeleteBatch(paths, allowUndo));
        _safeRootsProvider = safeRootsProvider ?? SafeRoots;
    }

    /// <summary>允许扫描的磁盘根目录（不含系统目录；强制删除判定也用同一组）。</summary>
    internal static string[] SafeRoots()
    {
        string user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var roots = new List<string>();
        if (!string.IsNullOrWhiteSpace(programFiles)) roots.Add(programFiles);
        if (!string.IsNullOrWhiteSpace(programFilesX86)) roots.Add(programFilesX86);
        if (!string.IsNullOrWhiteSpace(programData)) roots.Add(programData);
        if (!string.IsNullOrWhiteSpace(local))
        {
            roots.Add(Path.Combine(local, "Programs"));
            roots.Add(Path.Combine(local, "Temp"));
        }
        if (!string.IsNullOrWhiteSpace(roaming)) roots.Add(roaming);
        if (!string.IsNullOrWhiteSpace(user)) roots.Add(Path.Combine(user, "AppData", "Local"));
        return roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public IReadOnlyList<ForceRemovalCandidate> Scan(SoftwareEntry entry)
    {
        var result = new List<ForceRemovalCandidate>();
        var roots = _safeRootsProvider();

        // 1) 该软件自己的卸载登记键（精确 RegistryKey，绝不枚举其它软件的键）
        if (!string.IsNullOrWhiteSpace(entry.RegistryKey) && TryResolve(entry.RegistryKey, out _, out _, out _))
        {
            var verdict = ForceRemovalClassifier.Evaluate(
                entry.RegistryKey, ForceRemovalKind.Registry, entry.Name, entry.Publisher,
                entry.InstallLocation, roots);
            result.Add(new ForceRemovalCandidate(
                entry.RegistryKey, "卸载登记 · " + entry.Name, ForceRemovalKind.Registry,
                verdict.CanDelete, verdict.Reason));
        }

        // 2) 安装目录 + 安全根下按软件名关联的目录（各根只扫第一层，避免全盘递归）
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(entry.InstallLocation))
            AddDirectory(entry.InstallLocation, ForceRemovalKind.Directory, entry, roots, result, seen);

        foreach (var root in roots)
        {
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(root))
                    AddDirectory(dir, ForceRemovalKind.AppData, entry, roots, result, seen);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // 单个根不可读不影响其它根（只读扫描，跳过即可）
            }
        }

        return result;
    }

    private static void AddDirectory(string path, ForceRemovalKind kind, SoftwareEntry entry,
        string[] roots, List<ForceRemovalCandidate> result, HashSet<string> seen)
    {
        var full = path.TrimEnd('\\', '/');
        if (full.Length == 0 || !seen.Add(full)) return;
        if (!Directory.Exists(full)) return;

        // 先用分类器判定；CanDelete=false 也加入清单（展示供用户核对，但不可勾选删除）
        var verdict = ForceRemovalClassifier.Evaluate(
            full, kind, entry.Name, entry.Publisher, entry.InstallLocation, roots);
        result.Add(new ForceRemovalCandidate(
            full, Path.GetFileName(full), kind, verdict.CanDelete, verdict.Reason));
    }

    public ForceRemovalResult Remove(IReadOnlyList<ForceRemovalCandidate> candidates, string backupDirectory)
    {
        var accepted = candidates.Where(c => c.CanDelete).ToList();
        if (accepted.Count == 0)
            return new ForceRemovalResult(false, "没有可删除的候选项；系统级、共享运行时与无关联证据的项已被安全判定排除。", 0, 0, Array.Empty<string>());

        var messages = new List<string>();
        int deleted = 0, failed = 0;

        // 1) 磁盘项：一次批量进回收站（可从系统回收站还原）。
        var diskPaths = accepted.Where(c => c.Kind != ForceRemovalKind.Registry).Select(c => c.Path).ToList();
        if (diskPaths.Count > 0)
        {
            // 删除前复核：路径必须仍存在且仍通过安全判定，防止扫描后被替换/移动（TOCTOU 兜底）。
            var roots = _safeRootsProvider();
            var safe = new List<string>();
            foreach (var c in accepted.Where(c => c.Kind != ForceRemovalKind.Registry))
            {
                bool stillExists = c.Kind == ForceRemovalKind.File ? File.Exists(c.Path) : Directory.Exists(c.Path);
                bool stillSafe = ForceRemovalClassifier.IsPathStillSafe(c.Path, roots);
                if (stillExists && stillSafe) safe.Add(c.Path);
                else messages.Add("· " + c.DisplayName + "：删除前复核未通过（已被移动或不再满足安全条件），保留");
            }
            if (safe.Count > 0)
            {
                var (hr, aborted) = _deleteBatch(safe, true);   // true = 进回收站，可从系统回收站还原
                foreach (var path in safe)
                {
                    bool gone = !File.Exists(path) && !Directory.Exists(path);
                    if (gone) { deleted++; messages.Add("· " + path + "：已移入回收站，可从系统回收站还原"); }
                    else { failed++; messages.Add("· " + path + "：删除未完成（可能被占用）；HRESULT 0x" + hr.ToString("X8")); }
                }
                if (aborted) messages.Add("删除操作被中止，未处理项保留。");
            }
        }

        // 2) 注册表项：逐个导出 .reg 备份后删除该软件自己的卸载键。
        foreach (var c in accepted.Where(c => c.Kind == ForceRemovalKind.Registry))
        {
            var outcome = RemoveRegistryKey(c, backupDirectory, messages);
            if (outcome) deleted++; else failed++;
        }

        string message = $"强制删除完成：成功 {deleted} 项 / 失败 {failed} 项。" +
                         (diskPaths.Count > 0 ? " 文件与目录已移入回收站，可从系统回收站还原。" : "") +
                         " 注册表项已备份后再删除。";
        return new ForceRemovalResult(failed == 0, message, deleted, failed, messages);
    }

    private static bool RemoveRegistryKey(ForceRemovalCandidate candidate, string backupDirectory, List<string> messages)
    {
        if (!TryResolve(candidate.Path, out var hive, out var view, out var subKey))
        {
            messages.Add("· " + candidate.DisplayName + "：注册表位置无效，未删除");
            return false;
        }
        try
        {
            using (var root = RegistryKey.OpenBaseKey(hive, view))
            using (var uninstall = root.OpenSubKey(UninstallPath))
            using (var key = uninstall?.OpenSubKey(subKey))
            {
                if (key is null)
                {
                    messages.Add("· " + candidate.DisplayName + "：该项已不存在，无需删除");
                    return true;
                }
            }

            Directory.CreateDirectory(backupDirectory);
            var backupFile = Path.Combine(backupDirectory,
                "force-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + SafeName(subKey) + ".reg");
            string fullPath = RegPath(hive) + "\\" + UninstallPath + "\\" + subKey;
            if (!ExportKey(fullPath, backupFile, view))
            {
                messages.Add("· " + candidate.DisplayName + "：导出 .reg 备份失败，未删除任何注册表项");
                return false;
            }

            using (var root = RegistryKey.OpenBaseKey(hive, view))
            using (var uninstall = root.OpenSubKey(UninstallPath, writable: true))
            {
                if (uninstall is null)
                {
                    messages.Add("· " + candidate.DisplayName + "：无法以写入方式打开，未删除");
                    return false;
                }
                uninstall.DeleteSubKeyTree(subKey, throwOnMissingSubKey: false);
            }
            messages.Add("· " + candidate.DisplayName + "：已备份并删除卸载登记 → " + backupFile);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or System.Security.SecurityException or ArgumentException
                                       or InvalidOperationException)
        {
            messages.Add("· " + candidate.DisplayName + "：删除失败：" + ex.Message);
            return false;
        }
    }

    private static bool ExportKey(string fullPath, string backupFile, RegistryView view)
    {
        string viewArg = view == RegistryView.Registry32 ? "/reg:32" : "/reg:64";
        try
        {
            var psi = new ProcessStartInfo("reg.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("export");
            psi.ArgumentList.Add(fullPath);
            psi.ArgumentList.Add(backupFile);
            psi.ArgumentList.Add("/y");
            psi.ArgumentList.Add(viewArg);
            using var process = Process.Start(psi);
            if (process is null) return false;
            process.WaitForExit(15000);
            return process.HasExited && process.ExitCode == 0 && File.Exists(backupFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static bool TryResolve(string registryKey, out RegistryHive hive, out RegistryView view, out string subKey)
    {
        hive = default; view = default; subKey = "";
        if (string.IsNullOrWhiteSpace(registryKey)) return false;
        var parts = registryKey.Split(KeySeparator, 2);
        if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[1])) return false;
        subKey = parts[1];
        switch (parts[0])
        {
            case "Machine64": hive = RegistryHive.LocalMachine; view = RegistryView.Registry64; return true;
            case "Machine32": hive = RegistryHive.LocalMachine; view = RegistryView.Registry32; return true;
            case "CurrentUser": hive = RegistryHive.CurrentUser; view = RegistryView.Default; return true;
            default: return false;
        }
    }

    private static string RegPath(RegistryHive hive) => hive switch
    {
        RegistryHive.LocalMachine => "HKLM",
        RegistryHive.CurrentUser => "HKCU",
        _ => "HKLM"
    };

    private static string SafeName(string subKey)
    {
        var chars = subKey.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').Take(32).ToArray();
        return chars.Length == 0 ? "item" : new string(chars);
    }
}

/// <summary>无系统访问能力的空实现，保证旧构造路径可用。</summary>
public sealed class NullSoftwareForceRemovalService : ISoftwareForceRemovalService
{
    public static NullSoftwareForceRemovalService Instance { get; } = new();
    public bool IsAvailable => false;
    public IReadOnlyList<ForceRemovalCandidate> Scan(SoftwareEntry entry) => Array.Empty<ForceRemovalCandidate>();
    public ForceRemovalResult Remove(IReadOnlyList<ForceRemovalCandidate> candidates, string backupDirectory) =>
        new(false, "强制删除服务不可用。", 0, 0, Array.Empty<string>());
}
