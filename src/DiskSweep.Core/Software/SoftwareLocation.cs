namespace DiskSweep.Core.Software;

/// <summary>
/// 推断安装目录时的保护区：<see cref="Trees"/> 连同子目录一起排除（系统目录、共享数据、临时目录），
/// <see cref="Exact"/> 只排除自身（Program Files、LocalAppData 这类"容器"——真实软件就装在它们的子目录里）。
/// 可注入是为了让判定在测试里不依赖运行测试的机器。
/// </summary>
public sealed record SoftwarePathGuard(IReadOnlyList<string> Trees, IReadOnlyList<string> Exact)
{
    public static SoftwarePathGuard Default { get; } = Build();

    private static SoftwarePathGuard Build()
    {
        var trees = new List<string>(4);
        var exact = new List<string>(6);

        void Add(List<string> target, string path)
        {
            if (!string.IsNullOrWhiteSpace(path)) target.Add(path);
        }

        Add(trees, Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        Add(trees, Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
        Add(trees, Path.GetTempPath());
        Add(exact, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        Add(exact, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        Add(exact, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        Add(exact, Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        Add(exact, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        return new SoftwarePathGuard(trees, exact);
    }
}

/// <summary>
/// 从卸载登记里「找回」安装目录：InstallLocation 经常是空值，而 DisplayIcon 或卸载程序
/// 基本都住在安装目录里。这里只做候选推断，不猜测厂商语义，也不验证目录归属。
///
/// 所有文件系统判断都走注入的委托，判定本身是纯函数，可在无磁盘的测试里验证。
/// 结果只用于「测量占用 / 打开目录」，不参与卸载残留判定——那边的证据链必须用注册值。
/// </summary>
public static class SoftwareLocation
{
    /// <summary>
    /// 依次尝试：注册的安装目录 → DisplayIcon 所在目录 → 卸载程序所在目录。
    /// 返回空串表示没有可信候选。
    /// </summary>
    public static string Resolve(
        string installLocation,
        string displayIcon,
        string uninstallString,
        Func<string, bool> fileExists,
        Func<string, bool> directoryExists,
        SoftwarePathGuard? guard = null)
    {
        guard ??= SoftwarePathGuard.Default;

        string registered = CleanPath(installLocation);
        if (registered.Length > 0)
        {
            if (directoryExists(registered)) return registered;
            // 少数厂商把 exe 路径写进 InstallLocation。
            if (fileExists(registered))
            {
                string? parent = ParentOf(registered);
                if (parent is not null && directoryExists(parent) && IsPlausible(parent, guard)) return parent;
            }
        }

        // DisplayIcon 可能是 "C:\App\app.exe,0" 或 "C:\App\icon.ico"。
        string icon = StripIconIndex(CleanPath(displayIcon));
        if (icon.Length > 0 && fileExists(icon))
        {
            string? dir = ParentOf(icon);
            if (dir is not null && directoryExists(dir) && IsPlausible(dir, guard)) return dir;
        }

        // 引导器（SQL Server 的 Setup Bootstrap、部分厂商的 Setup 目录）不是安装目录：
        // 它的上级版本目录（...\Microsoft SQL Server\170）才是这个版本真正落盘的地方。
        foreach (string candidate in new[] { icon, CleanPath(displayIcon), CleanPath(uninstallString) })
        {
            string? root = BootstrapperRoot(candidate);
            if (root is not null && directoryExists(root) && IsPlausible(root, guard)) return root;
        }

        var command = UninstallCommand.Parse(uninstallString);
        if (command is not null && !command.IsSystemCommand && fileExists(command.FileName))
        {
            string? dir = ParentOf(command.FileName);
            if (dir is not null && directoryExists(dir) && IsPlausible(dir, guard)) return dir;
        }

        return "";
    }

    /// <summary>
    /// 候选目录是否像某个软件自己的安装目录：排除盘符根、系统目录、共享目录与临时目录，
    /// 避免把 C:\Windows\System32 或 Common Files 当成某个软件的体积来源。
    /// </summary>
    public static bool IsPlausible(string directory, SoftwarePathGuard guard)
    {
        string dir = CleanPath(directory);
        if (dir.Length == 0) return false;
        try { dir = Path.GetFullPath(dir).TrimEnd('\\', '/'); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
        if (dir.Length == 0) return false;

        string? root = Path.GetPathRoot(dir)?.TrimEnd('\\', '/');
        if (!string.IsNullOrEmpty(root) && string.Equals(dir, root, StringComparison.OrdinalIgnoreCase))
            return false;

        foreach (string blocked in guard.Trees)
        {
            string p = CleanPath(blocked).TrimEnd('\\', '/');
            if (p.Length == 0) continue;
            if (dir.Equals(p, StringComparison.OrdinalIgnoreCase)) return false;
            if (dir.StartsWith(p + "\\", StringComparison.OrdinalIgnoreCase)) return false;
        }

        foreach (string blocked in guard.Exact)
        {
            string p = CleanPath(blocked).TrimEnd('\\', '/');
            if (p.Length > 0 && dir.Equals(p, StringComparison.OrdinalIgnoreCase)) return false;
        }

        return !dir.Contains("\\Common Files", StringComparison.OrdinalIgnoreCase)
            && !dir.Contains("\\WindowsApps", StringComparison.OrdinalIgnoreCase)
            && !dir.Contains("\\$Recycle.Bin", StringComparison.OrdinalIgnoreCase)
            // 引导/缓存目录不是安装目录：SQL Server 的 DisplayIcon 就落在 Setup Bootstrap 里，
            // 量到的是引导器体积而不是数据库的体积，比"未登记"更容易误导。
            && !dir.Contains("\\Setup Bootstrap", StringComparison.OrdinalIgnoreCase)
            && !dir.Contains("\\InstallerCache", StringComparison.OrdinalIgnoreCase)
            && !dir.Contains("\\Downloaded Installations", StringComparison.OrdinalIgnoreCase);
    }

    private const string BootstrapperSegment = "\\Setup Bootstrap\\";

    /// <summary>
    /// 从引导器路径回溯到版本根目录："...\Microsoft SQL Server\170\Setup Bootstrap\SQL2025\x64\x.exe"
    /// → "...\Microsoft SQL Server\170"。没有引导器段时返回 null。
    /// </summary>
    public static string? BootstrapperRoot(string? path)
    {
        string text = CleanPath(path);
        int index = text.IndexOf(BootstrapperSegment, StringComparison.OrdinalIgnoreCase);
        if (index <= 0) return null;
        string root = text[..index].TrimEnd('\\', '/');
        // 只接受有实际目录层级的路径，避免把 "C:\Setup Bootstrap" 的上级盘符根当成候选。
        return root.Length > 0 && Path.GetDirectoryName(root) is not null ? root : null;
    }

    private static string? ParentOf(string path)    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            return string.IsNullOrWhiteSpace(dir) ? null : dir.TrimEnd('\\', '/');
        }
        catch (ArgumentException) { return null; }
    }

    /// <summary>
    /// 把注册表里的路径收敛成可用的路径：去掉首尾空白与引号、展开 %变量%。
    /// 厂商写 InstallLocation 时经常带引号（"C:\Program Files\App\"）或留一层多余空格，
    /// 不清理的话 Directory.Exists 直接返回 false——表现为「目录不存在」和「打不开安装目录」。
    /// </summary>
    public static string NormalizePath(string? value)
    {
        string text = (value ?? "").Trim().Trim('"').Trim();
        if (text.Length == 0) return "";
        try { return Environment.ExpandEnvironmentVariables(text).Trim(); }
        catch (ArgumentException) { return text; }
    }

    private static string CleanPath(string? value) => NormalizePath(value);

    /// <summary>DisplayIcon 常见写法是 "C:\App\app.exe,0"；末尾 ,数字 是图标索引，不是路径的一部分。</summary>
    public static string StripIconIndex(string value)
    {
        if (value.Length == 0) return value;
        int comma = value.LastIndexOf(',');
        if (comma <= 0 || comma == value.Length - 1) return value;

        int i = comma + 1;
        while (i < value.Length && char.IsWhiteSpace(value[i])) i++;
        if (i == value.Length) return value;          // 逗号后没有内容，原样保留
        for (int j = i; j < value.Length; j++)
            if (!char.IsDigit(value[j])) return value;
        return value[..comma].TrimEnd();
    }
}
