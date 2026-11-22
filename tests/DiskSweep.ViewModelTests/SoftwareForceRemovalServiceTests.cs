using System.IO;
using DiskSweep.App.Services;
using DiskSweep.Core;
using DiskSweep.Core.Software;
using Xunit;

namespace DiskSweep.ViewModelTests;

/// <summary>
/// 强制删除服务：Geek 式扫描全关联但严守安全边界。
/// 重点验证 CanDelete=false 的项永不进入删除，且磁盘删除固定走回收站（allowUndo=true）。
/// </summary>
public class SoftwareForceRemovalServiceTests
{
    private static SoftwareEntry Entry(string name, string location, string publisher = "Acme") =>
        new("Machine64:" + name, name, "1.0", publisher, location, "2025-01-02",
            @"C:\Program Files\Acme\unins000.exe", 0, SoftwareScope.Machine64, false);

    [Fact]
    public void Scan_MarksOnlyRelatedAndSafeCandidatesDeletable()
    {
        var root = Path.Combine(Path.GetTempPath(), "ds-fr-" + Guid.NewGuid().ToString("N"));
        var related = Path.Combine(root, "Foobar");
        var unrelated = Path.Combine(root, "UnrelatedApp");
        var shared = Path.Combine(root, "Common Files");
        Directory.CreateDirectory(related);
        Directory.CreateDirectory(unrelated);
        Directory.CreateDirectory(shared);
        try
        {
            var service = new SoftwareForceRemovalService(safeRootsProvider: () => new[] { root });
            var result = service.Scan(Entry("Foobar", related));

            var dir = Assert.Single(result, c => c.Path == related);
            Assert.True(dir.CanDelete);

            var other = Assert.Single(result, c => c.Path == unrelated);
            Assert.False(other.CanDelete, "无关联证据的目录不可删");

            var sharedDir = Assert.Single(result, c => c.Path == shared);
            Assert.False(sharedDir.CanDelete, "共享运行时目录不可删");
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Remove_DeletesOnlyApprovedCandidates_AndAlwaysUsesRecycleBin()
    {
        var root = Path.Combine(Path.GetTempPath(), "ds-fr-" + Guid.NewGuid().ToString("N"));
        var good = Path.Combine(root, "Foobar");
        var blocked = Path.Combine(root, "UnrelatedApp");
        Directory.CreateDirectory(good);
        Directory.CreateDirectory(blocked);
        bool allowUndoObserved = false;
        IReadOnlyList<string>? deletedPaths = null;
        try
        {
            var service = new SoftwareForceRemovalService(
                deleteBatch: (paths, allowUndo) =>
                {
                    allowUndoObserved = allowUndo;
                    deletedPaths = paths;
                    foreach (var p in paths) if (Directory.Exists(p)) Directory.Delete(p);
                    return (0, false);
                },
                safeRootsProvider: () => new[] { root });

            var candidates = new[]
            {
                new ForceRemovalCandidate(good, "Foobar", ForceRemovalKind.Directory, true, "ok"),
                new ForceRemovalCandidate(blocked, "UnrelatedApp", ForceRemovalKind.Directory, false, "保留")
            };
            var result = service.Remove(candidates, Path.Combine(root, "backup"));

            Assert.True(allowUndoObserved, "强制删除也必须走回收站，可撤销");
            Assert.NotNull(deletedPaths);
            Assert.Equal(new[] { good }, deletedPaths);
            Assert.Equal(1, result.Deleted);
            Assert.Equal(0, result.Failed);
            Assert.False(Directory.Exists(good));
            Assert.True(Directory.Exists(blocked), "被判定不可删的目录必须保留");
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Remove_RechecksExistenceBeforeDeletion()
    {
        var root = Path.Combine(Path.GetTempPath(), "ds-fr-" + Guid.NewGuid().ToString("N"));
        var gone = Path.Combine(root, "Foobar");
        Directory.CreateDirectory(root);
        bool called = false;
        try
        {
            var service = new SoftwareForceRemovalService(
                deleteBatch: (paths, allowUndo) => { called = true; return (Hr: 0, Aborted: false); },
                safeRootsProvider: () => new[] { root });

            var candidates = new[]
            {
                new ForceRemovalCandidate(gone, "Foobar", ForceRemovalKind.Directory, true, "ok")
            };
            var result = service.Remove(candidates, Path.Combine(root, "backup"));

            Assert.False(called, "删除前复核发现路径已不存在，不得调用批量删除");
            Assert.Equal(0, result.Deleted);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Remove_RejectsWhenNothingIsDeletable()
    {
        var service = new SoftwareForceRemovalService();
        var result = service.Remove(new[]
        {
            new ForceRemovalCandidate(@"C:\Windows", "Windows", ForceRemovalKind.Directory, false, "系统目录")
        }, Path.GetTempPath());
        Assert.False(result.Ok);
        Assert.Equal(0, result.Deleted);
    }
}
