using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Sweep.Core.Software;

namespace Sweep.App.Services;

/// <summary>
/// 版本升级的 winget 适配层。只读查询在后台进程执行；真正升级时打开一个可见的 cmd 控制台
/// 执行 winget upgrade --interactive，用户自己看到协议提示、下载与安装过程。
/// 这里绝不添加 --silent / --force / 自动接受协议等参数，也绝不自行下载或替换任何文件。
/// </summary>
public sealed class WingetUpgradeService : ISoftwareUpgradeService
{
    /// <summary>winget 查询可能因源更新卡住，统一加一个上限，避免界面永久转圈。</summary>
    private const int QueryTimeoutMs = 25_000;

    private readonly Lazy<string?> _detectedVersion;

    public WingetUpgradeService() =>
        _detectedVersion = new Lazy<string?>(DetectWinget, LazyThreadSafetyMode.ExecutionAndPublication);

    public bool IsAvailable => !string.IsNullOrWhiteSpace(_detectedVersion.Value);

    public string ToolStatus => IsAvailable
        ? $"winget {_detectedVersion.Value} · 可检查升级"
        : "未检测到 winget（Windows 程序包管理器），版本升级功能不可用";

    public async Task<UpgradeCatalogResult> GetUpgradableAsync(CancellationToken ct)
    {
        if (!IsAvailable)
            return new UpgradeCatalogResult(false, "未检测到 winget，无法检查升级。", Array.Empty<WingetPackage>());
        try
        {
            string output = await RunQueryAsync(
                new[] { "upgrade", "--disable-interactivity" }, ct);
            var packages = WingetOutputParser.ParseUpgradable(output);
            return new UpgradeCatalogResult(true, packages.Count == 0
                ? "winget 未报告可升级的软件（也可能只是尚未刷新源）。"
                : $"winget 报告 {packages.Count} 个软件有可用更新。", packages);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            return new UpgradeCatalogResult(false,
                "检查可升级软件未完成：" + ex.GetType().Name, Array.Empty<WingetPackage>());
        }
    }

    /// <summary>
    /// 按名称查候选包：<c>winget search</c> 的只读查询，不加 --exact，
    /// 让「福昕高级PDF编辑器」「VS Code」这类与包名不完全一致的名字也能找到候选。
    /// 结果只用于让用户挑包，绝不从这里直接发起升级。
    /// </summary>
    public async Task<UpgradeCatalogResult> SearchPackagesAsync(string query, CancellationToken ct)
    {
        string text = (query ?? "").Trim();
        if (text.Length == 0)
            return new UpgradeCatalogResult(false, "没有可用的软件名称，无法在 winget 中匹配包。",
                Array.Empty<WingetPackage>());
        if (text.Length > 120) text = text[..120];
        if (text.Any(char.IsControl))
            return new UpgradeCatalogResult(false, "软件名称含控制字符，已拒绝用作 winget 查询。",
                Array.Empty<WingetPackage>());
        if (!IsAvailable)
            return new UpgradeCatalogResult(false, "未检测到 winget，无法匹配包。", Array.Empty<WingetPackage>());

        try
        {
            string output = await RunQueryAsync(
                new[] { "search", "--query", text, "--disable-interactivity" }, ct);
            var packages = WingetOutputParser.ParseSearch(output);
            return new UpgradeCatalogResult(true, packages.Count == 0
                ? $"winget 源里没有与「{text}」匹配的包。"
                : $"在 winget 源里匹配到 {packages.Count} 个候选包。", packages);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            return new UpgradeCatalogResult(false, "在 winget 中匹配包未完成：" + ex.GetType().Name,
                Array.Empty<WingetPackage>());
        }
    }

    public async Task<SourceListResult> GetSourcesAsync(CancellationToken ct)
    {
        if (!IsAvailable)
            return new SourceListResult(false, "未检测到 winget，无法读取软件源。", Array.Empty<WingetSource>());
        try
        {
            string output = await RunQueryAsync(
                new[] { "source", "list", "--disable-interactivity" }, ct);
            var sources = WingetSources.ParseList(output);
            return new SourceListResult(true, sources.Count == 0
                ? "winget 未报告任何软件源。"
                : $"已发现 {sources.Count} 个软件源。", sources);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            return new SourceListResult(false,
                "读取软件源未完成：" + ex.GetType().Name, Array.Empty<WingetSource>());
        }
    }

    /// <summary>
    /// source 为空时并发扫描全部已配置源，再合并去重；单个源失败不会掩盖其他源的结果。
    /// 指定源时只查该源，用于用户明确锁定安装来源的场景。
    /// </summary>
    public async Task<VersionListResult> GetVersionsAsync(
        string packageId, string? source, CancellationToken ct)
    {
        string id = (packageId ?? "").Trim();
        if (!UpgradeRequest.IsValidId(id))
            return FailedVersions("winget 包 ID 无效，无法读取可用版本。");
        if (!IsAvailable)
            return FailedVersions("未检测到 winget，无法读取可用版本。");

        var sources = new List<string>();
        if (!string.IsNullOrWhiteSpace(source))
        {
            if (!WingetSources.IsValidName(source))
                return FailedVersions("软件源名称无效，无法读取可用版本。");
            sources.Add(source.Trim());
        }
        else
        {
            var discovered = await GetSourcesAsync(ct);
            if (!discovered.Ok || discovered.Sources.Count == 0)
                return FailedVersions(discovered.Ok
                    ? "未发现可用的 winget 软件源，无法扫描版本。"
                    : discovered.Message);
            sources.AddRange(discovered.Sources.Select(s => s.Name));
        }

        try
        {
            var scans = await Task.WhenAll(sources.Select(async name =>
            {
                try
                {
                    string output = await RunQueryAsync(new[]
                    {
                        "show", "--id", id, "--versions", "--exact", "--source", name,
                        "--disable-interactivity"
                    }, ct);
                    return (Name: name, Versions: WingetOutputParser.ParseVersionList(output), Ok: true);
                }
                catch (Exception ex) when (IsExpected(ex))
                {
                    return (Name: name, Versions: Array.Empty<string>(), Ok: false);
                }
            }));

            ct.ThrowIfCancellationRequested();
            var groups = scans.Where(s => s.Ok)
                .Select(s => new KeyValuePair<string, IReadOnlyList<string>>(s.Name, s.Versions))
                .ToArray();
            var failed = scans.Where(s => !s.Ok).Select(s => s.Name).ToArray();
            var versions = WingetSources.MergeVersions(groups);

            if (groups.Length == 0)
                return FailedVersions("所有软件源都未能读取版本，请检查网络或包名。", failed);

            string message = $"已扫描 {groups.Length} 个软件源，汇总 {versions.Count} 个可选版本。";
            if (failed.Length > 0) message += $"（{string.Join("、", failed)} 读取失败）";
            return new VersionListResult(true, message, versions, failed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            return FailedVersions("读取可用版本未完成：" + ex.GetType().Name);
        }
    }

    private static VersionListResult FailedVersions(
        string message, IReadOnlyList<string>? failed = null) =>
        new(false, message, Array.Empty<AvailableVersion>(), failed ?? Array.Empty<string>());

    /// <summary>
    /// 把预置源加入 winget。winget 1.2x 起修改源列表需要管理员权限（实测 1.29 直接报 0x8A150019），
    /// 所以这里用 runas 提权打开一个可见控制台执行 <c>winget source add</c>：
    /// 用户在 UAC 弹窗里点「是」才是真正的授权，Sweep 不代替用户同意任何东西。
    /// 提权进程拿不到输出，成功与否靠事后重读源列表核验。
    /// </summary>
    public async Task<SourceActionResult> EnableSourceAsync(
        WingetSourcePreset preset, CancellationToken ct)
    {
        if (preset is null || !WingetSources.IsValidName(preset.Name) || !WingetSources.IsValidUrl(preset.Url))
            return SourceActionResult.Fail("软件源名称或地址无效，已拒绝执行。");
        if (!IsAvailable) return SourceActionResult.Fail("未检测到 winget，无法添加软件源。");

        var args = WingetSourceCatalog.AddArguments(preset, _detectedVersion.Value);
        string command = "winget " + string.Join(" ", args.Select(Quote));
        try
        {
            // 幂等：已经配置过就不再提权，直接告诉用户已启用。
            var existing = await GetSourcesAsync(ct);
            if (existing.Sources.Any(s => string.Equals(s.Name, preset.Name, StringComparison.OrdinalIgnoreCase)))
                return new SourceActionResult(true, $"软件源「{preset.DisplayName}」已经在 winget 中启用。");

            var start = new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Normal
            };
            start.ArgumentList.Add("/d");
            start.ArgumentList.Add("/c");
            start.ArgumentList.Add("winget");
            foreach (string argument in args) start.ArgumentList.Add(argument);

            using var process = Process.Start(start);
            if (process is null) return SourceActionResult.Fail("无法启动提权进程。", command);

            if (!process.WaitForExit(60_000))
                return SourceActionResult.Fail(
                    "提权窗口还没结束，等它执行完再点一次「刷新源列表」核验。", command);

            ct.ThrowIfCancellationRequested();
            var after = await GetSourcesAsync(ct);
            return after.Sources.Any(s => string.Equals(s.Name, preset.Name, StringComparison.OrdinalIgnoreCase))
                ? new SourceActionResult(true,
                    $"已启用软件源「{preset.DisplayName}」：后续版本查询与升级都会带上 --source {preset.Name}。")
                : SourceActionResult.Fail(
                    $"提权窗口已结束，但 winget 里仍没有「{preset.Name}」。可在管理员终端里手动执行下面的命令。",
                    command);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return SourceActionResult.Fail("已取消管理员授权，没有添加任何软件源。", command);
        }
        catch (OperationCanceledException)
        {
            return SourceActionResult.Fail("已取消添加软件源。", command);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            return SourceActionResult.Fail("添加软件源未完成：" + ex.GetType().Name, command);
        }
    }

    /// <summary>仅用于把命令原样展示给用户：带空格的参数加引号，便于复制到终端。</summary>
    private static string Quote(string value) =>
        value.Length > 0 && !value.Any(char.IsWhiteSpace) ? value : '"' + value.Replace("\"", "\\\"") + '"';

    /// <summary>调用方（ViewModel）已完成二次确认；这里只负责把命令交给可见控制台。</summary>
    public SoftwareActionResult LaunchUpgrade(UpgradeRequest request)
    {
        if (!IsAvailable) return new(false, "未检测到 winget，无法执行升级。");

        // 直接用 ViewModel 校验过的请求，不在这里重建：
        // 重建任何请求都可能漏字段（曾漏掉 Source，导致用户锁定的源被悄悄丢弃）。
        IReadOnlyList<string> arguments;
        try { arguments = request.BuildArguments(); }
        catch (InvalidOperationException)
        {
            return new(false, "升级请求未通过校验，已拒绝执行。");
        }

        try
        {
            // /k 让控制台在命令结束后保留，用户能完整看到 winget 的输出与错误信息。
            // 每个参数单独进入 ArgumentList，带空格的版本号也不会被拆开或当成命令拼接。
            var start = new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Normal
            };
            start.ArgumentList.Add("/d");
            start.ArgumentList.Add("/k");
            start.ArgumentList.Add("winget");
            foreach (string argument in arguments) start.ArgumentList.Add(argument);

            Process.Start(start);
            return new(true,
                $"已打开 winget 控制台：winget {string.Join(" ", arguments)}。请在窗口中按提示完成升级；" +
                "Sweep 不会静默安装，也不会代替您接受协议。");
        }
        catch (Exception ex) when (ex is Win32Exception or IOException
                                       or InvalidOperationException or UnauthorizedAccessException)
        {
            return new(false, "启动 winget 控制台失败：" + ex.Message);
        }
    }

    private static bool IsExpected(Exception ex) => ex is Win32Exception or IOException
        or InvalidOperationException or UnauthorizedAccessException or OperationCanceledException;

    private static string? DetectWinget()
    {
        try
        {
            var start = new ProcessStartInfo("winget")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8
            };
            start.ArgumentList.Add("--version");
            using var process = Process.Start(start);
            if (process is null) return null;
            if (!process.WaitForExit(8_000))
            {
                try { process.Kill(entireProcessTree: true); } catch (Win32Exception) { }
                return null;
            }
            string text = process.StandardOutput.ReadToEnd();
            var match = Regex.Match(text, @"v?\d+\.\d+(?:\.\d+)?");
            return match.Success ? match.Value.TrimStart('v') : null;
        }
        catch (Exception ex) when (IsExpected(ex)) { return null; }
    }

    private static async Task<string> RunQueryAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(QueryTimeoutMs);

        var start = new ProcessStartInfo("winget")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (string argument in args) start.ArgumentList.Add(argument);

        using var process = Process.Start(start)
                            ?? throw new InvalidOperationException("无法启动 winget 进程。");
        using var kill = timeout.Token.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { }
        });

        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(timeout.Token);
        string output = await stdout;
        _ = await stderr;     // 排空错误流，避免缓冲区写满导致进程卡死
        return output;
    }
}

/// <summary>
/// 未注入升级服务时的空实现：升级入口保持可见但不可用，且不触碰任何进程。
/// </summary>
public sealed class NullSoftwareUpgradeService : ISoftwareUpgradeService
{
    public static NullSoftwareUpgradeService Instance { get; } = new();

    public bool IsAvailable => false;
    public string ToolStatus => "版本升级服务不可用。";

    public Task<SourceListResult> GetSourcesAsync(CancellationToken ct) =>
        Task.FromResult(new SourceListResult(false, "版本升级服务不可用。", Array.Empty<WingetSource>()));

    public Task<UpgradeCatalogResult> GetUpgradableAsync(CancellationToken ct) =>
        Task.FromResult(new UpgradeCatalogResult(false, "版本升级服务不可用。", Array.Empty<WingetPackage>()));

    public Task<UpgradeCatalogResult> SearchPackagesAsync(string query, CancellationToken ct) =>
        Task.FromResult(new UpgradeCatalogResult(false, "版本升级服务不可用。", Array.Empty<WingetPackage>()));

    public Task<VersionListResult> GetVersionsAsync(string packageId, string? source, CancellationToken ct) =>
        Task.FromResult(new VersionListResult(
            false, "版本升级服务不可用。", Array.Empty<AvailableVersion>(), Array.Empty<string>()));

    public Task<SourceActionResult> EnableSourceAsync(WingetSourcePreset preset, CancellationToken ct) =>
        Task.FromResult(SourceActionResult.Fail("版本升级服务不可用。"));

    public SoftwareActionResult LaunchUpgrade(UpgradeRequest request) =>
        new(false, "版本升级服务不可用。");
}
