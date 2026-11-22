namespace DiskSweep.Core.Safety;

/// <summary>受保护路径与跨用户边界（文档 4.4 / 8.1 红线 8 / 10.4 黄金样本）。</summary>
public static class ProtectedPaths
{
    private static readonly string[] AlwaysProtectedRoots =
    {
        @"$Recycle.Bin", "System Volume Information", "$WINDOWS.~BT", "Windows.old",
        "WinSxS", "$WINDOWS.~WS", "Boot", "Recovery", "Config.Msi", "Documents and Settings"
    };

    private static readonly string[] AlwaysProtectedFiles =
    {
        "pagefile.sys", "hiberfil.sys", "swapfile.sys", "bootmgr", "ntldr", "BOOTSECT.BAK"
    };

    public static bool Contains(string normalizedPath, SafetyContext ctx)
    {
        var p = normalizedPath.TrimEnd('\\');
        if (p.Length == 0) return true;

        // 卷根本身
        if (p.Length == 2 && p[1] == ':') return true;
        if (p.Length == 3 && p[1] == ':' && p[2] == '\\') return true;

        var leaf = Path.GetFileName(p);
        if (Array.Exists(AlwaysProtectedFiles, f => string.Equals(f, leaf, StringComparison.OrdinalIgnoreCase)))
            return true;

        // Windows 目录：仅放行显式登记的子目录
        if (IsUnder(p, ctx.WindowsDirectory))
        {
            bool exempt = ctx.ProtectedExemptions.Any(e =>
                IsUnder(p, e) || string.Equals(p, e.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
            if (!exempt) return true;
        }

        if (IsUnder(p, Path.Combine(ctx.SystemDrive, "Program Files")) ||
            IsUnder(p, Path.Combine(ctx.SystemDrive, "Program Files (x86)")) ||
            IsUnder(p, Path.Combine(ctx.SystemDrive, "ProgramData", "Microsoft", "Windows", "Start Menu")))
            return true;

        // 任意卷上的系统级目录名
        foreach (var segment in p.Split('\\'))
        {
            if (Array.Exists(AlwaysProtectedRoots, r => string.Equals(r, segment, StringComparison.OrdinalIgnoreCase)))
                return true;
        }

        // 当前用户的桌面 / 文档
        if (IsUnder(p, Path.Combine(ctx.CurrentUserProfile, "Documents")) ||
            IsUnder(p, Path.Combine(ctx.CurrentUserProfile, "Desktop")))
            return true;

        return false;
    }

    /// <summary>Helper 提权不改变此判定：其他用户目录一律 Keep。</summary>
    public static bool Owns(string normalizedPath, string currentUserProfile)
    {
        var usersRoot = FindUsersRoot(currentUserProfile);
        if (usersRoot is null) return true;
        var p = normalizedPath.TrimEnd('\\');
        if (!IsUnder(p, usersRoot)) return true;

        var relative = p[usersRoot.Length..].TrimStart('\\');
        var first = relative.Split('\\')[0];
        if (first.Length == 0) return true;                       // C:\Users 本身视为公共，保守 Keep
        var mine = currentUserProfile.TrimEnd('\\').Split('\\').LastOrDefault() ?? "";
        if (string.Equals(first, mine, StringComparison.OrdinalIgnoreCase)) return true;
        return false;                                              // 其他用户 / Public 一律 Keep
    }

    private static string? FindUsersRoot(string profile)
    {
        try
        {
            var parent = Directory.GetParent(profile.TrimEnd('\\'))?.FullName;
            return parent;
        }
        catch { return null; }
    }

    public static bool IsUnder(string path, string root)
    {
        if (string.IsNullOrEmpty(root)) return false;
        var r = root.TrimEnd('\\');
        var p = path.TrimEnd('\\');
        if (string.Equals(p, r, StringComparison.OrdinalIgnoreCase)) return true;
        return p.Length > r.Length &&
               p.StartsWith(r, StringComparison.OrdinalIgnoreCase) &&
               p[r.Length] == '\\';
    }
}
