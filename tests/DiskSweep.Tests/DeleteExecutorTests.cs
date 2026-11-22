using DiskSweep.Core;
using DiskSweep.Core.Executors;
using DiskSweep.Core.Recommendation;
using DiskSweep.Core.Storage;
using Xunit;

namespace DiskSweep.Tests;

/// <summary>8.1 红线 1/4/6、10.2 回收站配额、10.1 崩溃恢复。</summary>
public class DeleteExecutorTests
{
    private static (DiskSweepDb Db, string Dir, string File) Fixture()
    {
        var dir = TestFixture.NewTempDirectory();
        var file = Path.Combine(dir, "candidate.tmp");
        File.WriteAllText(file, "fixture");
        var db = new DiskSweepDb(Path.Combine(dir, "test.db"));
        db.Initialize();
        return (db, dir, file);
    }

    private static RecycleBinQuota QuotaOk(string root, long bytes) =>
        new(true, 1L << 40, 0, "ok", false);

    [Fact]
    public void QuotaExhaustion_AbortsWholeBatch_ZeroDeletes()
    {
        var (db, dir, file) = Fixture();
        int calls = 0;
        var executor = new DeleteExecutor(TestFixture.Context(),
            (root, bytes) => new RecycleBinQuota(false, 0, 0, "回收站配额不足", false),
            (paths, allowUndo) => { calls++; return (0, false); });

        var outcome = executor.Execute(new[] { TestFixture.Plan(file) }, null, DeleteOptions.Recycle(dir));

        Assert.False(outcome.Committed);
        Assert.Equal(0, outcome.Succeeded);
        Assert.Equal(0, calls);
        Assert.True(File.Exists(file), "配额不足时不得删除任何文件");
        Directory.Delete(dir, true);
    }

    [Fact]
    public void NukeOnDeleteDetection_AlsoAborts()
    {
        var (db, dir, file) = Fixture();
        var executor = new DeleteExecutor(TestFixture.Context(),
            (root, bytes) => new RecycleBinQuota(false, 0, 0, "系统设为不移入回收站", true),
            (paths, allowUndo) => (0, false));
        var outcome = executor.Execute(new[] { TestFixture.Plan(file) }, null, DeleteOptions.Recycle(dir));
        Assert.False(outcome.Committed);
        Assert.True(File.Exists(file));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void PermanentDelete_RequiresExactConfirmWord()
    {
        var (db, dir, file) = Fixture();
        var executor = new DeleteExecutor(TestFixture.Context(), QuotaOk,
            (paths, allowUndo) => (0, false));

        var noWord = executor.Execute(new[] { TestFixture.Plan(file) }, null,
            new DeleteOptions(true, null, true, dir));
        var wrongWord = executor.Execute(new[] { TestFixture.Plan(file) }, null,
            new DeleteOptions(true, "删除", true, dir));

        Assert.False(noWord.Committed);
        Assert.False(wrongWord.Committed);
        Assert.True(File.Exists(file));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void PermanentDelete_RejectsAiSuggestions()
    {
        var (db, dir, file) = Fixture();
        var ai = TestFixture.Plan(file, source: RecommendationSource.Ai, explicitConfirm: true);
        var executor = new DeleteExecutor(TestFixture.Context(), QuotaOk,
            (paths, allowUndo) => (0, false));
        var outcome = executor.Execute(new[] { ai }, null,
            new DeleteOptions(true, DeleteExecutor.PermanentConfirmWord, true, dir));
        Assert.False(outcome.Committed);
        Assert.True(File.Exists(file));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void RecycleDelete_KeepsAllowUndoAndReportsSucceededPaths()
    {
        var (db, dir, file) = Fixture();
        bool allowUndoObserved = false;
        var executor = new DeleteExecutor(TestFixture.Context(), QuotaOk,
            (paths, allowUndo) =>
            {
                allowUndoObserved = allowUndo;
                foreach (var p in paths) if (File.Exists(p)) File.Delete(p);
                return (0, false);
            });

        var outcome = executor.Execute(new[] { TestFixture.Plan(file) }, null, DeleteOptions.Recycle(dir));

        Assert.True(outcome.Committed);
        Assert.Equal(1, outcome.Succeeded);
        Assert.NotNull(outcome.SucceededPaths);
        Assert.Contains(file, outcome.SucceededPaths!);   // UI 据此精确移除已删项
        Assert.True(allowUndoObserved, "回收站模式必须带 FOF_ALLOWUNDO");
        Assert.False(File.Exists(file));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void BatchFailure_NeverRetriesWithoutAllowUndo()
    {
        var (db, dir, file) = Fixture();
        var observed = new List<bool>();
        var executor = new DeleteExecutor(TestFixture.Context(), QuotaOk,
            (paths, allowUndo) => { observed.Add(allowUndo); return (0x4C7, true); });

        var outcome = executor.Execute(new[] { TestFixture.Plan(file) }, null, DeleteOptions.Recycle(dir));

        Assert.Single(observed);
        Assert.True(observed[0]);
        Assert.Contains("未去掉 FOF_ALLOWUNDO", outcome.Message);
        Assert.True(File.Exists(file));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void SafetyGateVeto_PreventsExecution()
    {
        var (db, dir, _) = Fixture();
        var protectedPath = @"C:\Windows\System32\drivers\etc\hosts";
        var executor = new DeleteExecutor(TestFixture.Context(), QuotaOk,
            (paths, allowUndo) => { Assert.Fail("受保护路径不得进入执行器"); return (0, false); });
        var outcome = executor.Execute(new[] { TestFixture.Plan(protectedPath) }, null, DeleteOptions.Recycle(dir));
        Assert.False(outcome.Committed);
        Assert.Contains(outcome.Failures, f => f.Reason.Contains("闸门") || f.Reason.Contains("规范化"));
        Directory.Delete(dir, true);
    }
}
