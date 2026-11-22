namespace Sweep.Core.Software;

/// <summary>
/// 一条“失效卸载项”的判定结论。仅当卸载登记指向的程序与安装目录都已不存在时才判失效，
/// 用于清理“软件已不在、注册表登记仍在”的残留；正常软件项永远不会被判定为残留。
/// </summary>
public sealed record ResidueVerdict(
    bool IsOrphan,
    string Reason,
    string? UninstallerPath,
    string? InstallLocation)
{
    public static ResidueVerdict Healthy(string reason) => new(false, reason, null, null);
}

/// <summary>
/// 纯逻辑：判定一条卸载登记是否已失效。文件系统存在性由调用方以委托注入，
/// 使判定可在无注册表 / 无真实磁盘的测试中验证。
/// </summary>
public static class UninstallResidue
{
    /// <summary>
    /// 保守三重条件，全部满足才算失效：
    /// 1) 非系统组件、有卸载命令（否则本来就不该出现在清单）；
    /// 2) 卸载器目标路径已不存在；
    /// 3) 若登记了安装目录，则目录也必须已不存在（未登记目录时仅凭卸载器失效不足以判定）。
    /// 任一条件不满足即判健康，绝不误删正常软件项。
    /// </summary>
    public static ResidueVerdict Evaluate(
        SoftwareEntry entry,
        Func<string, bool> fileExists,
        Func<string, bool> directoryExists)
    {
        if (entry.IsSystemComponent)
            return ResidueVerdict.Healthy("系统组件，保留");
        if (string.IsNullOrWhiteSpace(entry.UninstallString))
            return ResidueVerdict.Healthy("未登记卸载命令，保留");

        var command = UninstallCommand.Parse(entry.UninstallString);
        if (command is null)
            return ResidueVerdict.Healthy("卸载命令无法解析，保留");

        // 系统命令（msiexec / rundll32）没有可直接判存在的可执行文件，保守不作残留判定。
        if (command.IsSystemCommand)
            return ResidueVerdict.Healthy("系统安装服务登记项，保留");

        string? uninstaller = command.FileName;
        bool uninstallerGone = !string.IsNullOrEmpty(uninstaller) && !fileExists(uninstaller);
        if (!uninstallerGone)
            return ResidueVerdict.Healthy("卸载程序仍存在，保留");

        bool hasLocation = !string.IsNullOrWhiteSpace(entry.InstallLocation);
        if (hasLocation)
        {
            bool locationGone = !directoryExists(entry.InstallLocation);
            if (!locationGone)
                return ResidueVerdict.Healthy("卸载程序缺失但安装目录仍在，保留");
        }
        else
        {
            // 未登记安装目录时，仅有“卸载器不存在”一项证据不足以判定残留。
            return ResidueVerdict.Healthy("未登记安装目录，证据不足，保留");
        }

        return new ResidueVerdict(true, "卸载程序与安装目录均已不存在，判定为失效残留",
            uninstaller, entry.InstallLocation);
    }
}
