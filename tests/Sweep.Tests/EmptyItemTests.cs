using Sweep.Core;
using Sweep.Core.Recommendation;
using Sweep.Core.Safety;
using Sweep.Core.Scanning;
using Xunit;

namespace Sweep.Tests;

/// <summary>空文件/空目录检测：只在扫描完整时给出空目录判断，且一律 Review + 默认不勾选。</summary>
public class EmptyItemTests
{
    private static CandidateItem EmptyFile(string path) =>
        new(path, "其他", 0, 0, 30, ".tmp", FileAttributes.Normal, 0, 0, false);

    [Fact]
    public void CollectEmptyItems_OnlyLeafDirectoriesWithoutChildren()
    {
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\data", @"C:\data\empty", @"C:\data\full"
        };
        var nonEmpty = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\data", @"C:\data\full"   // full 含子项；data 含子目录
        };
        var emptyFiles = new List<CandidateItem> { EmptyFile(@"C:\data\zero.tmp") };

        var result = ScanService.CollectEmptyItems(dirs, nonEmpty, emptyFiles, @"C:\data");

        var dir = Assert.Single(result, r => r.IsDirectory);
        Assert.Equal(@"C:\data\empty", dir.Path);
        Assert.Contains(result, r => !r.IsDirectory && r.Path == @"C:\data\zero.tmp");
        Assert.DoesNotContain(result, r => r.Path == @"C:\data\full");
        Assert.DoesNotContain(result, r => r.Path == @"C:\data");   // 扫描根不参与
    }

    [Fact]
    public void CollectEmptyItems_WhenIncomplete_SkipsEmptyDirectories()
    {
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\data\maybe" };
        var result = ScanService.CollectEmptyItems(dirs, new(), new(), @"C:\data", complete: false);
        Assert.Empty(result);
    }

    [Fact]
    public void EmptyItemDetector_AllAreReviewAndUnselected()
    {
        // 闸门会解析真实路径（不存在的路径规范化失败→Keep），因此建立真实空文件/空目录。
        var dir = TestFixture.NewTempDirectory();
        try
        {
            var file = Path.Combine(dir, "empty-a.tmp");
            File.WriteAllBytes(file, Array.Empty<byte>());
            var emptyDir = Path.Combine(dir, "empty-dir");
            Directory.CreateDirectory(emptyDir);

            var scan = TestFixture.Scan(new[] { EmptyFile(file) });
            var withEmpty = scan with { EmptyCandidates = new[]
            {
                EmptyFile(file),
                new CandidateItem(emptyDir, "空目录", 0, 0, 0,
                    "", FileAttributes.Directory, 0, 0, true)
            } };

            var plan = EmptyItemDetector.Build(withEmpty, TestFixture.Context());

            Assert.Equal(2, plan.Count);
            Assert.All(plan, p =>
            {
                Assert.Equal(RecommendationAction.Review, p.Action);
                Assert.Equal(RiskLevel.Low, p.Risk);
                Assert.False(p.IsSelected);
                Assert.False(p.DefaultSelected);
                Assert.False(SafetyGate.IsSilentCleanupAllowed(p.Recommendation));
                Assert.Equal(0, p.AllocationSize);
            });
            Assert.Contains(plan, p => p.Category == "空文件");
            Assert.Contains(plan, p => p.Category == "空目录");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void EmptyItemDetector_ProtectedPathsNeverEnterPlan()
    {
        // Windows 目录受保护；即便被当作空目录也不得进入清单
        var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var scan = TestFixture.Scan(Array.Empty<CandidateItem>()) with
        {
            EmptyCandidates = new[]
            {
                new CandidateItem(Path.Combine(winDir, "some-empty"), "空目录", 0, 0, 0,
                    "", FileAttributes.Directory, 0, 0, true)
            }
        };

        var plan = EmptyItemDetector.Build(scan, TestFixture.Context());
        Assert.Empty(plan);
    }
}
