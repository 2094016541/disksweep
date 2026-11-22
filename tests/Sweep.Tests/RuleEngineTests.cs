using Sweep.Core;
using Sweep.Core.Rules;
using Sweep.Core.Safety;
using Xunit;

namespace Sweep.Tests;

public class RuleEngineTests
{
    private static string Temp(string name) => Path.Combine(Path.GetTempPath(), name);

    [Fact]
    public void TempRule_MatchesAndIsLowRisk()
    {
        var match = TestFixture.Rules().Match(Temp("cache.tmp"), 30);
        Assert.NotNull(match);
        Assert.Equal(RecommendationAction.Delete, match.Action);
        Assert.Equal(RiskLevel.Low, match.Risk);
        Assert.True(match.DefaultSelected);
        Assert.Equal("temp-user", match.RuleId);
    }

    [Fact]
    public void MinAgeDays_KeepsFreshFiles()
    {
        var fresh = TestFixture.Rules().Match(Temp("fresh.tmp"), 0);
        Assert.NotNull(fresh);
        Assert.Equal(RecommendationAction.Keep, fresh.Action);
        Assert.False(fresh.DefaultSelected);
    }

    [Theory]
    [InlineData("mail.pst")]
    [InlineData("mail.ost")]
    [InlineData("data.sqlite")]
    [InlineData("vm.vhdx")]
    public void GoldenSamples_AreNeverAutoDeleted(string name)
    {
        var match = TestFixture.Rules().Match(Temp(name), 400);
        Assert.NotNull(match);
        Assert.NotEqual(RecommendationAction.Delete, match.Action);
        Assert.False(match.DefaultSelected);
    }

    [Fact]
    public void DevelopmentDirectories_AreProtected()
    {
        var rules = TestFixture.Rules();
        foreach (var path in new[]
                 {
                     @"C:\work\repo\.git\objects\ab",
                     @"C:\work\repo\node_modules\lodash\index.js",
                     @"C:\work\rust\target\debug\app.exe",
                     @"C:\work\py\.venv\Lib\site-packages\a.py"
                 })
        {
            var match = rules.Match(path, 400);
            Assert.NotNull(match);
            Assert.Equal(RecommendationAction.Keep, match.Action);
        }
    }

    [Fact]
    public void ConflictingRules_TakeConservativeConclusion()
    {
        // 同时命中 temp-user(delete/low) 与 protect-mail(keep/critical)
        var match = TestFixture.Rules().Match(Temp("backup.pst"), 400);
        Assert.NotNull(match);
        Assert.Equal(RecommendationAction.Keep, match.Action);
        Assert.Equal(RiskLevel.Critical, match.Risk);
        Assert.False(match.DefaultSelected);
    }

    [Fact]
    public void UserOverrides_WinAndCanVeto()
    {
        var dir = TestFixture.NewTempDirectory();
        var overrides = Path.Combine(dir, "user-overrides.json");
        try
        {
            var rule = new RuleDefinition
            {
                Id = "temp-user",
                Category = "用户规则",
                Paths = new List<string> { "%TEMP%\\**" },
                Action = "keep",
                Risk = "critical",
                DefaultSelected = false,
                Description = "用户手动保护临时目录"
            };
            RulePackLoader.SaveUserOverrides(overrides, new[] { rule });
            var match = TestFixture.Rules(overrides).Match(Temp("x.tmp"), 30);
            Assert.NotNull(match);
            Assert.Equal(RecommendationAction.Keep, match.Action);
            Assert.Equal("用户规则", match.Category);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void InvalidGlob_IsRejected()
    {
        Assert.False(GlobMatcher.TryCreate(@"C:\***\x", out _, out var error));
        Assert.NotNull(error);
        var validation = Whitelist.Validate(new[] { @"C:\projects\**", @"%UNCLOSED\x" });
        Assert.False(validation.Ok);
        Assert.NotNull(validation.Error);
    }

    [Fact]
    public void BuiltinRulePack_HasHighRiskDefaultsUnselected()
    {
        var rules = TestFixture.Rules();
        Assert.NotEmpty(rules.Rules);
        Assert.All(rules.Rules.Where(r => r.RiskLevel >= RiskLevel.High),
            r => Assert.False(r.DefaultSelected));
        Assert.Contains(rules.SystemOperations, r => r.Handling == "dism-startcomponentcleanup");
    }
}
