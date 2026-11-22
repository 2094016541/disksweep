using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;

namespace Sweep.Helper;

/// <summary>
/// 4.6.2 / 4.6.3 对端身份校验：映像路径、会话 ID、完整性级别、文件可写性、Authenticode 签名。
/// Debug 构建使用开发期白名单（未签名放行）；Release 强制验签且不编译进该白名单。
/// </summary>
internal static class PeerValidator
{
    private const uint TokenQuery = 0x0008;
    private const int TokenIntegrityLevel = 25;
    private const int MediumIntegrityRid = 0x2000;
    private const int HighIntegrityRid = 0x3000;

#if DEBUG
    private static readonly HashSet<string> DebugCertificateWhitelist = new(StringComparer.OrdinalIgnoreCase)
    {
        // 开发期测试证书指纹在此登记；Release 构建不编译本集合
    };
#endif

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint clientProcessId);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr tokenHandle, int infoClass,
        IntPtr info, int infoLength, out int returnLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    public static (bool Ok, string Reason) Validate(IntPtr pipeHandle, int declaredPid, string? declaredImage)
    {
        if (!GetNamedPipeClientProcessId(pipeHandle, out uint pid))
            return (false, "无法获取管道对端进程 ID");
        if (declaredPid != 0 && declaredPid != (int)pid)
            return (false, "对端进程 ID 与声明不一致");

        Process peer;
        try { peer = Process.GetProcessById((int)pid); }
        catch { return (false, "对端进程已退出"); }

        string image;
        try { image = peer.MainModule?.FileName ?? ""; }
        catch { return (false, "无法读取对端映像路径"); }
        if (image.Length == 0) return (false, "对端映像路径为空");
        if (!string.IsNullOrEmpty(declaredImage) &&
            !string.Equals(image, declaredImage, StringComparison.OrdinalIgnoreCase))
            return (false, "对端映像路径与声明不一致");
        if (!string.Equals(Path.GetFileName(image), "Sweep.App.exe", StringComparison.OrdinalIgnoreCase))
            return (false, "对端不是 Sweep 主程序");

        if (peer.SessionId != Process.GetCurrentProcess().SessionId)
            return (false, "对端会话 ID 不一致");

        int? integrity = GetIntegrityLevel(peer.Handle);
        if (integrity is null) return (false, "无法读取对端完整性级别");
        if (integrity > HighIntegrityRid || integrity < MediumIntegrityRid)
            return (false, $"对端完整性级别异常（RID {integrity}）");

#if !DEBUG
        if (IsWritableByNonAdmin(image))
            return (false, "主程序映像可被非管理员写入，疑似 DLL 侧加载风险");
        if (!VerifyReleaseSignature(image))
            return (false, "主程序缺少有效的 Authenticode 签名");
#else
        VerifyDebugSignature(image);
#endif
        return (true, "ok");
    }

    private static int? GetIntegrityLevel(IntPtr processHandle)
    {
        IntPtr token = IntPtr.Zero, buffer = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(processHandle, TokenQuery, out token)) return null;
            GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out int length);
            if (length <= 0) return null;
            buffer = Marshal.AllocHGlobal(length);
            if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, length, out _)) return null;
            IntPtr sid = Marshal.ReadIntPtr(buffer);   // TOKEN_INTEGRITY_LEVEL.Labels.pSid
            if (sid == IntPtr.Zero) return null;
            IntPtr countPtr = GetSidSubAuthorityCount(sid);
            if (countPtr == IntPtr.Zero) return null;
            byte count = Marshal.ReadByte(countPtr);
            if (count == 0) return null;
            IntPtr sub = GetSidSubAuthority(sid, (uint)(count - 1));
            return sub == IntPtr.Zero ? null : Marshal.ReadInt32(sub);
        }
        catch { return null; }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            if (token != IntPtr.Zero) CloseHandle(token);
        }
    }

    private static bool IsWritableByNonAdmin(string image)
    {
        try
        {
            var acl = new FileInfo(image).GetAccessControl();
            var rules = acl.GetAccessRules(true, true, typeof(SecurityIdentifier));
            var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null).Value;
            var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null).Value;
            var me = WindowsIdentity.GetCurrent().User?.Value ?? "";
            foreach (FileSystemAccessRule rule in rules)
            {
                if (rule.AccessControlType != AccessControlType.Allow) continue;
                var sid = rule.IdentityReference.Value;
                if (sid != users && sid != everyone && sid != me) continue;
                if ((rule.FileSystemRights & (FileSystemRights.Write | FileSystemRights.Modify |
                                             FileSystemRights.FullControl | FileSystemRights.WriteData)) != 0)
                    return true;
            }
            return false;
        }
        catch { return false; }
    }

    private static X509Certificate2? LoadSigningCertificate(string image)
    {
#pragma warning disable SYSLIB0057
        using var raw = X509Certificate.CreateFromSignedFile(image);   // 未签名时抛 CryptographicException
#pragma warning restore SYSLIB0057
        return X509CertificateLoader.LoadCertificate(raw.Export(X509ContentType.Cert));
    }

#if DEBUG
    private static void VerifyDebugSignature(string image)
    {
        try
        {
            using var cert = LoadSigningCertificate(image);
            if (cert is not null) _ = DebugCertificateWhitelist.Contains(cert.Thumbprint);   // 测试证书登记点
        }
        catch { /* 开发构建允许未签名主程序 */ }
    }
#else
    private static bool VerifyReleaseSignature(string image)
    {
        try
        {
            using var cert = LoadSigningCertificate(image);
            if (cert is null) return false;
            using var chain = new X509Chain();
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            return chain.Build(cert);
        }
        catch { return false; }
    }
#endif
}
