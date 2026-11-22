using System.IO;
using DiskSweep.Ai.Abstractions;
using DiskSweep.Core;
using DiskSweep.Core.Executors;
using DiskSweep.Core.Rules;
using DiskSweep.Core.Storage;

namespace DiskSweep.App.Services;

/// <summary>组合根：加载设置 → 构造规则引擎 → 构造闸门上下文 → 构造执行器 → 可选加载 AI 插件。</summary>
public sealed class AppServices : IAppContext
{
    public DiskSweepDb Db { get; }
    public SecretStore Secrets { get; }
    public AiStorageAdapter AiStorage { get; }
    public LoadedSettings Loaded { get; private set; } = null!;
    public AppSettings Settings => Loaded.Settings;
    public RuleEngine Rules { get; private set; } = null!;
    public SafetyContext Safety { get; private set; } = null!;
    public DeleteExecutor Executor { get; private set; } = null!;
    public IRecommendationEnricher? Enricher { get; private set; }

    private AppServices(DiskSweepDb db, SecretStore secrets)
    {
        Db = db;
        Secrets = secrets;
        AiStorage = new AiStorageAdapter(db, secrets);
    }

    public static AppServices Initialize()
    {
        var db = new DiskSweepDb();
        db.Initialize();
        var services = new AppServices(db, new SecretStore());
        services.ReloadAll();
        ;
        return services;
    }

    public bool AiEnabled => Settings.Ai.Enabled && !string.IsNullOrEmpty(Settings.Ai.ConsentVersion);
    public string ProviderOverridesPath => AppPaths.ProviderOverridesPath;

    public void ReloadAll()
    {
        Loaded = SettingsStore.Load();
        Rules = RuleEngine.Create(SettingsStore.BuiltinRulesPath, SettingsStore.UserRulesPath);
        Safety = SettingsStore.CreateSafetyContext(Settings);
        Executor = new DeleteExecutor(Safety);
        Enricher = AiPluginLoader.TryLoad(AiStorage, Settings, () => AiEnabled);
    }

    public void SaveSettings()
    {
        if (Loaded.ReadOnly)
            throw new InvalidOperationException("设置文件由更新版本创建，已拒绝写入，请升级 DiskSweep。");
        SettingsStore.Save(Settings);
        ReloadAll();
    }

    /// <summary>运行中关闭 AI：取消 in-flight 请求且不等待返回，扫描继续按纯规则路径收敛（6.5）。</summary>
    public void DisableAiImmediately()
    {
        if (Enricher is not null)
        {
            var method = Enricher.GetType().GetMethod("Disable",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            method?.Invoke(Enricher, null);
        }
        Enricher = null;
    }
}

public static class AppPaths
{
    public static string ProviderOverridesPath =>
        Path.Combine(DiskSweepDb.AppDataDirectory, "providers.json");
}
