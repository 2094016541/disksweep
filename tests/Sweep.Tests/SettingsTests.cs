using System.Text.Json.Nodes;
using Sweep.Core.Storage;
using Xunit;

namespace Sweep.Tests;

public class SettingsTests
{
    [Fact]
    public void Defaults_AreSafeAndOffline()
    {
        var s = SettingsStore.Defaults();
        Assert.Equal(2, s.Version);
        Assert.Equal("recycle", s.DeleteMode);
        Assert.False(s.Ai.Enabled);
        Assert.Null(s.Ai.ConsentVersion);
        Assert.True(s.Ai.PrivacyMode);
        Assert.False(s.Schedule.Enabled);
        Assert.False(s.TelemetryEnabled, "遥测默认关闭（8.2 已定）");
        Assert.Equal("weekly:Mon@03:00", s.Schedule.Trigger);
    }

    [Theory]
    [InlineData("0 3 * * 1", "weekly:Mon@03:00")]
    [InlineData("30 2 * * *", "daily@02:30")]
    [InlineData("0 4 * * 5", "weekly:Fri@04:00")]
    public void StructuredTrigger_MigratesFromV1Cron(string cron, string expected) =>
        Assert.Equal(expected, SettingsMigrator.MapCronToTrigger(cron));

    [Fact]
    public void V1Settings_MigrateToV2WithoutCron()
    {
        var node = JsonObject.Parse(
            "{\"version\":1,\"deleteMode\":\"permanent\",\"schedule\":{\"cron\":\"0 3 * * 1\"},\"ai\":{\"enabled\":true}}")
            as JsonObject;
        Assert.NotNull(node);
        var warnings = new List<string>();
        SettingsMigrator.Migrate(node, 1, warnings);
        Assert.Equal(2, node["version"]!.GetValue<int>());
        Assert.Equal("weekly:Mon@03:00", node["schedule"]!["trigger"]!.GetValue<string>());
        Assert.Null(node["schedule"]!["cron"]);
        Assert.NotEmpty(warnings);
    }

    [Fact]
    public void SettingsPath_IsPerUserLocalAppData()
    {
        Assert.Contains("Sweep", SettingsStore.SettingsPath);
        Assert.Contains("Sweep", SweepDb.DefaultPath);
        Assert.EndsWith("settings.json", SettingsStore.SettingsPath);
        Assert.EndsWith("sweep.db", SweepDb.DefaultPath);
    }

    [Fact]
    public void AppDataDirectory_PortableMarker_MovesDataIntoInstallDirectory()
    {
        var installDir = TestFixture.NewTempDirectory();
        var localAppData = Path.Combine(installDir, "LocalAppData");

        Assert.Equal(Path.Combine(localAppData, "Sweep"),
            SweepDb.ResolveAppDataDirectory(installDir, localAppData));

        File.WriteAllText(Path.Combine(installDir, "portable.mode"), "installed\n");

        Assert.Equal(Path.Combine(installDir, "data"),
            SweepDb.ResolveAppDataDirectory(installDir, localAppData));
    }

    [Fact]
    public void SecretStore_MasksKeyAndSurvivesMissingFile()
    {
        var dir = TestFixture.NewTempDirectory();
        var path = Path.Combine(dir, "secrets.dat");
        try
        {
            var store = new SecretStore(path);       // 文件不存在：按"未配置"处理，不抛异常
            Assert.Null(store.GetApiKey("deepseek"));
            Assert.Equal("（未配置）", SecretStore.Mask(null));
            Assert.Equal("****1234", SecretStore.Mask("sk-abcd1234"));

            store.SetApiKey("deepseek", "sk-abcd1234");
            var reopened = new SecretStore(path);    // DPAPI 加密落盘后可重新读取
            Assert.Equal("sk-abcd1234", reopened.GetApiKey("deepseek"));
            Assert.Equal(reopened.GetOrCreateSalt(), reopened.GetOrCreateSalt());
            Assert.True(reopened.GetOrCreateSalt().Length >= 32);
        }
        finally { Directory.Delete(dir, true); }
    }
}
