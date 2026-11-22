using System.Runtime.CompilerServices;
using DiskSweep.Core.Rules;

namespace DiskSweep.Core.Scanning;

public sealed record CategorySummary(string Category, long TotalBytes, long FileCount);
public sealed record ScanProgress(long Entries, long Bytes, string CurrentPath, string ModeLabel);

public sealed record ScanResult(
    ScanSnapshot Snapshot,
    IReadOnlyList<CandidateItem> Items,
    IReadOnlyDictionary<string, RuleMatch> RuleMatches,
    IReadOnlyList<CategorySummary> Categories,
    IReadOnlyList<DuplicateGroup> Duplicates,
    ScanStats Stats,
    bool Degraded,
    string? DegradeReason,
    int OmittedDetailCount,
    // 空文件/空目录候选：与明细限额分开收集，避免挤占规则候选名额。
    // 末尾可选参数，既有构造点（含测试）无需改动。
    IReadOnlyList<CandidateItem>? EmptyCandidates = null,
    // 重复文件检测是否因时间/读取预算提前收尾（结果仍是已确认的重复组，只是没扫全）。
    bool DuplicatesTruncated = false,
    // 整根没扫成的扫描目标（盘符不可访问 / 打开抛异常）。必须与 DegradeReason（换扫描方式）
    // 分开报：一个是"这块盘的内容完全没统计"，一个是"换了种方式统计"，混淆会误导用户。
    IReadOnlyList<string>? SkippedRoots = null);

/// <summary>流式消费扫描结果：硬链接去重、分类聚合、明细限额（文档 4.1.2 / 5.1）。</summary>
public static class ScanService
{
    private const int MaxDuplicatePool = 4000;
    private const int TopLargeFiles = 20;

    public static async Task<ScanResult> ScanAsync(
        string root,
        ScanMode requestedMode,
        RuleEngine rules,
        CancellationToken ct = default,
        IProgress<ScanProgress>? progress = null,
        int maxDetailed = 1000,
        long largeFileThreshold = 500L * 1024 * 1024)
    {
        // 统一分隔符：用户可能粘贴 C:/x/y 形式的路径，规则正则与界面展示都按反斜杠口径。
        root = Path.GetFullPath(root).Replace('/', '\\');
        var stats = new ScanStats();
        bool degraded = false;
        string? degradeReason = null;
        var actualMode = requestedMode;
        IAsyncEnumerable<RawEntry> stream;

        if (requestedMode == ScanMode.Mft)
        {
            bool ok = ScanEngine.TryQuickScan(root, stats, ct, out var mft, out degradeReason);
            if (ok && mft is not null)
                stream = ToAsync(mft, ct);
            else
            {
                degraded = true;
                actualMode = ScanMode.Walk;
                stream = ScanEngine.WalkAsync(root, stats, ct);
            }
        }
        else stream = ScanEngine.WalkAsync(root, stats, ct);

        var snapshot = new ScanSnapshot(Guid.NewGuid().ToString("N"), Path.GetFullPath(root),
            DateTime.UtcNow, null, actualMode, 0, 0, 0);
        var modeLabel = actualMode == ScanMode.Mft ? "MFT 快速扫描" : degraded ? "兼容扫描（已降级）" : "兼容扫描";

        var seen = new HashSet<(ulong Volume, ulong Id)>();
        // 明细候选按体积保留最大的 N 条（而不是"先扫到的前 N 条"）：
        // 十万个零字节临时文件会把清单挤满，让真正占空间的项排不进来，用户看着清了却没释放多少。
        var ruleTop = new PriorityQueue<(CandidateItem Item, RuleMatch? Match), long>(maxDetailed + 1);
        long ruleCandidates = 0;
        var ruleMatches = new Dictionary<string, RuleMatch>(StringComparer.OrdinalIgnoreCase);
        var largeFiles = new List<CandidateItem>();
        var duplicatePool = new List<CandidateItem>();
        var dirStats = new Dictionary<string, (long Bytes, long Count)>(StringComparer.OrdinalIgnoreCase);
        var extStats = new Dictionary<string, (long Bytes, long Count)>(StringComparer.OrdinalIgnoreCase);
        // 扩展名 / 顶层目录名在热路径上逐条出现：用 span 查表复用一个实例，
        // 避免每条记录都分配一个新的小字符串（百万级扫描时是几十 MB 的纯垃圾）。
        var extKeys = new SpanKeyCache();
        var dirKeys = new SpanKeyCache();
        // 空项收集：目录路径全集 - 出现在任一子项父路径中的目录 = 空目录；零字节文件单独记录。
        var allDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var nonEmptyDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var emptyFiles = new List<CandidateItem>();
        int omitted = 0;
        long lastReport = 0;
        var nowUtc = DateTime.UtcNow;
        long agedAt = 0;

        await foreach (var entry in stream.WithCancellation(ct))
        {
            // 硬链接去重：同 (volume,fileId) 只保留一条计费路径
            if (!entry.IsDirectory && entry.FileId != 0 && !seen.Add((entry.VolumeSerial, entry.FileId)))
            {
                stats.Aliases++;
                continue;
            }

            var lexical = entry.Path.Replace('/', '\\').TrimEnd(' ', '.');
            // 扫描可能持续数分钟：每 4096 条取一次时钟即可（天数粒度，不需要逐条读系统时间）。
            if (stats.TotalEntries - agedAt >= 4096) { nowUtc = DateTime.UtcNow; agedAt = stats.TotalEntries; }
            int ageDays = entry.LastWriteUtc == DateTime.MinValue
                ? 999
                : Math.Clamp((int)(nowUtc - entry.LastWriteUtc).TotalDays, 0, 10000);
            var extSpan = Path.GetExtension(lexical.AsSpan());
            var match = rules.Match(lexical, ageDays);

            if (!entry.IsDirectory)
            {
                var topDir = dirKeys.Get(TopLevelDirectorySpan(snapshot.RootPath, lexical), lowerCase: false);
                Accumulate(dirStats, topDir, entry.AllocationSize);
                var ext = extSpan.IsEmpty ? extKeys.Get("(无扩展名)".AsSpan(), false) : extKeys.Get(extSpan, lowerCase: true);
                Accumulate(extStats, ext, entry.AllocationSize);
            }

            var candidate = default(CandidateItem);
            bool keepRule = match is not null && match.Action == RecommendationAction.Keep;
            bool ruleCandidate = match is not null && !keepRule;
            bool isLarge = !entry.IsDirectory && entry.AllocationSize >= largeFileThreshold;
            bool duplicateCandidate = !entry.IsDirectory && entry.AllocationSize >= 1024 * 1024 &&
                                      duplicatePool.Count < MaxDuplicatePool;
            bool isZeroByte = !entry.IsDirectory && entry.LogicalSize == 0 && entry.AllocationSize == 0;

            // 只有真正要进清单/参与判定的对象才构造 CandidateItem：绝大多数对象（普通文件）
            // 扫完即弃，逐条分配一个十字段记录纯属浪费。空文件也要建，它走空项清单。
            if (ruleCandidate || isLarge || duplicateCandidate || isZeroByte)
                candidate = new CandidateItem(
                    entry.Path, match?.Category ?? "其他", entry.AllocationSize, entry.LogicalSize,
                    ageDays, extSpan.IsEmpty ? "" : extKeys.Get(extSpan, lowerCase: true), entry.Attributes,
                    entry.VolumeSerial, entry.FileId, entry.IsDirectory);

            // 空项检测：目录记入全集并把父目录标记为非空；零字节且零占用的文件直接记录。
            if (entry.IsDirectory)
                allDirectories.Add(lexical);
            else
            {
                var parent = ParentPath(lexical);
                if (parent is not null) nonEmptyDirectories.Add(parent);
                if (isZeroByte) emptyFiles.Add(candidate!);
            }

            if (ruleCandidate)
            {
                ruleCandidates++;
                Offer(ruleTop, candidate!, match, maxDetailed);
            }
            if (isLarge) largeFiles.Add(candidate!);
            if (duplicateCandidate) duplicatePool.Add(candidate!);

            if (stats.TotalEntries - lastReport >= 1000)
            {
                lastReport = stats.TotalEntries;
                progress?.Report(new ScanProgress(stats.TotalEntries, stats.TotalBytes, lexical, modeLabel));
            }
        }

        // 规则候选按体积降序落表；只有最终保留的条目才需要路径 → 规则命中的索引。
        var detailed = new List<CandidateItem>(maxDetailed);
        var detailedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in ruleTop.UnorderedItems
                     .OrderByDescending(x => x.Element.Item.AllocationSize))
        {
            var item = pair.Element.Item;
            if (!detailedPaths.Add(item.Path)) continue;
            detailed.Add(item);
            if (pair.Element.Match is { } match) ruleMatches[item.Path] = match;
        }
        omitted = (int)Math.Max(0, ruleCandidates - detailed.Count);

        // 大文件 Top N：规则明细之外单独保留，默认不勾选
        foreach (var f in largeFiles.OrderByDescending(f => f.AllocationSize).Take(TopLargeFiles))
        {
            if (!detailedPaths.Add(f.Path)) continue;
            if (detailed.Count >= maxDetailed) { omitted++; break; }
            detailed.Add(f with { Category = "大文件" });
        }

        var duplicates = DuplicateDetector.Find(duplicatePool, ct, null, out bool duplicatesTruncated);
        // 一次建索引，消除对 duplicatePool 的逐路径 FirstOrDefault 线性扫描（O(n²) → O(n)）。
        // 用 TryAdd 兜底：极端情况下（同一路径多个硬链接别名）保留首个，绝不让索引构建抛异常中断扫描。
        var poolByPath = new Dictionary<string, CandidateItem>(duplicatePool.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var c in duplicatePool) poolByPath.TryAdd(c.Path, c);
        foreach (var group in duplicates)
        {
            foreach (var path in group.Paths.Skip(1))   // 保留一份原件，其余列为可清理候选
            {
                if (!detailedPaths.Add(path)) continue;
                if (detailed.Count >= maxDetailed) { omitted++; break; }
                if (!poolByPath.TryGetValue(path, out var src)) continue;
                detailed.Add(src with { Category = "重复文件" });
            }
        }

        var categories = dirStats
            .Select(kv => new CategorySummary("目录 · " + kv.Key, kv.Value.Bytes, kv.Value.Count))
            .Concat(extStats.Select(kv => new CategorySummary("类型 · " + kv.Key, kv.Value.Bytes, kv.Value.Count)))
            .OrderByDescending(c => c.TotalBytes)
            .Take(30)
            .ToArray();

        var finished = ct.IsCancellationRequested ? (DateTime?)null : DateTime.UtcNow;
        var finalSnapshot = snapshot with
        {
            FinishedUtc = finished,
            TotalFiles = stats.TotalEntries,
            TotalBytes = stats.TotalBytes,
            DeniedCount = stats.Denied
        };
        progress?.Report(new ScanProgress(stats.TotalEntries, stats.TotalBytes, "", modeLabel));

        var emptyCandidates = CollectEmptyItems(allDirectories, nonEmptyDirectories, emptyFiles,
            snapshot.RootPath, complete: !ct.IsCancellationRequested);
        return new ScanResult(finalSnapshot, detailed, ruleMatches, categories, duplicates,
            stats, degraded, degradeReason, omitted, emptyCandidates, duplicatesTruncated);
    }

    /// <summary>
    /// 全集减去「至少含一个子项」的目录即为空目录；连同零字节文件一起作为独立候选返回。
    /// 扫描被取消时结果不完整，此时不给出空目录判断（避免把尚有子项未枚举的目录误判为空）。
    ///
    /// 折叠：一条 a\b\c 全空的目录链里，三个目录都"没有子项文件"，原实现会把三层全部列出，
    /// 用户勾选后既重复计数、又可能先删父后删子而失败。这里只保留最外层空目录，
    /// 并把位于空目录之下的零字节文件一并折叠（删目录时自然带走）。
    /// </summary>
    internal static IReadOnlyList<CandidateItem> CollectEmptyItems(
        HashSet<string> allDirectories, HashSet<string> nonEmptyDirectories,
        List<CandidateItem> emptyFiles, string rootPath, bool complete = true)
    {
        var emptyDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var root = rootPath.TrimEnd('\\');
        if (complete)
        {
            foreach (var dir in allDirectories)
            {
                if (nonEmptyDirectories.Contains(dir)) continue;
                if (string.Equals(dir, root, StringComparison.OrdinalIgnoreCase)) continue;
                emptyDirs.Add(dir);
            }
        }

        var result = new List<CandidateItem>(emptyFiles.Count + emptyDirs.Count);
        foreach (var file in emptyFiles)
        {
            // 父目录整体为空 → 由父目录那条候选负责，避免同一批里父子并存。
            var parent = ParentPath(file.Path);
            if (parent is not null && emptyDirs.Contains(parent)) continue;
            result.Add(file);
        }

        foreach (var dir in emptyDirs)
        {
            if (HasEmptyAncestor(dir, emptyDirs, root)) continue;
            result.Add(new CandidateItem(dir, "空目录", 0, 0, 0, "", FileAttributes.Directory, 0, 0, true));
        }
        return result;
    }

    /// <summary>沿父链判断是否已有更外层的空目录；链长受路径深度限制，代价可忽略。</summary>
    private static bool HasEmptyAncestor(string dir, HashSet<string> emptyDirs, string root)
    {
        int idx = dir.LastIndexOf('\\');
        while (idx > root.Length)
        {
            var parent = dir[..idx];
            if (emptyDirs.Contains(parent)) return true;
            idx = parent.LastIndexOf('\\');
        }
        return false;
    }

    private static string? ParentPath(string lexical)
    {
        int idx = lexical.LastIndexOf('\\');
        if (idx <= 2) return null;   // 形如 C:\xxx 的父是卷根，不参与空目录判断
        return lexical[..idx];
    }

    /// <summary>
    /// 「所有磁盘」：依次扫描多个盘根并合并成一份结果。单根流程（MFT 降级、硬链接去重、
    /// 明细限额）完全复用 ScanAsync，这里只负责汇总与跨根进度累计；单盘失败不影响其它盘。
    /// </summary>
    public static async Task<ScanResult> ScanAllAsync(
        IReadOnlyList<string> roots,
        ScanMode requestedMode,
        RuleEngine rules,
        CancellationToken ct = default,
        IProgress<ScanProgress>? progress = null,
        int maxDetailed = 1000,
        long largeFileThreshold = 500L * 1024 * 1024)
    {
        if (roots is null || roots.Count == 0)
            throw new ArgumentException("至少需要一个扫描根目录", nameof(roots));
        if (roots.Count == 1)
            return await ScanAsync(roots[0], requestedMode, rules, ct, progress,
                maxDetailed, largeFileThreshold).ConfigureAwait(false);

        var items = new List<CandidateItem>();
        var emptyItems = new List<CandidateItem>();
        var matches = new Dictionary<string, RuleMatch>(StringComparer.OrdinalIgnoreCase);
        var duplicates = new List<DuplicateGroup>();
        var categories = new Dictionary<string, (long Bytes, long Count)>(StringComparer.OrdinalIgnoreCase);
        var total = new ScanStats();
        var reasons = new List<string>();
        var skippedRoots = new List<string>();
        bool degraded = false;
        bool duplicatesTruncated = false;
        int omitted = 0;
        long cumulativeEntries = 0, cumulativeBytes = 0;
        DateTime startedUtc = DateTime.UtcNow;
        ScanMode actualMode = requestedMode;

        foreach (string root in roots)
        {
            if (ct.IsCancellationRequested) break;
            if (string.IsNullOrWhiteSpace(root)) continue;
            if (!Directory.Exists(root))
            {
                // 整根不可用：内容 0 统计。不算「降级」（那是指换了扫描方式），单独记。
                skippedRoots.Add(Path.GetFileName(root.TrimEnd('\\', '/')) + ": 目录不可访问");
                continue;
            }

            IProgress<ScanProgress>? bridge = progress is null ? null :
                new OffsetProgress(progress, cumulativeEntries, cumulativeBytes);
            ScanResult part;
            try
            {
                part = await ScanAsync(root, requestedMode, rules, ct, bridge,
                    Math.Max(1, maxDetailed - items.Count), largeFileThreshold).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                           or ArgumentException or System.Security.SecurityException)
            {
                skippedRoots.Add(Path.GetFileName(root.TrimEnd('\\', '/')) + ": " + ex.GetType().Name);
                continue;
            }

            actualMode = part.Snapshot.Mode;
            cumulativeEntries += part.Stats.TotalEntries;
            cumulativeBytes += part.Stats.TotalBytes;
            items.AddRange(part.Items);
            if (part.EmptyCandidates is not null) emptyItems.AddRange(part.EmptyCandidates);
            foreach (var pair in part.RuleMatches) matches[pair.Key] = pair.Value;
            duplicates.AddRange(part.Duplicates);
            foreach (var c in part.Categories)
            {
                categories.TryGetValue(c.Category, out var acc);
                categories[c.Category] = (acc.Bytes + c.TotalBytes, acc.Count + c.FileCount);
            }

            total.TotalEntries += part.Stats.TotalEntries;
            total.TotalBytes += part.Stats.TotalBytes;
            total.Denied += part.Stats.Denied;
            foreach (var deniedPath in part.Stats.DeniedPaths)
            {
                if (total.DeniedPaths.Count >= ScanStats.MaxDeniedSamples) break;
                total.DeniedPaths.Add(deniedPath);
            }
            total.Aliases += part.Stats.Aliases;
            total.AttributeFailures += part.Stats.AttributeFailures;
            total.UnresolvablePaths += part.Stats.UnresolvablePaths;
            total.ReparsePoints += part.Stats.ReparsePoints;
            omitted += part.OmittedDetailCount;
            duplicatesTruncated |= part.DuplicatesTruncated;
            if (part.Degraded)
            {
                degraded = true;
                if (!string.IsNullOrWhiteSpace(part.DegradeReason)) reasons.Add(part.DegradeReason!);
            }
        }

        var summary = categories
            .Select(kv => new CategorySummary(kv.Key, kv.Value.Bytes, kv.Value.Count))
            .OrderByDescending(c => c.TotalBytes)
            .Take(30)
            .ToArray();

        // 合并快照仍是单个 SnapshotId：落库、推荐、撤销链路无需感知多根。
        var snapshot = new ScanSnapshot(
            Guid.NewGuid().ToString("N"),
            string.Join(";", roots),
            startedUtc,
            ct.IsCancellationRequested ? null : DateTime.UtcNow,
            actualMode,
            total.TotalEntries, total.TotalBytes, total.Denied);

        return new ScanResult(snapshot, items, matches, summary, duplicates, total,
            degraded, reasons.Count == 0 ? null : string.Join(" / ", reasons.Distinct()), omitted, emptyItems,
            duplicatesTruncated, skippedRoots.Count == 0 ? null : skippedRoots);
    }

    /// <summary>把每根从 0 开始的进度换算成全程累计值，避免状态栏数字在换盘时回跳。</summary>
    private sealed class OffsetProgress : IProgress<ScanProgress>
    {
        private readonly IProgress<ScanProgress> _inner;
        private readonly long _entries;
        private readonly long _bytes;

        public OffsetProgress(IProgress<ScanProgress> inner, long entries, long bytes)
        {
            _inner = inner;
            _entries = entries;
            _bytes = bytes;
        }

        public void Report(ScanProgress value) => _inner.Report(new ScanProgress(
            _entries + value.Entries, _bytes + value.Bytes, value.CurrentPath, value.ModeLabel));
    }

    private static void Accumulate(Dictionary<string, (long Bytes, long Count)> map, string key, long bytes)
    {
        map.TryGetValue(key, out var current);
        map[key] = (current.Bytes + bytes, current.Count + 1);
    }

    /// <summary>
    /// 明细候选的「保留最大 N 条」漏斗：满了之后只与当前最小项比较，比它大才替换。
    /// 复杂度 O(log n)/条，且最大保留量固定，内存不随候选数增长。
    /// </summary>
    private static void Offer(
        PriorityQueue<(CandidateItem Item, RuleMatch? Match), long> queue,
        CandidateItem item, RuleMatch? match, int capacity)
    {
        if (capacity <= 0) return;
        if (queue.Count < capacity)
        {
            queue.Enqueue((item, match), item.AllocationSize);
            return;
        }
        if (queue.TryPeek(out _, out long smallest) && item.AllocationSize <= smallest) return;
        queue.Dequeue();
        queue.Enqueue((item, match), item.AllocationSize);
    }

    /// <summary>把热路径上反复出现的短字符串（扩展名、顶层目录名）归一成同一实例。</summary>
    private sealed class SpanKeyCache
    {
        private readonly Dictionary<string, string> _map = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> _lookup;

        public SpanKeyCache() => _lookup = _map.GetAlternateLookup<ReadOnlySpan<char>>();

        public string Get(ReadOnlySpan<char> key, bool lowerCase)
        {
            if (_lookup.TryGetValue(key, out var existing)) return existing;
            var created = lowerCase ? key.ToString().ToLowerInvariant() : key.ToString();
            _map[created] = created;
            return created;
        }
    }

    /// <summary>
    /// 取文件相对于扫描根的第一级目录名。用 span 切片 + IndexOf 替代 Split，
    /// 避免每个文件都分配一条相对路径字符串和一个字符串数组（热路径上的 GC 压力）。
    /// </summary>
    private static string TopLevelDirectory(string root, string path) =>
        TopLevelDirectorySpan(root, path).ToString();

    /// <summary>span 版：由调用方决定是否立即转成字符串（通常会先查归一化缓存）。</summary>
    private static ReadOnlySpan<char> TopLevelDirectorySpan(string root, string path)
    {
        var r = root.AsSpan().TrimEnd('\\');
        if (path.Length <= r.Length || !path.AsSpan(0, r.Length).Equals(r, StringComparison.OrdinalIgnoreCase))
            return "(根目录)";
        var relative = path.AsSpan(r.Length).TrimStart('\\');
        if (relative.IsEmpty) return "(根目录)";
        int sep = relative.IndexOf('\\');
        var first = sep >= 0 ? relative[..sep] : relative;
        return first.IsEmpty ? "(根目录)" : first;
    }

    private static async IAsyncEnumerable<RawEntry> ToAsync(
        IEnumerable<RawEntry> source, [EnumeratorCancellation] CancellationToken ct)
    {
        int n = 0;
        foreach (var entry in source)
        {
            if (ct.IsCancellationRequested) yield break;
            yield return entry;
            if (++n % 256 == 0) await Task.Yield();
        }
    }
}
