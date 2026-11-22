using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Sweep.Core.Executors;
using Sweep.Core.Ipc;
using Sweep.Core.Scanning;

namespace Sweep.Helper;

/// <summary>
/// 提权助手：Helper 创建管道并作服务端，能力面只有 open-session / delete / close。
/// 不解析规则、不打开 SQLite、不联网、不提供通用执行能力；单次任务结束即退出。
/// </summary>
internal static class Program
{
    private sealed record Options(string Nonce, int Pid, string Session, string PipeName);

    private static async Task<int> Main(string[] args)
    {
        var options = Parse(args);
        if (options is null) return 2;
        try { return await RunAsync(options); }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Sweep.Helper: " + ex.Message);
            return 1;
        }
    }

    private static Options? Parse(string[] args)
    {
        string? nonce = null, session = null;
        int pid = 0;
        for (int i = 0; i < args.Length - 1; i += 2)
        {
            switch (args[i])
            {
                case "--nonce": nonce = args[i + 1]; break;
                case "--pid": int.TryParse(args[i + 1], out pid); break;
                case "--session": session = args[i + 1]; break;
            }
        }
        if (string.IsNullOrEmpty(nonce) || string.IsNullOrEmpty(session) || pid == 0) return null;
        return new Options(nonce, pid, session, HelperProtocol.LocalPipeName(nonce));
    }

    private static async Task<int> RunAsync(Options options)
    {
        var security = new PipeSecurity();
        var userSid = WindowsIdentity.GetCurrent().User
                      ?? throw new InvalidOperationException("无法取得当前用户 SID");
        security.AddAccessRule(new PipeAccessRule(userSid, PipeAccessRights.FullControl, AccessControlType.Allow));

        using var pipe = NamedPipeServerStreamAcl.Create(options.PipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await pipe.WaitForConnectionAsync(timeout.Token);
        var peer = PeerValidator.Validate(pipe.SafePipeHandle.DangerousGetHandle(), options.Pid, null);
        if (!peer.Ok)
        {
            await WriteAsync(pipe, new HelperEnvelope { Op = "open-session", Ok = false, Reason = peer.Reason });
            return 3;
        }

        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);

        var open = HelperProtocol.Deserialize(await reader.ReadLineAsync(timeout.Token));
        if (open is null || open.Op != "open-session" ||
            !string.Equals(open.Nonce, options.Nonce, StringComparison.Ordinal) ||
            !string.Equals(open.Session, options.Session, StringComparison.Ordinal))
        {
            await WriteAsync(pipe, new HelperEnvelope { Op = "open-session", Ok = false, Reason = "会话参数校验失败" });
            return 4;
        }

        var locked = (open.Items ?? new List<LockedItem>())
            .OrderBy(i => i.Index)
            .ToList();                       // open-session 一次性下发并锁定清单
        string sessionToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        string mode = string.Equals(open.Mode, "permanent", StringComparison.OrdinalIgnoreCase) ? "permanent" : "recycle";

        await WriteAsync(pipe, new HelperEnvelope
        {
            Op = "open-session", Ok = true, Token = sessionToken,
            Reason = $"会话已建立，锁定 {locked.Count} 个对象"
        });
        timeout.CancelAfter(TimeSpan.FromSeconds(30));

        while (!timeout.IsCancellationRequested)
        {
            var message = HelperProtocol.Deserialize(await reader.ReadLineAsync(timeout.Token));
            if (message is null || message.Op == "close") break;
            timeout.CancelAfter(TimeSpan.FromSeconds(30));

            if (message.Op != "delete" ||
                !string.Equals(message.Token, sessionToken, StringComparison.Ordinal) ||
                !string.Equals(message.Session, options.Session, StringComparison.Ordinal))
            {
                await WriteAsync(pipe, new HelperEnvelope { Op = "error", Ok = false, Reason = "不支持的操作或会话令牌无效" });
                continue;
            }

            var failed = new List<HelperFailure>();
            var targets = new List<(string Path, int Index)>();
            foreach (var index in message.Indices ?? new List<int>())
            {
                if (index < 0 || index >= locked.Count)
                {
                    failed.Add(new HelperFailure { Index = index, Reason = "索引不在已锁定清单内" });
                    continue;
                }
                var item = locked[index];
                // TOCTOU：紧邻删除调用前重新打开句柄比对 volume/fileId
                var identity = ScanEngine.GetIdentity(item.Path);
                if (item.FileId != "0" &&
                    (identity is null ||
                     identity.VolumeSerial.ToString() != item.Volume ||
                     identity.FileId.ToString() != item.FileId))
                {
                    failed.Add(new HelperFailure { Index = index, Path = item.Path, Reason = "toctou-mismatch" });
                    continue;
                }
                targets.Add((item.Path, index));
            }

            var (hr, aborted) = Win32Shell.DeleteBatch(
                targets.Select(t => t.Path).ToArray(), mode == "recycle");
            int deleted = 0;
            foreach (var target in targets)
            {
                if (!File.Exists(target.Path) && !Directory.Exists(target.Path)) deleted++;
                else failed.Add(new HelperFailure
                {
                    Index = target.Index, Path = target.Path, Hr = hr,
                    Reason = aborted ? "operation-aborted" : "in-use-or-denied"
                });
            }

            await WriteAsync(pipe, new HelperEnvelope
            {
                Op = "delete", Ok = true, Deleted = deleted, Failed = failed,
                Reason = hr == 0 ? "完成" : $"批次返回 HRESULT 0x{hr:X8}，未重试、未降级"
            });
        }
        return 0;
    }

    private static async Task WriteAsync(Stream stream, HelperEnvelope envelope)
    {
        var bytes = Encoding.UTF8.GetBytes(HelperProtocol.Serialize(envelope) + "\n");
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();
    }
}
