using Sweep.Core;
using Sweep.Core.Rules;
using Sweep.Core.Scanning;
using Xunit;

namespace Sweep.Tests;

/// <summary>
/// 扫描完整性说明的契约：内容没统计到时必须说出来，且要区分
/// 「换了扫描方式（Degraded）」和「这块内容根本没扫（SkippedRoots）」——
/// 前者不影响结果完整性，后者会让用户把「共 X GB」当成全部。
/// </summary>
public class ScanCompletenessTests
{
    private static ScanResult Build(ScanStats stats, IReadOnlyList<string>? skippedRoots = null) =>
        new(new ScanSnapshot("s", "C:\\", DateTime.UtcNow, DateTime.UtcNow, ScanMode.Walk, 0, 0, 0),
            Array.Empty<CandidateItem>(),
            new Dictionary<string, RuleMatch>(),
            Array.Empty<CategorySummary>(),
            Array.Empty<DuplicateGroup>(),
            stats,
            false, null, 0,
            EmptyCandidates: null,
            DuplicatesTruncated: false,
            SkippedRoots: skippedRoots);

    [Fact]
    public void CleanScan_ReportsNoGaps()
    {
        var result = Build(new ScanStats { TotalEntries = 1000, TotalBytes = 5_000_000 });

        Assert.False(ScanCompleteness.HasGaps(result));
        Assert.Empty(ScanCompleteness.Describe(result));
        Assert.Null(ScanCompleteness.Summarize(result));
    }

    [Fact]
    public void DeniedDirectories_AreReportedWithSamplesAndAdvice()
    {
        var stats = new ScanStats { Denied = 3 };
        stats.DeniedPaths.Add(@"C:\System Volume Information");
        stats.DeniedPaths.Add(@"C:\Users\other\AppData");

        var gaps = ScanCompleteness.Describe(Build(stats));

        Assert.True(ScanCompleteness.HasGaps(Build(stats)));
        // 必须给出具体目录，只报数字等于没说
        Assert.Contains(gaps, g => g.Contains("3 个目录无访问权限"));
        Assert.Contains(gaps, g => g.Contains(@"C:\System Volume Information"));
        // 必须告诉用户怎么补救
        Assert.Contains(gaps, g => g.Contains("管理员"));
    }

    [Fact]
    public void DeniedDirectories_AreClassifiedIntoSystemProtectedAndOther()
    {
        // 系统保护目录与一般 ACL 受限目录要分开说清楚：前者不该删，后者可试管理员。
        var stats = new ScanStats();
        stats.MergeDenied(1, @"C:\System Volume Information");
        stats.MergeDenied(1, @"C:\Program Files\WindowsApps");
        stats.MergeDenied(1, @"C:\ProgramData\SomeVendor\Private");

        Assert.Equal(3, stats.Denied);
        Assert.Equal(2, stats.DeniedSystemProtected);
        Assert.Equal(1, stats.DeniedOther);

        var gaps = ScanCompleteness.Describe(Build(stats));
        Assert.Contains(gaps, g => g.Contains("2 个是系统保护目录") && g.Contains("本就不应删除"));
        Assert.Contains(gaps, g => g.Contains("其余 1 个目录受 ACL 限制") && g.Contains("管理员"));
    }

    [Fact]
    public void UnresolvableAndUnverifiedItems_AreReportedSeparately()
    {
        var stats = new ScanStats { UnresolvablePaths = 12, AttributeFailures = 7 };

        var gaps = ScanCompleteness.Describe(Build(stats));

        Assert.Contains(gaps, g => g.Contains("12") && g.Contains("路径"));
        Assert.Contains(gaps, g => g.Contains("7") && g.Contains("唯一身份"));
        // 路径无法重建的项不参与删除，必须说清楚（否则用户以为它只是没显示）
        Assert.Contains(gaps, g => g.Contains("不参与删除"));
    }

    [Fact]
    public void SkippedRoots_AreNotMixedIntoDegradeReason()
    {
        var stats = new ScanStats();
        var result = Build(stats, new[] { "D: 目录不可访问" });

        var gaps = ScanCompleteness.Describe(result);

        Assert.Contains(gaps, g => g.Contains("D:") && g.Contains("完全未统计"));
        // 整盘没扫属于"内容缺失"，不是"降级"，不应写在 DegradeReason 里
        Assert.Null(result.DegradeReason);
        Assert.True(result.Degraded == false);
    }

    [Fact]
    public void Summarize_IsShortEnoughForStatusBar()
    {
        var stats = new ScanStats { Denied = 3, UnresolvablePaths = 12, AttributeFailures = 7 };
        var summary = ScanCompleteness.Summarize(Build(stats, new[] { "D: 目录不可访问" }));

        Assert.NotNull(summary);
        Assert.Contains("1 个扫描目标不可访问", summary);
        Assert.Contains("3 个目录无权限", summary);
        Assert.Contains("12 项路径无法重建", summary);
        Assert.Contains("7 项身份未校验", summary);
        Assert.DoesNotContain("\n", summary);
    }

    [Fact]
    public void DeniedPathSamples_AreCappedToAvoidBlowingUpMemory()
    {
        var stats = new ScanStats();
        for (int i = 0; i < ScanStats.MaxDeniedSamples + 50; i++)
            stats.MergeDenied(1, @"C:\locked\" + i);

        Assert.Equal(ScanStats.MaxDeniedSamples, stats.DeniedPaths.Count);
        Assert.Equal(ScanStats.MaxDeniedSamples + 50, stats.Denied);
    }
}
