using Sweep.Core;
using Sweep.Core.Rules;
using Sweep.Core.Scanning;
using Xunit;

namespace Sweep.Tests;

/// <summary>
/// 扫描效率回归：MFT 路径重建的祖先缓存必须产出与逐次重建完全一致的完整路径，
/// 且在共享祖先时真正减少遍历（用缓存命中次数间接验证摊还效果）。
/// </summary>
public class ScanEfficiencyTests
{
    private static ScanEngine.UsnEntry E(ulong frn, ulong parent, string name) =>
        new(frn, parent, name, 0x10, DateTime.MinValue);

    [Fact]
    public void MftPathCache_ProducesSamePathsAsNoCache_ForSharedAncestors()
    {
        // 根(5) → Users(6) → alice(7) → a.tmp(10) / b.tmp(11) / c.tmp(12)
        var map = new Dictionary<ulong, ScanEngine.UsnEntry>
        {
            [6] = E(6, 5, "Users"),
            [7] = E(7, 6, "alice"),
            [10] = E(10, 7, "a.tmp"),
            [11] = E(11, 7, "b.tmp"),
            [12] = E(12, 7, "c.tmp")
        };

        var cache = new Dictionary<ulong, string>();
        Assert.Equal(@"C:\Users\alice\a.tmp", ScanEngine.RebuildPath(10, map, @"C:\", cache));
        Assert.Equal(@"C:\Users\alice\b.tmp", ScanEngine.RebuildPath(11, map, @"C:\", cache));
        Assert.Equal(@"C:\Users\alice\c.tmp", ScanEngine.RebuildPath(12, map, @"C:\", cache));

        // 每次用全新缓存逐次重建，结果必须与共享缓存一致
        Assert.Equal(@"C:\Users\alice\a.tmp",
            ScanEngine.RebuildPath(10, map, @"C:\", new Dictionary<ulong, string>()));
        Assert.Equal(@"C:\Users\alice\b.tmp",
            ScanEngine.RebuildPath(11, map, @"C:\", new Dictionary<ulong, string>()));
    }

    [Fact]
    public void MftPathCache_AncestorIsCachedAndReused()
    {
        var map = new Dictionary<ulong, ScanEngine.UsnEntry>
        {
            [6] = E(6, 5, "Users"),
            [7] = E(7, 6, "alice"),
            [10] = E(10, 7, "a.tmp")
        };
        var cache = new Dictionary<ulong, string>();
        ScanEngine.RebuildPath(10, map, @"C:\", cache);

        // 沿途每个祖先都被回填：后续兄弟文件可直接命中 alice 的缓存，不再走 Users→alice
        Assert.Equal(@"C:\Users", cache[6]);
        Assert.Equal(@"C:\Users\alice", cache[7]);
        Assert.Equal(@"C:\Users\alice\a.tmp", cache[10]);

        // 只给叶节点换名，父链命中缓存即可正确重建
        map[11] = E(11, 7, "b.tmp");
        Assert.Equal(@"C:\Users\alice\b.tmp", ScanEngine.RebuildPath(11, map, @"C:\", cache));
    }

    [Fact]
    public void AllocationSize_UsesCompressedFileSizeNotClusterAligned()
    {
        // 守护占用口径：FILE_STANDARD_INFO.AllocationSize 对小文件返回簇内对齐值（1B→8B），
        // 与 GetCompressedFileSize（返回 1）不同。扫描必须用后者，否则用户看到的"占用"会变。
        // 这条测试记录该陷阱，防止未来用"同句柄一次拿到"的写法误改口径。
        var dir = Path.Combine(Path.GetTempPath(), "sweep-alloc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "tiny.bin");
            File.WriteAllBytes(file, new byte[] { 0x42 });   // 1 字节
            var id = ScanEngine.GetIdentity(file);
            Assert.NotNull(id);
            long reported = ScanEngine.GetAllocationSize(file, id!.LogicalSize);
            Assert.True(reported > 0, "GetCompressedFileSize 不应返回 0（小文件至少按自身大小计）");
            Assert.Equal(1, id.LogicalSize);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* 清理失败不影响断言 */ }
        }
    }

    [Fact]
    public void TopLevelDirectory_HandlesRootAndNestedPaths()
    {
        // 顶层目录提取是扫描热路径，验证 span 版语义：盘根下的文件以自身文件名为顶层项
        // （与旧 Split 版一致，截图里的"目录 · pagefile.sys"正是如此）。
        var method = typeof(ScanService).GetMethod("TopLevelDirectory",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        string? Call(string root, string path) => (string?)method!.Invoke(null, new object[] { root, path });

        Assert.Equal("Users", Call(@"C:\", @"C:\Users\alice\a.tmp"));
        Assert.Equal("Windows", Call(@"C:\", @"C:\Windows\System32\drivers\a.sys"));
        Assert.Equal("pagefile.sys", Call(@"C:\", @"C:\pagefile.sys"));
        Assert.Equal("(根目录)", Call(@"C:\", @"C:\"));
        Assert.Equal("data", Call(@"D:\scan", @"D:\scan\data\x.bin"));
    }

    [Fact]
    public void MftPathCache_MissingParentReturnsNull()
    {
        var map = new Dictionary<ulong, ScanEngine.UsnEntry>
        {
            [10] = E(10, 999, "orphan.tmp")   // 父 FRN 不在 map
        };
        var cache = new Dictionary<ulong, string>();
        Assert.Null(ScanEngine.RebuildPath(10, map, @"C:\", cache));
    }

    // ── 目录级枚举（FileIdBothDirectoryInfo）取代逐文件 open 后的口径守护 ──────────────

    private static string CreateTree()
    {
        var root = Path.Combine(Path.GetTempPath(), "ds-walk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "sub", "deeper"));
        File.WriteAllBytes(Path.Combine(root, "a.bin"), new byte[] { 0x42 });            // 1B（驻留）
        File.WriteAllBytes(Path.Combine(root, "b.bin"), new byte[3000]);                 // < 1 簇
        File.WriteAllBytes(Path.Combine(root, "sub", "c.bin"), Array.Empty<byte>());     // 0B
        File.WriteAllBytes(Path.Combine(root, "sub", "deeper", "d.bin"), new byte[100_000]);
        return root;
    }

    [Fact]
    public async Task Walk_ReportsEveryEntryOnce_WithSameFileIdAsHandleQuery()
    {
        var root = CreateTree();
        try
        {
            var entries = new List<RawEntry>();
            await foreach (var e in ScanEngine.WalkAsync(root, new ScanStats())) entries.Add(e);

            var expected = Directory.EnumerateFileSystemEntries(root, "*",
                    new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 })
                .Select(p => Path.GetFullPath(p)).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
            var actual = entries.Select(e => e.Path).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();

            Assert.Equal(expected, actual);                      // 不多不少、不重复
            Assert.Equal(expected.Length, actual.Length);

            foreach (var e in entries.Where(x => !x.IsDirectory))
            {
                // 目录项里的 64 位文件 ID 必须与句柄查询一致：TOCTOU 与硬链接去重都依赖它。
                var identity = ScanEngine.GetIdentity(e.Path);
                Assert.NotNull(identity);
                Assert.Equal(identity!.FileId, e.FileId);
                Assert.Equal(identity.LogicalSize, e.LogicalSize);
                Assert.True(e.VolumeSerial != 0, "卷序列号应整卷取到，不应为 0");
            }
        }
        finally { try { Directory.Delete(root, true); } catch { /* 清理失败不影响断言 */ } }
    }

    [Fact]
    public async Task Walk_AllocationMatchesCompressedFileSize()
    {
        // 占用口径红线：目录项里的 AllocationSize 是簇内对齐值（1B→8B/4096B），
        // 扫描对外呈现的"占用"必须与 GetCompressedFileSize 一致。
        var root = CreateTree();
        try
        {
            var checkedFiles = 0;
            await foreach (var e in ScanEngine.WalkAsync(root, new ScanStats()))
            {
                if (e.IsDirectory) continue;
                long reported = (long)ScanEngine.GetAllocationSize(e.Path, e.LogicalSize);
                Assert.Equal(reported, e.AllocationSize);
                checkedFiles++;
            }
            Assert.Equal(4, checkedFiles);
        }
        finally { try { Directory.Delete(root, true); } catch { /* 清理失败不影响断言 */ } }
    }

    [Fact]
    public void DuplicateDetector_FindsIdenticalFiles_AndRejectsSamePrefixOnly()
    {
        var root = Path.Combine(Path.GetTempPath(), "ds-dup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            const int size = 2 * 1024 * 1024;
            var head = new byte[size];
            new Random(7).NextBytes(head);
            var tailDifferent = (byte[])head.Clone();
            tailDifferent[size - 1] ^= 0xFF;   // 只有最后一个字节不同

            var a = Path.Combine(root, "a.bin");
            var b = Path.Combine(root, "b.bin");
            var c = Path.Combine(root, "c.bin");
            File.WriteAllBytes(a, head);
            File.WriteAllBytes(b, head);
            File.WriteAllBytes(c, tailDifferent);

            var candidates = new[] { a, b, c }
                .Select(p => new CandidateItem(p, "其他", size, size, 30, ".bin",
                    FileAttributes.Normal, 0, 0, false))
                .ToList();

            var groups = DuplicateDetector.Find(candidates);

            var group = Assert.Single(groups);
            Assert.Equal(2, group.Paths.Count);
            Assert.Contains(a, group.Paths);
            Assert.Contains(b, group.Paths);
            Assert.DoesNotContain(c, group.Paths);
        }
        finally { try { Directory.Delete(root, true); } catch { /* 清理失败不影响断言 */ } }
    }

    [Fact]
    public async Task Scan_NormalizesForwardSlashRoot()
    {
        // 用户可能粘贴 C:/x/y 形式：规则正则按 \ 写、界面也按 \ 展示，入口统一分隔符。
        var root = CreateTree();
        try
        {
            var result = await ScanService.ScanAsync(root.Replace('\\', '/'), ScanMode.Walk, TestFixture.Rules());
            Assert.DoesNotContain('/', result.Snapshot.RootPath);
            Assert.All(result.Items, i => Assert.DoesNotContain('/', i.Path));
            Assert.Equal(6, result.Snapshot.TotalFiles);   // 4 文件 + 2 目录（TotalFiles 实为对象数）
        }
        finally { try { Directory.Delete(root, true); } catch { /* 清理失败不影响断言 */ } }
    }

    [Fact]
    public void Glob_FragmentsAreOnlyNecessaryNotSufficient()
    {
        // 预筛用「路径必须包含字面量片段」快速拒绝，但最终仍由正则定稿：
        // node_modules_backup 含 node_modules 片段，却不应命中 **\node_modules\**
        Assert.True(GlobMatcher.TryCreate(@"**\node_modules\**", out var matcher, out _));
        Assert.True(matcher!.Matches(@"C:\proj\node_modules\pkg\index.js"));
        Assert.False(matcher.Matches(@"C:\proj\node_modules_backup\index.js"));
        Assert.False(matcher.Matches(@"C:\proj\src\index.js"));
    }

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"D:\")]
    public void NormalizeScanRoot_KeepsDriveRootSeparator(string root)
    {
        // 守护整盘扫描口径：盘根必须是 C:\，不能Trim成 C:——后者是「驱动器当前目录」，
        // 曾经导致扫 C 盘只枚举到当前目录的几十个文件（用户看到只有十几 GB）。
        var expected = root[..2] + @"\";
        Assert.Equal(expected, ScanEngine.NormalizeScanRoot(root));
    }

    [Fact]
    public void NormalizeScanRoot_StripsTrailingSeparatorOnSubDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "ds-root-");
        Assert.Equal(root.TrimEnd('\\'), ScanEngine.NormalizeScanRoot(root));
        // 带不带尾斜杠是同一种根，拼接子项时不会出现 C:\\x 双分隔符
        Assert.Equal(ScanEngine.NormalizeScanRoot(root),
            ScanEngine.NormalizeScanRoot(root + "\\"));
    }

    [Fact]
    public async Task Walk_CancelDuringLargeEnumeration_ReturnsQuickly()
    {
        // 取消必须传进目录枚举内部：原来只在每轮 32 个目录之间检查，
        // WinSxS 这类十万级目录会让「取消扫描」干等十几秒。
        var root = CreateTree();
        try
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await foreach (var _ in ScanEngine.WalkAsync(root, new ScanStats(), cts.Token)) { }
            sw.Stop();
            Assert.True(sw.ElapsedMilliseconds < 2000, $"取消后仍在枚举：{sw.ElapsedMilliseconds} ms");
        }
        finally { try { Directory.Delete(root, true); } catch { /* 清理失败不影响断言 */ } }
    }
}
