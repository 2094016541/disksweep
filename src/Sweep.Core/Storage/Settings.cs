using System.Text.Json;
using System.Text.Json.Nodes;
using Sweep.Core.Safety;

namespace Sweep.Core.Storage;

public sealed class ScheduleSettings
{
    public bool Enabled { get; set; }
    public string Trigger { get; set; } = "weekly:Mon@03:00";   // 结构化触发器，不使用 cron
    public int MinFreePercent { get; set; } = 15;
}

public sealed class AiSettings
{
    public bool Enabled { get; set; }
    public string? ProviderId { get; set; }
    public double MonthlyBudgetUsd { get; set; } = 0.5;
    public bool PrivacyMode { get; set; } = true;
    public string? ConsentVersion { get; set; }
    public int MaxCandidatesPerScan { get; set; } = 200;
    public int MaxRequestsPerMonth { get; set; } = 500;
}

public sealed class AppSettings
{
    public int Version { get; set; } = 2;
    public string Language { get; set; } = "zh-CN";
    public string Theme { get; set; } = "system";
    public string DeleteMode { get; set; } = "recycle";
    public List<string> Whitelist { get; set; } = new();
    public List<string> ExcludeExtensions { get; set; } = new();
    public ScheduleSettings Schedule { get; set; } = new();
    public AiSettings Ai { get; set; } = new();
    public bool TelemetryEnabled { get; set; }      // 默认关闭（8.2 已定）
    public bool TrayEnabled { get; set; }
    public long LargeFileThresholdBytes { get; set; } = 500L * 1024 * 1024;
}

public sealed record LoadedSettings(AppSettings Settings, IReadOnlyList<string> Warnings, bool ReadOnly);

public sealed class UnsupportedSettingsVersionException : Exception
{
    public UnsupportedSettingsVersionException(string message) : base(message) { }
}

/// <summary>文档 5.2：settings.json 不含密钥；deleteMode 持久化值恒为 recycle。</summary>
public static class SettingsStore
{
    public const int CurrentVersion = 2;

    public static string AppDirectory => SweepDb.AppDataDirectory;
    public static string SettingsPath => Path.Combine(AppDirectory, "settings.json");
    public static string UserRulesPath => Path.Combine(AppDirectory, "rules", "user-overrides.json");
    public static string BuiltinRulesPath => Path.Combine(AppContext.BaseDirectory, "rules", "builtin-win.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static AppSettings Defaults()
    {
        return new AppSettings
        {
            Version = CurrentVersion,
            DeleteMode = "recycle",
            Whitelist = new List<string> { "D:\\projects\\**", "%USERPROFILE%\\Documents\\**" },
            ExcludeExtensions = new List<string> { ".git", ".sqlite", ".pst" },
            Schedule = new ScheduleSettings(),
            Ai = new AiSettings(),
            TelemetryEnabled = false
        };
    }

    public static LoadedSettings Load()
    {
        var warnings = new List<string>();
        if (!File.Exists(SettingsPath)) return new LoadedSettings(Defaults(), warnings, false);

        try
        {
            var node = JsonNode.Parse(File.ReadAllText(SettingsPath)) as JsonObject;
            if (node is null) return new LoadedSettings(Defaults(), warnings, false);
            int version = node["version"]?.GetValue<int>() ?? 1;
            if (version > CurrentVersion)
                return new LoadedSettings(Defaults(),
                    new[] { $"设置文件版本 v{version} 高于当前程序支持的 v{CurrentVersion}，已以只读方式打开，请升级后再保存。" }, true);

            if (version < CurrentVersion)
            {
                SettingsMigrator.Migrate(node, version, warnings);
                var migrated = node.Deserialize<AppSettings>(Options) ?? Defaults();
                var merged = MergeDefaults(migrated);
                Save(merged);
                return new LoadedSettings(merged, warnings, false);
            }

            var settings = node.Deserialize<AppSettings>(Options) ?? Defaults();
            return new LoadedSettings(MergeDefaults(settings), warnings, false);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            warnings.Add("设置文件读取失败，已使用默认值：" + ex.Message);
            return new LoadedSettings(Defaults(), warnings, false);
        }
    }

    private static AppSettings MergeDefaults(AppSettings s)
    {
        var d = Defaults();
        s.Whitelist ??= d.Whitelist;
        s.ExcludeExtensions ??= d.ExcludeExtensions;
        s.Schedule ??= d.Schedule;
        s.Ai ??= d.Ai;
        s.Version = CurrentVersion;
        s.DeleteMode = "recycle";       // 永久删除绝不回写（4.5）
        return s;
    }

    public static void Save(AppSettings settings)
    {
        if (settings.Version > CurrentVersion) throw new UnsupportedSettingsVersionException("拒绝覆盖更高版本的设置文件");
        var validation = Whitelist.Validate(settings.Whitelist);
        if (!validation.Ok) throw new ArgumentException(validation.Error ?? "白名单包含非法模式");

        settings.Version = CurrentVersion;
        settings.DeleteMode = "recycle";
        Directory.CreateDirectory(AppDirectory);
        var temp = SettingsPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, Options));
        File.Move(temp, SettingsPath, true);
    }

    public static SafetyContext CreateSafetyContext(AppSettings settings)
    {
        return SafetyContext.Create(new Whitelist(settings.Whitelist, settings.ExcludeExtensions),
            settings.ExcludeExtensions);
    }
}

public static class SettingsMigrator
{
    /// <summary>v1 → v2：schedule.cron → 结构化 trigger；ai 补齐同意版本字段。</summary>
    public static void Migrate(JsonObject node, int fromVersion, IList<string> warnings)
    {
        if (fromVersion < 2)
        {
            if (node["schedule"] is JsonObject schedule && schedule["cron"] is { } cron)
            {
                var mapped = MapCronToTrigger(cron.GetValue<string>());
                schedule.Remove("cron");
                schedule["trigger"] = mapped;
                schedule["minFreePercent"] ??= 15;
                warnings.Add($"已将 v1 的 cron 触发器迁移为结构化触发器：{mapped}");
            }
            if (node["ai"] is JsonObject ai && ai["consentVersion"] is null)
                ai["consentVersion"] = null;
            node["version"] = 2;
        }
    }

    public static string MapCronToTrigger(string cron)
    {
        var parts = cron.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 5) return "weekly:Mon@03:00";
        var minute = parts[0] == "0" ? "00" : parts[0].PadLeft(2, '0');
        var hour = parts[1].PadLeft(2, '0');
        var days = new[] { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };
        if (parts[4] != "*" && int.TryParse(parts[4], out int dow) && dow >= 0 && dow <= 6)
            return $"weekly:{days[dow]}@{hour}:{minute}";
        return $"daily@{hour}:{minute}";
    }
}
