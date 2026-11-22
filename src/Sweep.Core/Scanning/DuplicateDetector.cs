using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography;

namespace Sweep.Core.Scanning;

public sealed record DuplicateGroup(string Hash, long SizeBytes, IReadOnlyList<string> Paths);

/// <summary>
/// 同大小 → 前缀快速哈希 → 全量哈希；先比大小可显著减少哈希 IO（文档 7.2）。
/// 一组内的哈希并行执行（SSD 上多流读能重叠延迟），结果按原顺序回填以保证输出稳定。
///
/// 预算：重复文件检测是扫描里唯一「读文件内容」的阶段，在机械盘或开了实时防护的机器上
/// 能把整次扫描从 8 秒拖到 40 秒。因此它有三道闸：按「浪费空间」取前若干组、
/// 单次全量哈希总量上限、以及墙钟预算——撞到任一闸即停，扫描总时长可控。
/// </summary>
public static class DuplicateDetector
{
    private const long MinSize = 1024 * 1024;

    /// <summary>按「组内浪费空间」降序最多看这么多组；更靠后的组价值极低，不值得为其读盘。</summary>
    private const int MaxGroups = 8;

    /// <summary>每组最多比对这么多个文件（同一内容的副本超过十余份极少见）。</summary>
    private const int MaxFilesPerGroup = 6;

    private const int QuickHashBytes = 32 * 1024;

    /// <summary>全量哈希总读取量上限，防止一组超大文件把时间预算吃光。</summary>
    private const long MaxFullHashBytes = 256L * 1024 * 1024;

    /// <summary>默认墙钟预算：超过即停止，只返回已确认的重复组。</summary>
    public static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(1.5);

    /// <summary>哈希是 IO 密集：并发度取核数一半（2~6），既重叠延迟又不把机械盘打散。</summary>
    private static readonly ParallelOptions HashParallel = new()
    {
        MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 6)
    };

    public static IReadOnlyList<DuplicateGroup> Find(
        IEnumerable<CandidateItem> candidates, CancellationToken ct = default, TimeSpan? budget = null)
        => Find(candidates, ct, budget, out _);

    public static IReadOnlyList<DuplicateGroup> Find(
        IEnumerable<CandidateItem> candidates, CancellationToken ct, TimeSpan? budget, out bool truncated)
    {
        truncated = false;
        var limit = budget ?? DefaultBudget;
        if (limit <= TimeSpan.Zero) { truncated = true; return Array.Empty<DuplicateGroup>(); }

        var bySize = candidates
            .Where(c => !c.IsDirectory && c.AllocationSize >= MinSize)
            .GroupBy(c => c.AllocationSize)
            .Where(g => g.Count() >= 2)
            .OrderByDescending(g => g.Key * (g.Count() - 1))
            .Take(MaxGroups);

        var hashBudget = new HashBudget(limit);
        var result = new List<DuplicateGroup>();
        long hashedBytes = 0;
        foreach (var group in bySize)
        {
            if (ct.IsCancellationRequested) break;
            if (hashBudget.Spent || hashedBytes >= MaxFullHashBytes) { truncated = true; break; }
            var paths = group.Take(MaxFilesPerGroup).Select(f => f.Path).ToList();

            // 第一层：只读每个文件的前 32KB
            var quickHashes = HashAll(paths, QuickHashBytes, ct, hashBudget);
            var quick = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            for (int i = 0; i < paths.Count; i++)
            {
                if (quickHashes[i] is not { } h) continue;
                if (!quick.TryGetValue(h, out var list)) quick[h] = list = new List<string>();
                list.Add(paths[i]);
            }
            // 每个文件的前缀都不同 → 这组不可能有重复，省掉全量读取。
            if (quick.Count == paths.Count) continue;

            // 第二层：前缀相同才做全量哈希（真正的 IO 大头，只对疑似重复文件付）
            foreach (var bucket in quick.Values)
            {
                if (bucket.Count < 2 || ct.IsCancellationRequested) continue;
                if (hashBudget.Spent || hashedBytes >= MaxFullHashBytes) { truncated = true; break; }
                hashedBytes += (long)bucket.Count * group.Key;
                var fullHashes = HashAll(bucket, long.MaxValue, ct, hashBudget);
                var full = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                for (int i = 0; i < bucket.Count; i++)
                {
                    if (fullHashes[i] is not { } h) continue;
                    if (!full.TryGetValue(h, out var list)) full[h] = list = new List<string>();
                    list.Add(bucket[i]);
                }
                foreach (var kv in full.Where(kv => kv.Value.Count >= 2))
                    result.Add(new DuplicateGroup(kv.Key, group.Key, kv.Value));
            }
            if (result.Count >= MaxGroups) break;
        }
        truncated |= hashBudget.Exhausted;
        return result;
    }

    /// <summary>
    /// 墙钟预算。必须做到「单文件读盘途中」也能停：一组 3×80MB 的文件可以一口气读十几秒，
    /// 只在组间检查等于没有预算。读盘途中放弃时返回 null（视为未知，绝不当作"不重复"以外的结论）。
    /// </summary>
    private sealed class HashBudget
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly TimeSpan _limit;
        private volatile bool _exhausted;

        public HashBudget(TimeSpan limit) => _limit = limit;

        public bool Spent => _exhausted || _clock.Elapsed >= _limit;
        public bool Exhausted => _exhausted || _clock.Elapsed >= _limit;
        public void GiveUp() => _exhausted = true;
    }

    private static string?[] HashAll(IReadOnlyList<string> paths, long maxBytes, CancellationToken ct,
        HashBudget budget)
    {
        var hashes = new string?[paths.Count];
        if (paths.Count >= 2 && Environment.ProcessorCount > 1)
        {
            try
            {
                // 绑定取消令牌：否则点取消后，已排队的大文件哈希还会逐个读完。
                Parallel.For(0, paths.Count,
                    new ParallelOptions
                    {
                        CancellationToken = ct,
                        MaxDegreeOfParallelism = HashParallel.MaxDegreeOfParallelism
                    },
                    i =>
                    {
                        if (!ct.IsCancellationRequested && !budget.Spent)
                            hashes[i] = HashFile(paths[i], maxBytes, budget);
                    });
            }
            catch (OperationCanceledException)
            {
                // 取消是正常退出，返回已算出的部分哈希。
            }
        }
        else
        {
            for (int i = 0; i < paths.Count; i++)
            {
                if (ct.IsCancellationRequested || budget.Spent) break;
                hashes[i] = HashFile(paths[i], maxBytes, budget);
            }
        }
        return hashes;
    }

    /// <summary>
    /// 前缀快哈希与全量哈希共用：maxBytes 小于缓冲大小时只读前缀，并走一次性 SHA256.HashData
    /// （无需 IncrementalHash 的对象与状态机）；否则分块喂给增量哈希。
    /// </summary>
    private static string? HashFile(string path, long maxBytes, HashBudget budget)
    {
        byte[]? buffer = ArrayPool<byte>.Shared.Rent(1 << 16);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);

            if (maxBytes <= buffer.Length)
            {
                int read = stream.ReadAtLeast(buffer, (int)maxBytes, throwOnEndOfStream: false);
                return Convert.ToHexString(SHA256.HashData(buffer.AsSpan(0, read)));
            }

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long remaining = maxBytes;
            int chunk;
            while (remaining > 0 && (chunk = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining))) > 0)
            {
                if (budget.Spent) { budget.GiveUp(); return null; }
                hash.AppendData(buffer, 0, chunk);
                remaining -= chunk;
            }
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
        finally
        {
            if (buffer is not null) ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
