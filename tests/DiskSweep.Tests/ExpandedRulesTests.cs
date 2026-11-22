using DiskSweep.Core;
using DiskSweep.Core.Rules;
using Xunit;

namespace DiskSweep.Tests;

/// <summary>新增缓存规则：命中正确、低风险、默认勾选，且保护规则仍压过缓存规则。</summary>
public class ExpandedRulesTests
{
    [Theory]
    [InlineData("chrome-code-cache", @"%LOCALAPPDATA%\Google\Chrome\User Data\Default\Code Cache\js\abc")]
    [InlineData("edge-code-cache", @"%LOCALAPPDATA%\Microsoft\Edge\User Data\Default\Code Cache\a\data")]
    [InlineData("firefox-cache", @"%LOCALAPPDATA%\Mozilla\Firefox\Profiles\abc.default\cache2\entries\x")]
    [InlineData("npm-cache", @"%LOCALAPPDATA%\npm-cache\_cacache\content-v2\x")]
    [InlineData("pip-cache", @"%LOCALAPPDATA%\pip\Cache\wheels\x.whl")]
    [InlineData("inetcache", @"%LOCALAPPDATA%\Microsoft\Windows\INetCache\IE\ABC\x.dat")]
    [InlineData("teams-cache", @"%APPDATA%\Microsoft\Teams\Cache\data_1")]
    [InlineData("slack-cache", @"%APPDATA%\Slack\Cache\Cache_Data\x")]
    [InlineData("discord-cache", @"%APPDATA%\discord\Code Cache\js\x")]
    [InlineData("vscode-cache", @"%APPDATA%\Code\CachedData\abc\index")]
    public void NewCacheRules_MatchLowRiskAndDefaultSelected(string ruleId, string pattern)
    {
        var path = Environment.ExpandEnvironmentVariables(pattern);
        var match = TestFixture.Rules().Match(path, 30);
        Assert.NotNull(match);
        Assert.Equal(ruleId, match.RuleId);
        Assert.Equal(RecommendationAction.Delete, match.Action);
        Assert.Equal(RiskLevel.Low, match.Risk);
        Assert.True(match.DefaultSelected);
    }

    [Theory]
    [InlineData("yarn-cache", @"%LOCALAPPDATA%\Yarn\Cache\v6\x.zip")]
    [InlineData("pnpm-cache", @"%LOCALAPPDATA%\pnpm-cache\files\x")]
    [InlineData("nuget-cache", @"%LOCALAPPDATA%\NuGet\v3-cache\x.dat")]
    [InlineData("gradle-cache", @"%USERPROFILE%\.gradle\caches\modules-2\files-2.1\x.jar")]
    public void DevCaches_AreMediumAndNotSelectedByDefault(string ruleId, string pattern)
    {
        var path = Environment.ExpandEnvironmentVariables(pattern);
        var match = TestFixture.Rules().Match(path, 30);
        Assert.NotNull(match);
        Assert.Equal(ruleId, match.RuleId);
        Assert.Equal(RecommendationAction.Delete, match.Action);
        Assert.Equal(RiskLevel.Medium, match.Risk);
        Assert.False(match.DefaultSelected);   // 删了要重新下载，必须由用户主动勾选
    }

    [Fact]
    public void CacheRules_DoNotCoverSiblingDirectoryWithSamePrefix()
    {
        // INetCache 与 INetCacheFoo 是兄弟目录：目录范围规则不能越界命中（否则会误删）
        var path = Environment.ExpandEnvironmentVariables(
            @"%LOCALAPPDATA%\Microsoft\Windows\INetCacheOther\secret.txt");
        Assert.Null(TestFixture.Rules().Match(path, 30));
    }

    [Fact]
    public void CacheRules_StillYieldToProtectionRules()
    {
        // 落在缓存路径下但命中数据库保护规则：必须 Keep + Critical
        var path = Environment.ExpandEnvironmentVariables(
            @"%LOCALAPPDATA%\Google\Chrome\User Data\Default\Code Cache\data.sqlite");
        var match = TestFixture.Rules().Match(path, 400);
        Assert.NotNull(match);
        Assert.Equal(RecommendationAction.Keep, match.Action);
        Assert.Equal(RiskLevel.Critical, match.Risk);
        Assert.False(match.DefaultSelected);
    }

    [Fact]
    public void BuiltinPack_AllHighRiskStillUnselected()
    {
        var rules = TestFixture.Rules();
        Assert.All(rules.Rules.Where(r => r.RiskLevel >= RiskLevel.High),
            r => Assert.False(r.DefaultSelected));
    }
}
