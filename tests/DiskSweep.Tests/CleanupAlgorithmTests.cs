using DiskSweep.Core;
using DiskSweep.Core.Executors;
using DiskSweep.Core.Recommendation;
using DiskSweep.Core.Rules;
using DiskSweep.Core.Scanning;
using DiskSweep.Core.Storage;
using Xunit;

namespace DiskSweep.Tests;

/// <summary>
/// 清理算法回归：目录范围边界、空目录嵌套折叠、分批删除、按体积保留明细。
/// 这些都是"用户看得见的错"——误删同前缀目录、父子项互相打架、一条失败拖垮整批。
/// </summary>
public class CleanupAlgorithmTests
{
    private static string TempDir() => TestFixture.NewTempDirectory();

    private sealed class SyncProgress<T> : IProgress<T>
    {
        public List<T> Values { get; } = new();
        public void Report(T value) => Values.Add(value);
    }

    // ── 目录范围边界 ────────────────────────────────────────────────────────────

    [Fact]
    public void DirectoryScopePattern_DoesNotLeakIntoSiblingWithSamePrefix()
    {
        var dir = TempDir();
        try
        {
            var temp = Path.Combine(dir, "Temp");
            var temperature = Path.Combine(dir, "Temperature");
            var matcher = MatcherFor(temp + "\\**");

            Assert.True(matcher.Matches(Path.Combine(temp, "a.tmp")));
            Assert.True(matcher.Matches(Path.Combine(temp, "sub", "a.tmp")));
            Assert.False(matcher.Matches(Path.Combine(temperature, "a.tmp")),
                "同前缀的兄弟目录不得被当作目录范围命中（会误删）");
            Assert.False(matcher.Matches(temp), "目录本身不算其内容，否则会整目录删除");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void UnanchoredDirectoryScope_StillMatchesNestedDirectory()
    {
        var matcher = MatcherFor(@"**\.git\**");
        Assert.True(matcher.Matches(@"C:\repo\.git\config"));
        Assert.False(matcher.Matches(@"C:\repo\.gitignore"));
        Assert.False(matcher.Matches(@"C:\repo\.git"));
    }

    private static GlobMatcher MatcherFor(string pattern)
    {
        Assert.True(GlobMatcher.TryCreate(pattern, out var matcher, out var error), error);
        return matcher!;
    }

    // ── 空目录嵌套折叠 ──────────────────────────────────────────────────────────

    [Fact]
    public void EmptyDirectoryChain_CollapsesToOutermost()
    {
        var root = @"C:\scan";
        var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\scan\a", @"C:\scan\a\b", @"C:\scan\a\b\c", @"C:\scan\keep"
        };
        var nonEmpty = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\scan\keep" };

        var items = ScanService.CollectEmptyItems(all, nonEmpty, new List<CandidateItem>(), root);

        var dir = Assert.Single(items);
        Assert.Equal(@"C:\scan\a", dir.Path);
        Assert.True(dir.IsDirectory);
    }

    [Fact]
    public void EmptyFileUnderEmptyDirectory_IsFoldedIntoDirectory()
    {
        var root = @"C:\scan";
        var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\scan\a", @"C:\scan\b" };
        var nonEmpty = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\scan\b" };
        var files = new List<CandidateItem>
        {
            new(@"C:\scan\a\zero.txt", "空文件", 0, 0, 0, ".txt", FileAttributes.Normal, 0, 0, false),
            new(@"C:\scan\b\zero.txt", "空文件", 0, 0, 0, ".txt", FileAttributes.Normal, 0, 0, false)
        };

        var items = ScanService.CollectEmptyItems(all, nonEmpty, files, root);

        Assert.Equal(2, items.Count);
        Assert.Contains(items, i => i.Path == @"C:\scan\a");                  // 空目录
        Assert.Contains(items, i => i.Path == @"C:\scan\b\zero.txt");          // 非空目录里的零字节文件
        Assert.DoesNotContain(items, i => i.Path == @"C:\scan\a\zero.txt");    // 已被目录覆盖
    }

    [Fact]
    public void CancelledScan_ReportsNoEmptyDirectories()
    {
        var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\scan\a" };
        var items = ScanService.CollectEmptyItems(all, new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new List<CandidateItem>(), @"C:\scan", complete: false);
        Assert.Empty(items);
    }

    // ── 分批删除 ────────────────────────────────────────────────────────────────

    private static DiskSweepDb NewDb(string dir)
    {
        var db = new DiskSweepDb(Path.Combine(dir, "test.db"));
        db.Initialize();
        return db;
    }

    [Fact]
    public void DeleteBatch_IsChunkedAndContinuesAfterFailedChunk()
    {
        var dir = TempDir();
        try
        {
            var db = NewDb(dir);
            var files = new List<PlanItem>();
            for (int i = 0; i < 130; i++)
            {
                var p = Path.Combine(dir, "f" + i + ".tmp");
                File.WriteAllText(p, "x");
                files.Add(TestFixture.Plan(p));
            }

            var sizes = new List<int>();
            int batch = 0;
            var executor = new DeleteExecutor(TestFixture.Context(),
                (root, bytes) => new RecycleBinQuota(true, 1L << 40, 0, "ok", false),
                (paths, allowUndo) =>
                {
                    sizes.Add(paths.Count);
                    batch++;
                    // 第一批发火（模拟被占用/权限问题），其余照常删除
                    if (batch == 1) return (0x4C7, false);
                    foreach (var p in paths) if (File.Exists(p)) File.Delete(p);
                    return (0, false);
                });

            var progress = new SyncProgress<DeleteProgress>();
            var outcome = executor.Execute(files, null, DeleteOptions.Recycle(dir), progress);

            Assert.Equal(new[] { 64, 64, 2 }, sizes);          // 130 项按 64 分批
            Assert.True(outcome.Succeeded > 0, "某一批失败不应终止整批");
            Assert.Equal(files.Count, outcome.Succeeded + outcome.Failed);
            Assert.Contains("未去掉 FOF_ALLOWUNDO", outcome.Message);
            Assert.Contains("实际释放", outcome.Message);
            Assert.Equal(130, progress.Values[^1].Total);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void NestedTargets_AreFoldedToOutermostParent()
    {
        var dir = TempDir();
        try
        {
            var db = NewDb(dir);
            var parent = Path.Combine(dir, "empty");
            var child = Path.Combine(parent, "inner");
            Directory.CreateDirectory(child);
            File.WriteAllText(Path.Combine(child, "zero.txt"), "");

            var sent = new List<string>();
            var executor = new DeleteExecutor(TestFixture.Context(),
                (root, bytes) => new RecycleBinQuota(true, 1L << 40, 0, "ok", false),
                (paths, allowUndo) =>
                {
                    sent.AddRange(paths);
                    foreach (var p in paths) if (Directory.Exists(p)) Directory.Delete(p, true);
                    return (0, false);
                });

            var items = new[]
            {
                TestFixture.Plan(parent),
                TestFixture.Plan(child),
                TestFixture.Plan(Path.Combine(child, "zero.txt"), size: 0)
            };
            var outcome = executor.Execute(items, null, DeleteOptions.Recycle(dir));

            Assert.Single(sent);                     // 只把最外层交给 Shell
            Assert.Equal(parent, sent[0]);
            Assert.Equal(3, outcome.Succeeded);      // 子项一并视为成功（已随父删除）
            Assert.False(Directory.Exists(parent));
        }
        finally { Directory.Delete(dir, true); }
    }

    // ── 明细按体积保留 ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Scan_KeepsLargestCandidatesWhenDetailLimitIsSmall()
    {
        var dir = TempDir();
        try
        {
            // 12 个命中规则的文件，大小 1..12 KB；明细上限 3 时必须保留最大的三个
            var temp = Path.Combine(dir, "scan", "Temp");
            Directory.CreateDirectory(temp);
            for (int i = 1; i <= 12; i++)
                File.WriteAllBytes(Path.Combine(temp, "f" + i + ".tmp"), new byte[i * 1024]);

            var rulesPath = Path.Combine(dir, "rules.json");
            File.WriteAllText(rulesPath, $$"""
            {"schemaVersion":2,"packId":"t","packVersion":"1","rules":[
              {"id":"scan-temp","category":"临时文件","paths":["{{temp.Replace("\\", "\\\\")}}\\**"],
               "risk":"low","defaultSelected":true,"minAgeDays":0,"handling":"glob-delete"}
            ]}
            """);
            var rules = RuleEngine.Create(rulesPath);

            var scan = await ScanService.ScanAsync(Path.Combine(dir, "scan"), ScanMode.Walk, rules,
                maxDetailed: 3);

            Assert.Equal(3, scan.Items.Count);
            var kept = scan.Items.Select(i => i.AllocationSize).OrderByDescending(s => s).ToArray();
            Assert.Equal(new long[] { 12 * 1024, 11 * 1024, 10 * 1024 }, kept);
            Assert.Equal(9, scan.OmittedDetailCount);
        }
        finally { Directory.Delete(dir, true); }
    }
}
