using System.Text.RegularExpressions;

namespace Sweep.Core.Software;

/// <summary>
/// 一次「升级到某版本」的纯数据请求。TargetVersion 为 null/空表示默认升级到最新版
/// （winget 的 --version 缺省语义），非空表示升级到指定版本。
/// Source 为 null/空表示交给 winget 自己选源；非空表示必须在指定软件源中安装该版本。
/// 所有进入 winget 命令行的文本都在这里做白名单校验，杜绝参数注入与静默安装开关。
/// </summary>
public sealed record UpgradeRequest(string PackageId, string? TargetVersion, string? Source = null)
{
    /// <summary>winget 包 ID 白名单：字母数字与 . _ + -，不含空格、反斜杠或 shell 元字符。</summary>
    private static readonly Regex IdPattern =
        new(@"^[A-Za-z0-9][A-Za-z0-9._+\-]{0,127}$", RegexOptions.Compiled);

    /// <summary>版本白名单：允许「8.0 Beta 10」这类带空格/括号的版本，但拒绝引号与 shell 元字符。</summary>
    private static readonly Regex VersionPattern =
        new(@"^[A-Za-z0-9][A-Za-z0-9._+\-() ]{0,63}$", RegexOptions.Compiled);

    public static bool IsValidId(string? id) => id is not null && IdPattern.IsMatch(id.Trim());
    public static bool IsValidVersionToken(string? version) =>
        version is not null && VersionPattern.IsMatch(version.Trim());

    /// <summary>兼容未指定软件源的调用：交给 winget 自行选源。</summary>
    public static UpgradeRequest? TryCreate(string? packageId, string? targetVersion, out string error) =>
        TryCreate(packageId, targetVersion, null, out error);

    /// <summary>把用户输入收敛成合法请求；失败时给出可直接展示的原因。</summary>
    public static UpgradeRequest? TryCreate(
        string? packageId, string? targetVersion, string? source, out string error)
    {
        string id = (packageId ?? "").Trim();
        if (!IsValidId(id))
        {
            error = "winget 包 ID 无效：只允许字母、数字和 . _ + -，且不能包含空格或路径分隔符。";
            return null;
        }

        string version = (targetVersion ?? "").Trim();
        string sourceName = (source ?? "").Trim();
        if (sourceName.Length > 0 && !WingetSources.IsValidName(sourceName))
        {
            error = "软件源名称无效：只允许字母开头的字母、数字和 . _ -。请从下拉选择软件源。";
            return null;
        }

        if (version.Length == 0)
        {
            error = "";
            return new UpgradeRequest(id, null, sourceName.Length == 0 ? null : sourceName);
        }
        if (!IsValidVersionToken(version))
        {
            error = "指定版本含有不允许的字符或过长；请从下拉选择 winget 报告的版本号（如 2.8.55）。";
            return null;
        }

        error = "";
        return new UpgradeRequest(id, version, sourceName.Length == 0 ? null : sourceName);
    }

    /// <summary>
    /// 生成 winget 的参数序列（不含可执行文件名）。
    /// 固定带 --interactive 保证安装向导可见；绝不出现 --silent / --force / 任何自动接受协议的参数。
    /// </summary>
    public IReadOnlyList<string> BuildArguments()
    {
        if (!IsValidId(PackageId) ||
            (TargetVersion is not null && !IsValidVersionToken(TargetVersion)) ||
            (Source is not null && !WingetSources.IsValidName(Source)))
            throw new InvalidOperationException("升级请求未通过校验，拒绝构造 winget 命令。");

        var arguments = new List<string> { "upgrade", "--id", PackageId, "--exact" };
        if (!string.IsNullOrWhiteSpace(Source))
        {
            arguments.Add("--source");
            arguments.Add(Source!.Trim());
        }
        if (!string.IsNullOrWhiteSpace(TargetVersion))
        {
            arguments.Add("--version");
            arguments.Add(TargetVersion!.Trim());
        }
        arguments.Add("--interactive");
        return arguments;
    }

    public string Describe() => "winget " + string.Join(" ", BuildArguments());
    public bool IsLatest => string.IsNullOrWhiteSpace(TargetVersion);
}
