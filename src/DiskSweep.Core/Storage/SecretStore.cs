using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DiskSweep.Core.Storage;

/// <summary>DPAPI(CurrentUser) 加密的 secrets.dat：API Key 与每安装随机脱敏盐（文档 6.4 / 6.7）。</summary>
public sealed class SecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DiskSweep.v1.local-secrets");
    private readonly string _path;
    private readonly Dictionary<string, string> _entries;

    public SecretStore(string? path = null)
    {
        _path = path ?? Path.Combine(DiskSweepDb.AppDataDirectory, "secrets.dat");
        _entries = Load();
    }

    private Dictionary<string, string> Load()
    {
        if (!File.Exists(_path)) return new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(_path), Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(plain)
                   ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or JsonException or UnauthorizedAccessException)
        {
            // 密钥缺失/损坏按"未配置"处理：不弹窗、不写 Error 日志（10.2）
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    public void Save()
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var json = JsonSerializer.SerializeToUtf8Bytes(_entries);
        var encrypted = ProtectedData.Protect(json, Entropy, DataProtectionScope.CurrentUser);
        var temp = _path + ".tmp";
        File.WriteAllBytes(temp, encrypted);
        File.Move(temp, _path, true);
    }

    public string? GetApiKey(string providerId) =>
        _entries.TryGetValue("apikey:" + providerId, out var v) && v.Length > 0 ? v : null;

    public void SetApiKey(string providerId, string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) _entries.Remove("apikey:" + providerId);
        else _entries["apikey:" + providerId] = key.Trim();
        Save();
    }

    /// <summary>128-bit 盐只存本地、不出本机，保证 &lt;DIR:xxxx&gt; token 跨扫描稳定且不可字典反推。</summary>
    public string GetOrCreateSalt()
    {
        if (_entries.TryGetValue("redaction-salt", out var salt) && salt.Length >= 32) return salt;
        salt = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        _entries["redaction-salt"] = salt;
        Save();
        return salt;
    }

    public static string Mask(string? key)
    {
        if (string.IsNullOrEmpty(key)) return "（未配置）";
        return key.Length <= 4 ? "****" : "****" + key[^4..];
    }
}
