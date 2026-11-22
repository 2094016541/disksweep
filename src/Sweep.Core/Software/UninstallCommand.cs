using System.Text.RegularExpressions;

namespace Sweep.Core.Software;

/// <summary>
/// 从 UninstallString 拆出的可执行程序与参数。
/// 只做解析，不启动进程；启动动作由 App 服务层在用户二次确认后完成。
/// </summary>
public sealed record UninstallCommand(string FileName, string Arguments, bool IsSilent)
{
    /// <summary>
    /// msiexec 的 /I 是「安装/维护」，/X 才是卸载。
    /// 有些厂商把 UninstallString 写成 "MsiExec.exe /I{ProductCode}"，直接执行只会打开维护界面，
    /// 用户点了「卸载」却看到「修改/修复」——本机 146 项里有 12 项是这种写法。
    /// 只在参数"仅含一个产品码"时改写，带其它开关（/qb、TRANSFORMS 等）一律原样保留。
    /// </summary>
    private static readonly Regex MsiProductCodeOnly = new(
        @"^\s*/I\s*(\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\})\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>静默卸载参数前缀：命中时 Sweep 拒绝直接执行，避免无提示卸载。</summary>
    private static readonly string[] SilentPrefixes =
    {
        "/q", "/silent", "/verysilent", "/passive", "/s", "-q", "-s", "--silent", "--quiet"
    };

    public static UninstallCommand? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string text = raw.Trim();
        // 多行 / 控制字符一律拒绝：卸载串来自注册表，不应被当成脚本解释。
        if (text.Any(c => c is '\r' or '\n' or '\0')) return null;

        string file;
        string args;
        if (text[0] == '"')
        {
            int close = text.IndexOf('"', 1);
            if (close <= 1) return null;
            file = text[1..close];
            args = text[(close + 1)..].Trim();
        }
        else
        {
            // 兼容未加引号但路径含空格的老式卸载串：以 .exe 结尾处切分。
            int exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exe >= 0 && exe + 4 <= text.Length &&
                (exe + 4 == text.Length || char.IsWhiteSpace(text[exe + 4])))
            {
                file = text[..(exe + 4)];
                args = text[(exe + 4)..].Trim();
            }
            else
            {
                int space = text.IndexOf(' ');
                file = space < 0 ? text : text[..space];
                args = space < 0 ? "" : text[(space + 1)..].Trim();
            }
        }

        file = Environment.ExpandEnvironmentVariables(file.Trim().Trim('"')).Trim();
        if (file.Length == 0 || file.Contains('"')) return null;

        return new UninstallCommand(file, NormalizeMsiArguments(file, args), HasSilentSwitch(args));
    }

    private static string NormalizeMsiArguments(string file, string arguments)
    {
        string exe = Path.GetFileName(file);
        if (!string.Equals(exe, "msiexec.exe", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(exe, "msiexec", StringComparison.OrdinalIgnoreCase))
            return arguments;

        var match = MsiProductCodeOnly.Match(arguments);
        return match.Success ? "/X" + match.Groups[1].Value : arguments;
    }

    /// <summary>MSI 的 /qn、NSIS 的 /S、InnoSetup 的 /VERYSILENT 等一律视为静默。</summary>
    public static bool HasSilentSwitch(string arguments)
    {
        foreach (var part in arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string flag = part.Trim().Trim('"');
            foreach (var prefix in SilentPrefixes)
                if (flag.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>不含路径分隔符的命令依赖系统 PATH（msiexec.exe / rundll32.exe 等），交由 Shell 解析。</summary>
    public bool IsSystemCommand =>
        !FileName.Contains(Path.DirectorySeparatorChar) &&
        !FileName.Contains(Path.AltDirectorySeparatorChar);
}
