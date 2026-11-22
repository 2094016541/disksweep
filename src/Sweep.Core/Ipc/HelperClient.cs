using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using Sweep.Core.Recommendation;

namespace Sweep.Core.Ipc;

public sealed record HelperRunResult(bool Available, bool Degraded, string Message, int Deleted, IReadOnlyList<HelperFailure> Failed);

/// <summary>主进程侧：按需拉起 Helper；UAC 拒绝 / 超时 / 断管一律按「未提权」降级（文档 4.6.4）。</summary>
public static class HelperClient
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(12);

    public static string HelperExecutablePath => Path.Combine(AppContext.BaseDirectory, "Sweep.Helper.exe");

    public static async Task<HelperRunResult> RunAsync(
        IReadOnlyList<PlanItem> adminItems, string mode = "recycle", CancellationToken ct = default)
    {
        if (adminItems.Count == 0)
            return new HelperRunResult(true, false, "无需提权的清理项", 0, Array.Empty<HelperFailure>());
        if (!File.Exists(HelperExecutablePath))
            return new HelperRunResult(false, true,
                "提权助手未随安装分发，已跳过需要管理员的清理项；其余项目正常执行。", 0, Array.Empty<HelperFailure>());

        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var sessionId = Guid.NewGuid().ToString("N");
        var pipeName = HelperProtocol.LocalPipeName(nonce);
        Process? helper = null;

        try
        {
            var psi = new ProcessStartInfo(HelperExecutablePath)
            {
                Arguments = $"--nonce {nonce} --pid {Environment.ProcessId} --session {sessionId}",
                Verb = "runas",
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            helper = Process.Start(psi);
            if (helper is null)
                return Degraded("提权助手未能启动（UAC 可能被拒绝）");
        }
        catch (Win32Exception)
        {
            return Degraded("用户拒绝了 UAC 提权，已跳过需要管理员的清理项；其余项目正常执行。");
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ConnectTimeout);
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(timeout.Token);

            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };

            var lockedItems = adminItems
                .Select((p, i) => HelperProtocol.FromPlan(i, p.VolumeSerial, p.FileId, p.Path))
                .ToList();

            await writer.WriteLineAsync(HelperProtocol.Serialize(new HelperEnvelope
            {
                Op = "open-session",
                Nonce = nonce,
                Session = sessionId,
                Mode = mode,
                ClientPid = Environment.ProcessId,
                ClientImagePath = Environment.ProcessPath,
                Items = lockedItems
            }));

            var openReply = await ReadAsync(reader, timeout.Token);
            if (openReply is null || !openReply.Ok || string.IsNullOrEmpty(openReply.Token))
                return Degraded("提权助手拒绝了会话：" + (openReply?.Reason ?? "无响应"));

            await writer.WriteLineAsync(HelperProtocol.Serialize(new HelperEnvelope
            {
                Op = "delete",
                Token = openReply.Token,
                Session = sessionId,
                Indices = Enumerable.Range(0, lockedItems.Count).ToList()
            }));

            var reply = await ReadAsync(reader, timeout.Token);
            if (reply is null) return Degraded("提权助手连接中断，本批标记为未完成，不自动重试");
            // 必须校验信封：Helper 返回 Op="error"/Ok=false 时不能当成"完成 0 项"报成功。
            if (!string.Equals(reply.Op, "delete", StringComparison.Ordinal) || !reply.Ok)
                return Degraded("提权助手未确认删除：" + (reply.Reason ?? "未知原因") + "；本批标记为未完成，不自动重试");

            await writer.WriteLineAsync(HelperProtocol.Serialize(new HelperEnvelope { Op = "close" }));
            return new HelperRunResult(true, false,
                $"提权助手完成：成功 {reply.Deleted} 项 / 失败 {reply.Failed?.Count ?? 0} 项。{reply.Reason}",
                reply.Deleted, reply.Failed ?? new List<HelperFailure>());
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException)
        {
            return Degraded("提权助手不可用（" + ex.Message + "），已按未提权降级；本批不自动重试。");
        }
        finally
        {
            // 助手在收到 close / 30 s 无消息后自行退出，主进程不强制结束它
            helper?.Dispose();
        }
    }

    private static async Task<HelperEnvelope?> ReadAsync(StreamReader reader, CancellationToken ct)
    {
        var line = await reader.ReadLineAsync(ct);
        return HelperProtocol.Deserialize(line);
    }

    private static HelperRunResult Degraded(string message) =>
        new(false, true, message, 0, Array.Empty<HelperFailure>());
}
