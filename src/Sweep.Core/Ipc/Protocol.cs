using System.Globalization;
using System.Text.Json;

namespace Sweep.Core.Ipc;

/// <summary>命名管道 JSON Lines 协议（文档 4.6.1）。volume/fileId 一律用十进制字符串，避免 JSON number 丢精度。</summary>
public sealed class LockedItem
{
    public int Index { get; set; }
    public string Volume { get; set; } = "0";
    public string FileId { get; set; } = "0";
    public string Path { get; set; } = "";
}

public sealed class HelperFailure
{
    public int Index { get; set; }
    public string Path { get; set; } = "";
    public int Hr { get; set; }
    public string Reason { get; set; } = "";
}

/// <summary>请求与响应共用一个信封，按 Op 区分；无通用执行能力。</summary>
public sealed class HelperEnvelope
{
    public string Op { get; set; } = "";
    public string? Token { get; set; }
    public string? Session { get; set; }
    public string? Mode { get; set; }
    public string? Nonce { get; set; }
    public int ClientPid { get; set; }
    public string? ClientImagePath { get; set; }
    public List<LockedItem>? Items { get; set; }
    public List<int>? Indices { get; set; }
    public bool Ok { get; set; }
    public int Deleted { get; set; }
    public string? Reason { get; set; }
    public List<HelperFailure>? Failed { get; set; }
}

public static class HelperProtocol
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public static string PipeName(string nonce) => $@"\\.\pipe\Sweep.Helper.{nonce}";
    public static string LocalPipeName(string nonce) => $"Sweep.Helper.{nonce}";

    public static string Serialize(HelperEnvelope envelope) => JsonSerializer.Serialize(envelope, Json);

    public static HelperEnvelope? Deserialize(string? line) =>
        string.IsNullOrWhiteSpace(line) ? null : JsonSerializer.Deserialize<HelperEnvelope>(line, Json);

    public static LockedItem FromPlan(int index, ulong volume, ulong fileId, string path) => new()
    {
        Index = index,
        Volume = volume.ToString(CultureInfo.InvariantCulture),
        FileId = fileId.ToString(CultureInfo.InvariantCulture),
        Path = path
    };
}
