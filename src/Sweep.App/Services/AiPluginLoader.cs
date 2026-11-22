using System.IO;
using System.Reflection;
using Sweep.Ai.Abstractions;
using Sweep.Core.Storage;

namespace Sweep.App.Services;

/// <summary>运行时反射加载可缺省插件；DLL 缺失 / 未同意 / 未配密钥一律返回 null（文档 3.2 / 6.6）。</summary>
public static class AiPluginLoader
{
    private const string PluginTypeName = "Sweep.Ai.Providers.AiPlugin";
    private static Assembly? _assembly;

    public static string Status { get; private set; } = "AI 未启用";

    private static Assembly? TryGetAssembly(out string status)
    {
        if (_assembly is not null) { status = ""; return _assembly; }
        var dll = Path.Combine(AppContext.BaseDirectory, "Sweep.Ai.Providers.dll");
        if (!File.Exists(dll))
        {
            status = "AI 面板不可用（插件未随安装分发，核心功能完整）";
            return null;
        }
        try
        {
            _assembly = Assembly.LoadFrom(dll);
            status = "";
            return _assembly;
        }
        catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or IOException)
        {
            status = "AI 插件加载失败，已按未启用处理：" + ex.Message;
            return null;
        }
    }

    public static IRecommendationEnricher? TryLoad(
        IAiStorage storage, AppSettings settings, Func<bool> isEnabled)
    {
        if (!isEnabled())
        {
            Status = "AI 未启用（默认关闭）";
            return null;
        }
        if (string.IsNullOrEmpty(settings.Ai.ConsentVersion))
        {
            Status = "AI 待确认：请先在设置中完成数据发送告知并勾选同意";
            return null;
        }
        var assembly = TryGetAssembly(out var status);
        if (assembly is null) { Status = status; return null; }

        try
        {
            var type = assembly.GetType(PluginTypeName)
                       ?? throw new MissingMethodException(PluginTypeName);
            var method = type.GetMethod("TryLoad", BindingFlags.Public | BindingFlags.Static)
                         ?? throw new MissingMethodException("TryLoad");
            var options = new AiOptions(
                settings.Ai.ProviderId ?? "",
                "",
                "v1",
                settings.Ai.MonthlyBudgetUsd,
                settings.Ai.MaxCandidatesPerScan,
                settings.Ai.MaxRequestsPerMonth,
                settings.Ai.PrivacyMode,
                TimeSpan.FromDays(30),
                TimeSpan.FromSeconds(15));
            bool consented = !string.IsNullOrEmpty(settings.Ai.ConsentVersion);
            var result = method.Invoke(null, new object[]
            {
                storage, options, isEnabled, consented, AppPaths.ProviderOverridesPath
            });
            var enricher = result as IRecommendationEnricher;
            Status = enricher is null
                ? "AI 未配置（Provider / 密钥缺失），核心功能完整"
                : "AI 已启用：仅对未知项提供建议，所有建议需逐项确认";
            return enricher;
        }
        catch (Exception ex) when (ex is TargetInvocationException or MissingMethodException or TypeLoadException or ArgumentException)
        {
            Status = "AI 插件不可用，已按未启用处理";
            ;
            return null;
        }
    }

    public static IReadOnlyList<ProviderDescriptor> GetProviders()
    {
        var assembly = TryGetAssembly(out _);
        if (assembly is null) return Array.Empty<ProviderDescriptor>();
        try
        {
            var type = assembly.GetType(PluginTypeName)!;
            var method = type.GetMethod("GetProviders", BindingFlags.Public | BindingFlags.Static)!;
            return method.Invoke(null, new object?[] { AppPaths.ProviderOverridesPath })
                   as IReadOnlyList<ProviderDescriptor> ?? Array.Empty<ProviderDescriptor>();
        }
        catch { return Array.Empty<ProviderDescriptor>(); }
    }

    public static string TranslateRule(string naturalLanguage)
    {
        var assembly = TryGetAssembly(out _);
        if (assembly is null) return "（AI 插件未安装，无法翻译指令）";
        try
        {
            var type = assembly.GetType("Sweep.Ai.Providers.RuleDslTranslator")!;
            var translate = type.GetMethod("Translate", BindingFlags.Public | BindingFlags.Static)!;
            var render = type.GetMethod("Render", BindingFlags.Public | BindingFlags.Static)!;
            var dsl = translate.Invoke(null, new object[] { naturalLanguage });
            return render.Invoke(null, new[] { dsl })?.ToString() ?? "（无法渲染规则）";
        }
        catch { return "（规则翻译失败，未写入任何规则）"; }
    }
}
