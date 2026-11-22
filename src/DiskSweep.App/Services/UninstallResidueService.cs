using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using DiskSweep.Core.Software;

namespace DiskSweep.App.Services;

/// <summary>
/// 卸载残留清理：扫描“卸载程序与安装目录都已不存在”的失效登记，删除前导出 .reg 备份，
/// 并在删除瞬间重新判定仍为失效。绝不删除安装目录、不执行卸载命令、不碰正常软件项。
/// 与 SoftwareService（只读清单 + 启动官方卸载向导）职责分离。
/// </summary>
public sealed class UninstallResidueService : IUninstallResidueService
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private static readonly char[] KeySeparator = { ':' };
    public bool IsAvailable => true;

    public IReadOnlyList<ResidueEntry> Scan()
    {
        var result = new List<ResidueEntry>();
        foreach (var entry in ReadRawEntries())
        {
            var verdict = UninstallResidue.Evaluate(entry, File.Exists, Directory.Exists);
            if (!verdict.IsOrphan) continue;
            result.Add(new ResidueEntry(
                entry.RegistryKey, entry.Name, entry.Publisher,
                entry.UninstallString, entry.InstallLocation, verdict.Reason));
        }
        return result;
    }

    public SoftwareActionResult RemoveOrphan(ResidueEntry entry, string backupDirectory)
    {
        if (!TryResolve(entry.RegistryKey, out var hive, out var view, out var subKey))
            return new(false, "残留项的注册表位置无效，未执行删除。");

        try
        {
            // 删除前重新读取真实键并复核判定（TOCTOU）：期间若被修复/重建则放弃删除。
            var fresh = ReadOne(hive, view, subKey);
            if (fresh is null)
                return new(true, "该项已不存在，无需删除。");
            var verdict = UninstallResidue.Evaluate(fresh, File.Exists, Directory.Exists);
            if (!verdict.IsOrphan)
                return new(false, "复核发现该项已不再是失效残留（卸载程序或安装目录重新出现），为安全起见未删除。");

            // 导出 .reg 备份后再删除；备份失败即中止，绝不“先删后补备份”。
            Directory.CreateDirectory(backupDirectory);
            var backupFile = Path.Combine(backupDirectory,
                "residue-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + SafeName(subKey) + ".reg");
            string fullPath = RegPath(hive) + "\\" + UninstallPath + "\\" + subKey;
            if (!ExportKey(fullPath, backupFile, view))
                return new(false, "导出 .reg 备份失败，未删除任何注册表项。备份目录：" + backupDirectory);

            using (var root = RegistryKey.OpenBaseKey(hive, view))
            using (var uninstall = root.OpenSubKey(UninstallPath, writable: true))
            {
                if (uninstall is null)
                    return new(false, "无法以写入方式打开卸载登记项，未执行删除。");
                uninstall.DeleteSubKeyTree(subKey, throwOnMissingSubKey: false);
            }

            return new(true, $"已删除失效残留「{entry.Name}」，并备份到：{backupFile}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or System.Security.SecurityException or ArgumentException
                                       or InvalidOperationException)
        {
            return new(false, "删除失效残留失败：" + ex.Message);
        }
    }

    private static IReadOnlyList<SoftwareEntry> ReadRawEntries()
    {
        var all = new List<SoftwareEntry>();
        var scopes = new (RegistryHive Hive, RegistryView View, SoftwareScope Scope)[]
        {
            (RegistryHive.LocalMachine, RegistryView.Registry64, SoftwareScope.Machine64),
            (RegistryHive.LocalMachine, RegistryView.Registry32, SoftwareScope.Machine32),
            (RegistryHive.CurrentUser, RegistryView.Default, SoftwareScope.CurrentUser)
        };
        foreach (var (hive, view, scope) in scopes)
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = root.OpenSubKey(UninstallPath);
                if (uninstall is null) continue;
                foreach (string keyName in uninstall.GetSubKeyNames())
                {
                    var entry = ReadOne(hive, view, keyName, scope);
                    if (entry is not null) all.Add(entry);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                           or System.Security.SecurityException or ArgumentException
                                           or PlatformNotSupportedException)
            {
                // 单个视图不可读不影响其它视图
            }
        }
        return all;
    }

    private static SoftwareEntry? ReadOne(RegistryHive hive, RegistryView view, string keyName,
        SoftwareScope? scope = null)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = root.OpenSubKey(UninstallPath);
            using var key = uninstall?.OpenSubKey(keyName);
            if (key is null) return null;
            string name = Text(key, "DisplayName");
            if (string.IsNullOrWhiteSpace(name)) return null;
            string cmd = Text(key, "UninstallString").Trim();
            if (string.IsNullOrWhiteSpace(cmd)) return null;
            string location = Text(key, "InstallLocation");
            if (!string.IsNullOrWhiteSpace(location))
            {
                try { location = Environment.ExpandEnvironmentVariables(location.Trim().Trim('"')); }
                catch (ArgumentException) { }
            }
            var resolvedScope = scope ?? (hive == RegistryHive.CurrentUser
                ? SoftwareScope.CurrentUser
                : view == RegistryView.Registry32 ? SoftwareScope.Machine32 : SoftwareScope.Machine64);
            return new SoftwareEntry(
                resolvedScope + ":" + keyName, name.Trim(), Text(key, "DisplayVersion").Trim(),
                Text(key, "Publisher").Trim(), location, "", cmd, 0, resolvedScope,
                key.GetValue("SystemComponent") is int sc && sc == 1);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or System.Security.SecurityException or ArgumentException)
        {
            return null;
        }
    }

    private static string Text(RegistryKey key, string name) => key.GetValue(name) as string ?? "";

    private static bool ExportKey(string fullPath, string backupFile, RegistryView view)
    {
        // /y 覆盖；/reg:64 或 /reg:32 指定视图；参数逐项传入，避免字符串拼接注入。
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

    private static bool TryResolve(string registryKey, out RegistryHive hive, out RegistryView view,
        out string subKey)
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

/// <summary>无注册表访问的空实现，保证旧构造路径仍可用。</summary>
public sealed class NullUninstallResidueService : IUninstallResidueService
{
    public static NullUninstallResidueService Instance { get; } = new();
    public bool IsAvailable => false;
    public IReadOnlyList<ResidueEntry> Scan() => Array.Empty<ResidueEntry>();
    public SoftwareActionResult RemoveOrphan(ResidueEntry entry, string backupDirectory) =>
        new(false, "卸载残留清理服务不可用。");
}
