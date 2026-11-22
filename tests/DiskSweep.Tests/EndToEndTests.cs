using DiskSweep.Core;
using DiskSweep.Core.Executors;
using DiskSweep.Core.Recommendation;
using DiskSweep.Core.Scanning;
using DiskSweep.Core.Storage;
using Xunit;

namespace DiskSweep.Tests;

/// <summary>10.1 端到端：扫描 → 推荐 → 闸门 → 删除 → 落库。</summary>
public class EndToEndTests
{
    [Fact]
    public async Task ScanPlanDelete_FullLoop()
    {
        var dir = TestFixture.NewTempDirectory();       // 位于 %TEMP% 下，命中 temp-user 规则
        var files = new List<string>();
        for (int i = 0; i < 3; i++)
        {
            var f = Path.Combine(dir, $"old-{i}.tmp");
            File.WriteAllText(f, new string('x', 1024));
            File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddDays(-10));
            files.Add(f);
        }

        try
        {
            var rules = TestFixture.Rules();
            var scan = await ScanService.ScanAsync(dir, ScanMode.Walk, rules,
                CancellationToken.None, null, 1000, 500L * 1024 * 1024);

            Assert.True(scan.Snapshot.TotalFiles >= 3, "扫描应至少产出 3 个条目");
            Assert.False(scan.Degraded);
            Assert.Equal(ScanMode.Walk, scan.Snapshot.Mode);

            var ctx = TestFixture.Context();
            var plan = RecommendationEngine.BuildRulePlan(scan, rules, ctx);
            Assert.Contains(plan, p => p.Recommendation.RuleId == "temp-user");
            var deletable = plan.Where(p => p.Action == RecommendationAction.Delete && p.IsSelected).ToList();
            Assert.NotEmpty(deletable);
            Assert.All(deletable, p => Assert.False(p.RequiresExplicitConfirm));

            var db = new DiskSweepDb(Path.Combine(dir, "e2e.db"));
            db.Initialize();
            var executor = new DeleteExecutor(ctx,
                (root, bytes) => new RecycleBinQuota(true, 1L << 40, 0, "ok", false),
                (paths, allowUndo) =>
                {
                    foreach (var p in paths) if (File.Exists(p)) File.Delete(p);
                    return (0, false);
                });

            var outcome = executor.Execute(deletable, scan.Snapshot.SnapshotId, DeleteOptions.Recycle(dir));
            Assert.True(outcome.Committed);
            Assert.True(outcome.Succeeded > 0);
            Assert.All(deletable, p => Assert.False(File.Exists(p.Path)));

            db.InsertScan(scan.Snapshot);
            db.InsertItemsBatched(scan.Snapshot.SnapshotId, scan.Items);
            db.InsertRecommendations(scan.Snapshot.SnapshotId, plan);
            Assert.Single(db.GetRecentScans(1));

        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* 数据库文件可能仍被 SQLite 占用 */ }
        }
    }

    /// <summary>「所有磁盘」聚合项走多根扫描：坏盘不能拖垮好盘，进度必须跨根累计。</summary>
    [Fact]
    public async Task ScanAllAsync_MergesEveryRootAndKeepsCumulativeProgress()
    {
        var a = TestFixture.NewTempDirectory();
        var b = TestFixture.NewTempDirectory();
        try
        {
            // 与既有端到端用例一致：临时目录下的过期 .tmp 才会命中 temp-user 规则成为明细
            var fileA = Path.Combine(a, "old-a.tmp");
            var fileB = Path.Combine(b, "old-b.tmp");
            File.WriteAllText(fileA, new string('x', 1024));
            File.WriteAllText(fileB, new string('y', 2048));
            File.SetLastWriteTimeUtc(fileA, DateTime.UtcNow.AddDays(-10));
            File.SetLastWriteTimeUtc(fileB, DateTime.UtcNow.AddDays(-10));
            string missing = Path.Combine(a, "no-such-dir");

            var progress = new SyncProgress();
            var scan = await ScanService.ScanAllAsync(
                new[] { missing, a, b }, ScanMode.Walk, TestFixture.Rules(),
                CancellationToken.None, progress, 1000);

            Assert.Contains(scan.Items, i => i.Path.EndsWith("old-a.tmp", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(scan.Items, i => i.Path.EndsWith("old-b.tmp", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(a, scan.Snapshot.RootPath);
            Assert.Contains(b, scan.Snapshot.RootPath);
            Assert.True(scan.Stats.TotalEntries >= 2, "两个根目录的条目都应计入");
            Assert.NotEmpty(scan.Categories);
            Assert.NotNull(scan.Snapshot.FinishedUtc);
            // 根不可访问属于「内容缺失」（SkippedRoots），不是「换了扫描方式」（Degraded）：
            // Degraded 在界面上会渲染成「MFT 已降级」，把"目录不存在"混进去会误导用户
            // 以为只是换了扫描模式，实际是整块目标没统计。
            Assert.False(scan.Degraded, "根不可访问不应标记为扫描降级");
            Assert.NotNull(scan.SkippedRoots);
            Assert.Contains(scan.SkippedRoots!, r => r.Contains("no-such-dir"));
            Assert.True(ScanCompleteness.HasGaps(scan), "跳过的根必须出现在完整性缺口里");
            Assert.NotEmpty(progress.Reports);

            long last = 0;
            foreach (var report in progress.Reports)
            {
                Assert.True(report.Entries >= last, "换盘时进度不能回跳到 0");
                last = report.Entries;
            }
        }
        finally
        {
            Directory.Delete(a, true);
            Directory.Delete(b, true);
        }
    }

    private sealed class SyncProgress : IProgress<ScanProgress>
    {
        public List<ScanProgress> Reports { get; } = new();

        public void Report(ScanProgress value)
        {
            lock (Reports) Reports.Add(value);
        }
    }

    [Fact]
    public async Task Cancellation_KeepsPartialSnapshot()
    {
        var dir = TestFixture.NewTempDirectory();
        for (int i = 0; i < 20; i++) File.WriteAllText(Path.Combine(dir, $"f{i}.bin"), new string('a', 512));
        try
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            var scan = await ScanService.ScanAsync(dir, ScanMode.Walk, TestFixture.Rules(),
                cts.Token, null, 1000);
            Assert.NotNull(scan.Snapshot);
            Assert.Null(scan.Snapshot.FinishedUtc);      // 取消后快照标记为未完成但可用
        }
        finally { Directory.Delete(dir, true); }
    }
}
