using DiskSweep.Core;
using DiskSweep.Core.Recommendation;
using DiskSweep.Core.Safety;
using Xunit;

namespace DiskSweep.Tests;

/// <summary>10.3 闸门绕过专项：畸形路径必须被 Keep 或规范化后命中受保护路径。</summary>
public class SafetyGateTests
{
    private static Recommendation Rec(string path,
        RecommendationSource source = RecommendationSource.Rule,
        RecommendationAction action = RecommendationAction.Delete,
        RiskLevel risk = RiskLevel.Low,
        bool explicitConfirm = false) =>
        new(path, source, action, risk, 1.0, "测试", "temp-user", explicitConfirm);

    [Fact]
    public void Whitelist_HasAbsoluteVeto()
    {
        var dir = TestFixture.NewTempDirectory();
        var file = Path.Combine(dir, "keep.tmp");
        File.WriteAllText(file, "x");
        try
        {
            var ctx = SafetyContext.Create(new Whitelist(new[] { dir + @"\**" }));
            var gated = SafetyGate.Apply(Rec(file, risk: RiskLevel.Low), ctx);
            Assert.Equal(RecommendationAction.Keep, gated.Action);
            Assert.Contains("白名单", gated.Reason);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Critical_AlwaysBecomesReview()
    {
        var dir = TestFixture.NewTempDirectory();
        var file = Path.Combine(dir, "big.vhdx");
        File.WriteAllText(file, "x");
        try
        {
            var gated = SafetyGate.Apply(Rec(file, risk: RiskLevel.Critical), TestFixture.Context());
            Assert.Equal(RecommendationAction.Review, gated.Action);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void AiSource_AlwaysRequiresExplicitConfirm()
    {
        var dir = TestFixture.NewTempDirectory();
        var file = Path.Combine(dir, "a.iso");
        File.WriteAllText(file, "x");
        try
        {
            var high = SafetyGate.Apply(Rec(file, RecommendationSource.Ai), TestFixture.Context());
            Assert.True(high.RequiresExplicitConfirm);
            Assert.StartsWith("AI 建议：", high.Reason);
            Assert.False(SafetyGate.IsSilentCleanupAllowed(high));

            var low = new Recommendation(file, RecommendationSource.Ai,
                RecommendationAction.Delete, RiskLevel.Low, 0.4, "不确定", null);
            Assert.Equal(RecommendationAction.Review, SafetyGate.Apply(low, TestFixture.Context()).Action);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData(@"\\?\C:\Windows\System32\..\..\Temp\evil.tmp")]
    [InlineData(@"\\.\C:\Windows\System32\drivers\etc\hosts")]
    [InlineData(@"C:\WINDOWS\system32\notepad.exe")]
    [InlineData(@"C:\Windows ")]
    [InlineData(@"C:\Windows\.")]
    [InlineData(@"C:\Windows\System32\CON")]
    [InlineData(@"C:\Windows\System32\NUL.txt")]
    [InlineData(@"\\server\share\secret.txt")]
    [InlineData(@"C:\Users\OtherUser\Documents\private.docx")]
    [InlineData(@"C:\Program Files\Common Files\thing.dll")]
    public void MalformedOrProtectedPaths_AreNeverDeleted(string path)
    {
        var gated = SafetyGate.Apply(Rec(path), TestFixture.Context());
        Assert.Equal(RecommendationAction.Keep, gated.Action);
    }

    [Fact]
    public void ProtectedPaths_Owns_RejectsOtherUsers()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.True(ProtectedPaths.Owns(Path.Combine(profile, "Downloads", "a.iso"), profile));
        Assert.False(ProtectedPaths.Owns(@"C:\Users\SomeOtherUser\Downloads\a.iso", profile));
        Assert.False(ProtectedPaths.Owns(@"C:\Users\Public\Desktop\a.iso", profile));
    }

    [Fact]
    public void PathNormalizer_ExpandsDotDotAndDevicePrefix()
    {
        var path = PathNormalizer.Normalize(@"\\?\C:\Windows\System32\..\notepad.exe");
        Assert.NotNull(path);
        Assert.Contains("Windows", path, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"\\?\", path!);
        Assert.Null(PathNormalizer.Normalize(@"C:\Windows\System32\CON"));
        Assert.Null(PathNormalizer.Normalize(@"\\remote\share\x"));
    }

    [Fact]
    public void SystemOperationGate_ForcesReviewAndRejectsAi()
    {
        var ctx = TestFixture.Context();
        var rule = SafetyGate.ApplySystemOperation(
            Rec(@"C:\Windows\WinSxS", risk: RiskLevel.High), ctx);
        Assert.Equal(RecommendationAction.Review, rule.Action);
        Assert.True(rule.RequiresExplicitConfirm);
        Assert.True(rule.Risk >= RiskLevel.High);

        var ai = SafetyGate.ApplySystemOperation(
            Rec(@"C:\Windows\WinSxS", RecommendationSource.Ai), ctx);
        Assert.Equal(RecommendationAction.Keep, ai.Action);
    }

    [Fact]
    public void SystemOperations_AreNeverSilentlyAllowed()
    {
        var operations = RecommendationEngine.BuildSystemOperations(TestFixture.Rules(), TestFixture.Context());
        Assert.NotEmpty(operations);
        Assert.All(operations, o =>
        {
            Assert.True(o.IsSystemOperation);
            Assert.True(o.RequiresExplicitConfirm);
            Assert.False(o.IsSelected);
            Assert.Equal(RecommendationAction.Review, o.Action);
            Assert.False(SafetyGate.IsSilentCleanupAllowed(o.Recommendation));
        });
    }
}
