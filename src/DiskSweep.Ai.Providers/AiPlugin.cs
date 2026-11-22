using DiskSweep.Ai.Abstractions;

namespace DiskSweep.Ai.Providers;

/// <summary>App 通过反射调用本类；Providers DLL 缺失时 TryLoad 不会被调用，主程序走纯规则路径。</summary>
public static class AiPlugin
{
    public const string PromptVersion = "v1";

    public static IReadOnlyList<ProviderDescriptor> GetProviders(string? overridesPath = null) =>
        ProviderRegistry.Load(overridesPath);

    public static IAiProvider? CreateProvider(ProviderDescriptor descriptor, string? apiKey) =>
        descriptor.Kind.ToLowerInvariant() switch
        {
            "openai-compatible" => new OpenAiCompatibleProvider(descriptor, apiKey),
            "ollama" => new OllamaProvider(descriptor),
            "anthropic" => string.IsNullOrWhiteSpace(apiKey) ? null : new AnthropicProvider(descriptor, apiKey!),
            _ => null
        };

    /// <summary>未完成同意告知 / 未配密钥 / 注册表缺失一律返回 null，不抛异常、不弹错误框、不发起请求。</summary>
    public static IRecommendationEnricher? TryLoad(
        IAiStorage storage, AiOptions options, Func<bool> isEnabled, bool consented,
        string? overridesPath = null)
    {
        if (!isEnabled() || !consented) return null;
        if (string.IsNullOrWhiteSpace(options.ProviderId)) return null;

        var descriptor = ProviderRegistry.Find(overridesPath, options.ProviderId);
        if (descriptor is null) return null;
        options = options with { Model = descriptor.DefaultModel };   // 缓存指纹须与实际模型一致

        var key = storage.GetSecret(descriptor.Id);
        bool needsKey = !string.Equals(descriptor.Kind, "ollama", StringComparison.OrdinalIgnoreCase);
        if (needsKey && string.IsNullOrWhiteSpace(key)) return null;

        var provider = CreateProvider(descriptor, key);
        return provider is null ? null : new AiGateway(provider, storage, options, descriptor, isEnabled);
    }
}
