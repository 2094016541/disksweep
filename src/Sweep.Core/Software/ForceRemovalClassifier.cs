using System.IO;

namespace Sweep.Core.Software;

/// <summary>强制删除候选的类型。</summary>
public enum ForceRemovalKind
{
    Registry,        // 卸载登记键（该软件自己的）
    Directory,       // 安装目录
    AppData,         // AppData / ProgramData 下按软件名关联的目录
    File             // 软件名关联的单文件
}

/// <summary>
/// 强制删除扫描候选：服务层发现、Core 判定、UI 逐项勾选。
/// CanDelete=false 的项只展示供用户核对，永远不会进入删除清单。
/// </summary>
public sealed record ForceRemovalCandidate(
    string Path,
    string DisplayName,
    ForceRemovalKind Kind,
    bool CanDelete,
    string Reason);

/// <summary>一批强制删除的结果汇总。</summary>
public sealed record ForceRemovalResult(
    bool Ok, string Message, int Deleted, int Failed, IReadOnlyList<string> Messages);

/// <summary>
/// 强制删除候选的判定结论。CanDelete=false 的项只展示、永不进入删除清单。
/// </summary>
public sealed record ForceRemovalVerdict(
    bool CanDelete, string Reason, ForceRemovalKind Kind, string NormalizedPath);

/// <summary>
/// 强制删除（Geek 式"扫描全关联"）的安全判定——纯函数，不碰注册表/文件系统/进程。
/// 默认拒绝：只有明确落在安全目录白名单、且不与系统级软件/共享运行时冲突的候选才允许删除。
/// 所有删除仍须走服务层：文件进回收站、注册表先导出 .reg 备份，且用户逐项勾选 + 二次确认。
/// </summary>
public static class ForceRemovalClassifier
{
    /// <summary>
    /// 系统级 / 共享运行时名称：这些软件的条目一律不允许强制删除（Geek 官方同样警告）。
    /// 命中即整项拒绝，防止误删系统组件、驱动、共享 C++/NET 运行时。
    /// </summary>
    private static readonly string[] SystemLevelNames =
    {
        "Microsoft", "Windows", "Intel", "NVIDIA", "AMD", "Realtek", "Qualcomm", "ARM",
        "Visual C++", ".NET", "Microsoft Visual", "DirectX", "SQL Server", "Office",
        "Java", "Swift", "VC++", "Windows Installer", "Radeon", "GeForce"
    };

    /// <summary>共享运行时目录名：即使名称关联也不能删（其它软件可能依赖）。</summary>
    private static readonly string[] SharedRuntimeFolders =
    {
        "Microsoft", "Windows", "Intel", "NVIDIA", "AMD", "Realtek", "Common Files",
        "Microsoft.NET", "Visual Studio", "Java", "InstallShield", "WiX Toolset"
    };

    /// <summary>
    /// 判定软件名是否属于系统级或共享组件——命中则整个强制删除流程禁用。
    /// 用整词/子串结合：像 "Microsoft Edge" 这类必须挡住，但 "Microwave Soft" 不能误伤。
    /// </summary>
    public static bool IsSystemLevel(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return true;   // 无名条目一律拒绝
        foreach (var banned in SystemLevelNames)
            if (ContainsWholeToken(name, banned)) return true;
        return false;
    }

    /// <summary>候选路径是否落在允许强制删除的安全根目录内（大小写不敏感）。</summary>
    public static bool IsInSafeRoot(string path, string safeRoot)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(safeRoot)) return false;
        var p = Path.GetFullPath(path).TrimEnd('\\') + "\\";
        var root = Path.GetFullPath(safeRoot).TrimEnd('\\') + "\\";
        return p.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 核心判定：候选路径与该软件是否明确关联、且删除是否安全。
    /// safeRoots 由服务层传入（Program Files / ProgramData / AppData / LocalAppData / Temp）。
    /// </summary>
    public static ForceRemovalVerdict Evaluate(
        string candidatePath,
        ForceRemovalKind kind,
        string softwareName,
        string? publisher,
        string? installLocation,
        IReadOnlyList<string> safeRoots)
    {
        var path = candidatePath.Trim().TrimEnd('\\', '/');
        if (path.Length == 0)
            return new(false, "路径为空，保留", kind, "");

        // 1) 系统级软件：任何候选都不许删
        if (IsSystemLevel(softwareName))
            return new(false, "系统级或共享组件，禁止强制删除", kind, path);

        // 2) 注册表卸载登记键：只碰该软件自己的那一个键，服务层强制先备份，不做磁盘安全根判定
        if (kind == ForceRemovalKind.Registry)
            return new(true, "该软件自己的卸载登记，可删（删除前备份）", kind, path);

        // 3) 磁盘候选必须落在某个安全根内（挡住系统盘根、Windows 目录、其它盘）
        bool inSafeRoot = safeRoots.Any(r => IsInSafeRoot(path, r));
        if (!inSafeRoot)
            return new(false, "不在允许清理的安全目录内，保留", kind, path);

        // 4) 路径分段命中共享运行时目录名 → 保留
        var segments = path.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        foreach (var seg in segments)
            foreach (var banned in SharedRuntimeFolders)
                if (string.Equals(seg, banned, StringComparison.OrdinalIgnoreCase))
                    return new(false, "共享运行时目录，保留", kind, path);

        // 5) 关联强度：安装目录本身 / 名称匹配 / 发布者匹配，三者至少一项明确成立
        bool isInstallLocation = !string.IsNullOrWhiteSpace(installLocation) &&
            string.Equals(Trim(path), Trim(installLocation), StringComparison.OrdinalIgnoreCase);
        string leaf = Path.GetFileName(path);
        bool nameMatch = NameRelated(leaf, softwareName);
        bool publisherMatch = !string.IsNullOrWhiteSpace(publisher) && NameRelated(leaf, publisher!);

        if (isInstallLocation)
            return new(true, "软件登记的安装目录，可删（进回收站）", kind, path);
        if (nameMatch)
            return new(true, "目录名与软件名关联，可删（进回收站）", kind, path);
        if (publisherMatch)
            return new(true, "目录名与发布者关联，可删（进回收站）", kind, path);

        return new(false, "未找到明确关联证据，保留", kind, path);
    }

    /// <summary>
    /// 删除瞬间的复核：路径仍在安全根内、且不含共享运行时目录段。
    /// 用于 TOCTOU 兜底——扫描判定后路径可能被移动/替换，删除前再确认一次位置安全。
    /// 名称关联在扫描时已判定，此处只复核位置安全性。
    /// </summary>
    public static bool IsPathStillSafe(string path, IReadOnlyList<string> safeRoots)
    {
        var trimmed = path.Trim().TrimEnd('\\', '/');
        if (trimmed.Length == 0) return false;
        if (!safeRoots.Any(r => IsInSafeRoot(trimmed, r))) return false;
        var segments = trimmed.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        foreach (var seg in segments)
            foreach (var banned in SharedRuntimeFolders)
                if (string.Equals(seg, banned, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private static string Trim(string s) => s.Trim().TrimEnd('\\', '/');

    /// <summary>
    /// 名称关联：去空格/点/连字符后双向包含（"FooBar" 与 "Foo Bar Ltd" 都应命中），
    /// 但要求较短一方 ≥3 字符且长度差不过大，避免 "soft" 误配 "Microsoft"。
    /// </summary>
    private static bool NameRelated(string candidate, string software)
    {
        string a = Normalize(candidate), b = Normalize(software);
        if (a.Length < 3 || b.Length < 3) return false;
        string shorter = a.Length <= b.Length ? a : b;
        string longer = a.Length <= b.Length ? b : a;
        // 软件名常带 "Setup"/"2022" 等后缀，允许较长方最多多 12 个字符
        return longer.Contains(shorter, StringComparison.Ordinal) && longer.Length - shorter.Length <= 12;
    }

    private static string Normalize(string s)
    {
        var chars = s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray();
        return new string(chars);
    }

    /// <summary>整词匹配系统级名称：Microsoft Edge 命中 Microsoft，Microwave 不误伤。</summary>
    private static bool ContainsWholeToken(string name, string token)
    {
        int i = name.IndexOf(token, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return false;
        int end = i + token.Length;
        bool leftOk = i == 0 || !char.IsLetterOrDigit(name[i - 1]);
        bool rightOk = end == name.Length || !char.IsLetterOrDigit(name[end]);
        // "C++" / ".NET" / "VC++" 这类带符号 token 用整词边界即可
        return leftOk && rightOk;
    }
}
