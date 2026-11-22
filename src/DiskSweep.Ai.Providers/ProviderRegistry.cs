using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using DiskSweep.Ai.Abstractions;

namespace DiskSweep.Ai.Providers;

/// <summary>厂商清单不写在代码里：内置 JSON 注册表 + 用户 userOverrides 分区（文档 6.4）。</summary>
public static class ProviderRegistry
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static IReadOnlyList<ProviderDescriptor> Load(string? userOverridesPath = null)
    {
        var list = new List<ProviderDescriptor>();
        using var stream = typeof(ProviderRegistry).Assembly
            .GetManifestResourceStream("DiskSweep.Ai.Providers.providers.json");
        if (stream is not null)
        {
            using var reader = new StreamReader(stream);
            var root = JsonNode.Parse(reader.ReadToEnd()) as JsonObject;
            if (root?["providers"] is JsonArray builtins)
                foreach (var node in builtins)
                {
                    var d = JsonSerializer.Deserialize<RegistryEntry>(node?.ToJsonString() ?? "{}", Options);
                    if (d is not null && d.Id.Length > 0) list.Add(d.ToDescriptor());
                }
        }

        if (!string.IsNullOrEmpty(userOverridesPath) && File.Exists(userOverridesPath))
        {
            try
            {
                var root = JsonNode.Parse(File.ReadAllText(userOverridesPath)) as JsonObject;
                var disabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (root?["disabled"] is JsonArray off)
                    foreach (var n in off) if (n is not null) disabled.Add(n.GetValue<string>());

                if (root?["userOverrides"] is JsonArray overrides)
                {
                    foreach (var node in overrides)
                    {
                        var d = JsonSerializer.Deserialize<RegistryEntry>(node?.ToJsonString() ?? "{}", Options);
                        if (d is null || d.Id.Length == 0) continue;
                        int index = list.FindIndex(x => string.Equals(x.Id, d.Id, StringComparison.OrdinalIgnoreCase));
                        if (index >= 0) list[index] = Merge(list[index], d);
                        else list.Add(d.ToDescriptor());
                    }
                }
                list.RemoveAll(d => disabled.Contains(d.Id));
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // 覆写文件损坏时退回内置清单，不影响离线核心功能
            }
        }
        return list;
    }

    public static ProviderDescriptor? Find(string? overridesPath, string id) =>
        Load(overridesPath).FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));

    private static ProviderDescriptor Merge(ProviderDescriptor builtin, RegistryEntry o) => builtin with
    {
        Label = string.IsNullOrEmpty(o.Label) ? builtin.Label : o.Label,
        Kind = string.IsNullOrEmpty(o.Kind) ? builtin.Kind : o.Kind,
        BaseUrl = string.IsNullOrEmpty(o.BaseUrl) ? builtin.BaseUrl : o.BaseUrl,
        DefaultModel = string.IsNullOrEmpty(o.DefaultModel) ? builtin.DefaultModel : o.DefaultModel,
        JsonMode = string.IsNullOrEmpty(o.JsonMode) ? builtin.JsonMode : o.JsonMode,
        PricePer1MInputTokens = o.PricePer1MInputTokens ?? builtin.PricePer1MInputTokens,
        PricePer1MOutputTokens = o.PricePer1MOutputTokens ?? builtin.PricePer1MOutputTokens
    };

    internal sealed class RegistryEntry
    {
        public string Id { get; set; } = "";
        public string Label { get; set; } = "";
        public string Kind { get; set; } = "openai-compatible";
        public string BaseUrl { get; set; } = "";
        public string DefaultModel { get; set; } = "any";
        public string JsonMode { get; set; } = "response_format";
        public bool Builtin { get; set; }
        public double? PricePer1MInputTokens { get; set; }
        public double? PricePer1MOutputTokens { get; set; }

        public ProviderDescriptor ToDescriptor() => new(
            Id, string.IsNullOrEmpty(Label) ? Id : Label,
            string.IsNullOrEmpty(Kind) ? "openai-compatible" : Kind,
            BaseUrl, string.IsNullOrEmpty(DefaultModel) ? "any" : DefaultModel,
            JsonMode, Builtin,
            PricePer1MInputTokens ?? 0, PricePer1MOutputTokens ?? 0);
    }
}
