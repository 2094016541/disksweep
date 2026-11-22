using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Sweep.Core.Scanning;

public sealed class ScanStats
{
    public long TotalEntries;
    public long TotalBytes;
    public long Denied;
    public long Aliases;
    public long AttributeFailures;
    public long UnresolvablePaths;
    public long ReparsePoints;
    /// <summary>被拒绝的目录里属于系统保护机制的数量（系统还原/恢复环境/商店应用/回收站）。</summary>
    public long DeniedSystemProtected;
    /// <summary>被拒绝的目录里其它 ACL 受限项的数量（以管理员运行可能读到其中一部分）。</summary>
    public long DeniedOther;

    /// <summary>
    /// 因权限被跳过的目录（只留前 <see cref="MaxDeniedSamples"/> 个用于展示）。
    /// 这些目录下的所有内容都不会出现在结果里，因此必须让用户看得见——
    /// 只报一个「权限拒绝 N」的数字，用户无法判断"没扫到的到底是什么"。
    /// </summary>
    public List<string> DeniedPaths { get; } = new(MaxDeniedSamples);

    public const int MaxDeniedSamples = 20;

    /// <summary>把一次目录扫描的跳过记录并入全局统计（调用点必须串行，避免 List 竞态）。</summary>
    internal void MergeDenied(int denied, string? deniedPath)
    {
        Denied += denied;
        if (deniedPath is not null)
        {
            if (ScanCompleteness.IsSystemProtectedPath(deniedPath)) DeniedSystemProtected += denied;
            else DeniedOther += denied;
            if (DeniedPaths.Count < MaxDeniedSamples) DeniedPaths.Add(deniedPath);
        }
    }
}

public sealed record FileIdentity(ulong VolumeSerial, ulong FileId, long LogicalSize, DateTime LastWriteUtc);

/// <summary>双路径扫描：兼容枚举（FindFirstFileEx）/ MFT 快速枚举（FSCTL_ENUM_USN_DATA）。</summary>
public static class ScanEngine
{
    private const uint FileAttributeDirectory = 0x10;
    private const uint FileAttributeReparsePoint = 0x400;



    public static FileIdentity? GetIdentity(string path)
    {
        try
        {
            using var handle = NativeMethods.CreateFile(path, NativeMethods.FILE_READ_ATTRIBUTES,
                NativeMethods.FILE_SHARE_ALL, IntPtr.Zero, NativeMethods.OPEN_EXISTING,
                NativeMethods.FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
            if (handle.IsInvalid || !NativeMethods.GetFileInformationByHandle(handle, out var info)) return null;
            ulong fileId = ((ulong)info.nFileIndexHigh << 32) | info.nFileIndexLow;
            long size = ((long)info.nFileSizeHigh << 32) | info.nFileSizeLow;
            var ft = info.ftLastWriteTime;
            long ticks = ((long)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime;
            var time = ticks > 0 ? DateTime.FromFileTimeUtc(ticks) : DateTime.MinValue;
            return new FileIdentity(info.dwVolumeSerialNumber, fileId, size, time);
        }
        catch { return null; }
    }

    internal static long GetAllocationSize(string path, long logicalSize)
    {
        uint low = NativeMethods.GetCompressedFileSize(path, out uint high);
        if (low == uint.MaxValue && Marshal.GetLastWin32Error() != 0) return logicalSize;
        return ((long)high << 32) | low;
    }

    internal static DateTime FileTimeToUtc(System.Runtime.InteropServices.ComTypes.FILETIME ft)
    {
        long ticks = ((long)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime;
        return ticks > 0 ? DateTime.FromFileTimeUtc(ticks) : DateTime.MinValue;
    }

    /// <summary>
    /// 统一扫描根路径口径。不能用 TrimEnd('\\')：盘根 C:\ 会变成 C:，而 C: 在 Win32 里是
    /// 「驱动器当前目录」——扫整盘时只会枚举当前目录的几十个文件。本方法保留盘根分隔符。
    /// </summary>
    internal static string NormalizeScanRoot(string root) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

    /// <summary>批量预取元数据的大小：一次凑够这么多对象再并行抓取，摊薄调度开销。</summary>
    private const int MetadataBatch = 192;

    /// <summary>低于这个数量并行无收益（调度成本高于 syscall）。</summary>
    private const int MetadataParallelThreshold = 8;

    /// <summary>一轮并行枚举多少个目录。</summary>
    private const int DirectoryBatch = 32;

    private const int DirInfoBufferSize = 1 << 16;

    /// <summary>
    /// 目录枚举缓冲按线程复用：原实现每枚举一个目录就 AllocHGlobal(64KB) 再释放，
    /// 十万级目录意味着十万次 64KB 原生堆分配与首次触碰缺页（每次 16 页）。
    /// 线程池线程在进程生命周期内长期存在，因此复用后分配次数趋近于「线程数」。
    /// 缓冲区大小固定、读写严格限制在返回长度内，互不干扰。
    /// </summary>
    [ThreadStatic] private static IntPtr _dirBuffer;

    private static IntPtr RentDirBuffer()
    {
        if (_dirBuffer == IntPtr.Zero) _dirBuffer = Marshal.AllocHGlobal(DirInfoBufferSize);
        return _dirBuffer;
    }

    /// <summary>
    /// 目录枚举与元数据抓取都是 IO 密集（每次 open 约 100µs），单线程排队时是扫描的主要瓶颈。
    /// 按批并行把等待重叠起来，消费侧（聚合/规则匹配）看到的仍是单线程流。
    /// </summary>
    private static readonly ParallelOptions MetadataParallel = new()
    {
        MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 16) * 2
    };

    private readonly record struct PendingEntry(
        string Path, FileAttributes Attributes, long LogicalSize, DateTime LastWriteUtc, bool IsDirectory);

    /// <summary>单个目录的枚举结果：拒绝数 / 重解析点数 / 元数据失败数。</summary>
    private readonly record struct DirScanResult(int Denied, int Reparse, int AttributeFailures, string? DeniedPath = null);

    /// <summary>
    /// 兼容扫描：任意文件系统可用；不跟随重解析点，权限拒绝只计数。
    /// 主路径用 FileIdBothDirectoryInfo 一次拿整个目录的 (文件ID, 大小, 属性, 时间)——
    /// 逐个 open 取元数据是原实现的主要耗时来源（每个文件 2 次 open）。
    /// </summary>
    public static async IAsyncEnumerable<RawEntry> WalkAsync(
        string root, ScanStats stats, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var fullRoot = NormalizeScanRoot(root);
        // 卷序列号整卷一致：取一次，与目录项里的 64 位文件 ID 组成硬链接去重 / TOCTOU 身份。
        var volumeSerial = GetIdentity(Path.GetPathRoot(fullRoot) ?? fullRoot)?.VolumeSerial ?? 0;

        var stack = new Stack<string>();
        stack.Push(fullRoot);
        var dirs = new List<string>(DirectoryBatch);
        var sinks = new List<RawEntry>[DirectoryBatch];
        var children = new List<string>[DirectoryBatch];
        var results = new DirScanResult[DirectoryBatch];
        for (int i = 0; i < DirectoryBatch; i++)
        {
            sinks[i] = new List<RawEntry>(64);
            children[i] = new List<string>(8);
        }

        int round = 0;
        while (stack.Count > 0)
        {
            if (ct.IsCancellationRequested) yield break;

            dirs.Clear();
            while (stack.Count > 0 && dirs.Count < DirectoryBatch) dirs.Add(stack.Pop());
            int n = dirs.Count;

            // 取消令牌传进枚举内部：WinSxS 这类十万级目录单目录就能枚举十几秒，
            // 只在「每轮 32 个目录之间」检查的话，点取消后还要干等当前批次跑完。
            try
            {
                if (n == 1) results[0] = ScanDirectory(dirs[0], volumeSerial, sinks[0], children[0], ct);
                else Parallel.For(0, n,
                    new ParallelOptions
                    {
                        CancellationToken = ct,
                        MaxDegreeOfParallelism = MetadataParallel.MaxDegreeOfParallelism
                    },
                    i => results[i] = ScanDirectory(dirs[i], volumeSerial, sinks[i], children[i], ct));
            }
            catch (OperationCanceledException)
            {
                yield break;   // 取消是正常退出，保留已扫描部分，不冒泡成「扫描失败」
            }

            // 合并与统计一律串行：顺序稳定、计数无竞态，消费侧也不感知并行。
            for (int i = 0; i < n; i++)
            {
                var result = results[i];
                // 串行合并：DeniedPaths 是共享 List，只在主循环里写，Parallel.For 里不碰。
                stats.MergeDenied(result.Denied, result.DeniedPath);
                stats.ReparsePoints += result.Reparse;
                stats.AttributeFailures += result.AttributeFailures;
                if (result.Denied == 0)
                    foreach (var child in children[i]) stack.Push(child);
                foreach (var entry in sinks[i])
                {
                    if (ct.IsCancellationRequested) yield break;
                    stats.TotalEntries++;
                    stats.TotalBytes += entry.AllocationSize;
                    yield return entry;
                }
                sinks[i].Clear();
                children[i].Clear();
            }

            if (++round % 8 == 0) await Task.Yield();
        }
    }

    /// <summary>
    /// 枚举一个目录。优先走 FileIdBothDirectoryInfo（一次系统调用带回整批元数据）；
    /// 卷不支持该信息类（FAT/exFAT/网络盘）时整目录回退到逐文件读取，绝不少报文件。
    /// </summary>
    private static DirScanResult ScanDirectory(string dir, ulong volumeSerial,
        List<RawEntry> sink, List<string> childDirs, CancellationToken ct)
    {
        using var handle = NativeMethods.CreateFile(dir, NativeMethods.FILE_LIST_DIRECTORY,
            NativeMethods.FILE_SHARE_ALL, IntPtr.Zero, NativeMethods.OPEN_EXISTING,
            NativeMethods.FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        if (handle.IsInvalid) return new DirScanResult(1, 0, 0, dir);

        var buffer = RentDirBuffer();
        bool first = true;
        int reparse = 0;
        while (true)
        {
            if (ct.IsCancellationRequested) return new DirScanResult(0, reparse, 0);
            int infoClass = first
                ? NativeMethods.FileIdBothDirectoryRestartInfo
                : NativeMethods.FileIdBothDirectoryInfo;
            first = false;
            if (!NativeMethods.GetFileInformationByHandleEx(handle, infoClass, buffer, DirInfoBufferSize))
            {
                int error = Marshal.GetLastWin32Error();
                if (error == NativeMethods.ErrorNoMoreFiles) break;
                sink.Clear();
                childDirs.Clear();
                return EnumerateDirectoryLegacy(dir, volumeSerial, sink, childDirs, ct);
            }

            int offset = 0;
            while (true)
            {
                // 读的是非托管缓冲：偏移越界会直接 AV，先做完整性校验再解析。
                if (offset < 0 || offset + 104 > DirInfoBufferSize) break;
                int next = Marshal.ReadInt32(buffer, offset);
                long logical = Marshal.ReadInt64(buffer, offset + 40);
                long allocation = Marshal.ReadInt64(buffer, offset + 48);
                uint attributes = unchecked((uint)Marshal.ReadInt32(buffer, offset + 56));
                int nameBytes = Marshal.ReadInt32(buffer, offset + 60);
                long writeTicks = Marshal.ReadInt64(buffer, offset + 24);
                ulong fileId = unchecked((ulong)Marshal.ReadInt64(buffer, offset + 96));

                if (nameBytes > 0 && offset + 104 + nameBytes <= DirInfoBufferSize)
                {
                    var name = Marshal.PtrToStringUni(IntPtr.Add(buffer, offset + 104), nameBytes / 2);
                    if (!string.IsNullOrEmpty(name) && name != "." && name != "..")
                    {
                        bool isDir = (attributes & FileAttributeDirectory) != 0;
                        bool isReparse = (attributes & FileAttributeReparsePoint) != 0;
                        if (isReparse) reparse++;
                        // 盘根本身带尾随 \（C:\），拼接时不能再加一个；避免 C:\\x 这种双分隔符。
                        var path = string.Concat(dir, dir.EndsWith('\\') ? "" : "\\", name);
                        // 占用口径（与 GetCompressedFileSize 等价，但不必逐文件 open）：
                        // 目录/重解析点按链接自身计 0；目录项里分配为 0 但文件非空（WOF 压缩、
                        // 完全稀疏）时目录项不承载真实占用，回退问系统；其余取「分配 vs 逻辑」较小值。
                        // 反例：直接用 AllocationSize（簇内对齐）会让 1B 文件显示 8B。
                        long alloc;
                        if (isDir || isReparse) alloc = 0;
                        else if (allocation == 0 && logical > 0) alloc = GetAllocationSize(path, logical);
                        else alloc = Math.Min(allocation, logical);
                        sink.Add(new RawEntry(volumeSerial, fileId, path, logical, alloc,
                            writeTicks > 0 ? DateTime.FromFileTimeUtc(writeTicks) : DateTime.MinValue,
                            (FileAttributes)attributes, isDir));
                        if (isDir && !isReparse) childDirs.Add(path);
                    }
                }

                if (next <= 0) break;
                offset += next;
            }
        }
        return new DirScanResult(0, reparse, 0);
    }

    /// <summary>兼容回退：FindFirstFileEx 枚举 + 逐文件 open 取文件 ID/占用（任何文件系统都可用）。</summary>
    private static DirScanResult EnumerateDirectoryLegacy(string dir, ulong volumeSerial,
        List<RawEntry> sink, List<string> childDirs, CancellationToken ct)
    {
        var handle = NativeMethods.FindFirstFileEx(Path.Combine(dir, "*"), 1, out var data, 0, IntPtr.Zero, 2);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return new DirScanResult(1, 0, 0, dir);

        int reparse = 0, failures = 0;
        try
        {
            while (true)
            {
                if (ct.IsCancellationRequested) return new DirScanResult(0, reparse, failures);
                var name = data.cFileName;
                if (name != "." && name != ".." && !string.IsNullOrEmpty(name))
                {
                    var full = Path.Combine(dir, name);
                    uint attrs = data.dwFileAttributes;
                    bool isDir = (attrs & FileAttributeDirectory) != 0;
                    bool isReparse = (attrs & FileAttributeReparsePoint) != 0;
                    if (isReparse) reparse++;

                    var identity = GetIdentity(full);
                    if (identity is null) failures++;
                    long logical = ((long)data.nFileSizeHigh << 32) | data.nFileSizeLow;
                    // 占用口径固定用 GetCompressedFileSize：FILE_STANDARD_INFO.AllocationSize
                    // 对小文件/驻留文件返回簇内对齐值（如 1B→8B），二者不同，不可互换。
                    long alloc = isDir ? 0 : GetAllocationSize(full, logical);
                    sink.Add(new RawEntry(identity?.VolumeSerial ?? volumeSerial, identity?.FileId ?? 0, full,
                        logical, alloc, identity?.LastWriteUtc ?? FileTimeToUtc(data.ftLastWriteTime),
                        (FileAttributes)attrs, isDir));
                    if (isDir && !isReparse) childDirs.Add(full);
                }

                if (!NativeMethods.FindNextFile(handle, out data)) break;
            }
        }
        finally { NativeMethods.FindClose(handle); }
        return new DirScanResult(0, reparse, failures);
    }

    /// <summary>快速扫描自检失败时返回 false，调用方自动降级到兼容扫描并在 UI 标注（文档 4.1.1）。</summary>
    public static bool TryQuickScan(string root, ScanStats stats, CancellationToken ct,
        out IEnumerable<RawEntry>? entries, out string? degradeReason)
    {
        entries = null;
        degradeReason = null;
        try
        {
            var volume = Path.GetPathRoot(Path.GetFullPath(root));
            if (string.IsNullOrEmpty(volume)) { degradeReason = "无法确定卷根"; return false; }

            var drive = new DriveInfo(volume);
            if (!string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
            { degradeReason = $"卷为 {drive.DriveFormat}，非 NTFS"; return false; }
            if (!IsElevated()) { degradeReason = "快速扫描需要管理员权限"; return false; }

            using var volHandle = OpenVolume(volume);
            if (volHandle.IsInvalid) { degradeReason = "无法以管理员权限打开卷（USN Journal 可能被禁用）"; return false; }

            var map = ReadAllRecords(volHandle, stats, ct, out var probeReason);
            if (map is null || map.Count < 8)
            { degradeReason = probeReason ?? "USN Journal 自检未通过"; return false; }

            var rootPrefix = NormalizeScanRoot(root);
            entries = BuildMftEntries(map, volume, rootPrefix, stats, ct);
            return true;
        }
        catch (Exception ex)
        {
            degradeReason = "MFT 自检异常：" + ex.Message;
            return false;
        }
    }

    private static bool IsElevated()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    private static SafeFileHandle OpenVolume(string volume)
    {
        var device = @"\\.\" + volume.TrimEnd('\\');
        return NativeMethods.CreateFile(device, 0x80000000, 0x0001 | 0x0002, IntPtr.Zero,
            NativeMethods.OPEN_EXISTING, 0, IntPtr.Zero);
    }

    internal sealed record UsnEntry(ulong Frn, ulong Parent, string Name, uint Attributes, DateTime LastWriteUtc);
    private const int MaxMftRecords = 2_000_000;

    private static Dictionary<ulong, UsnEntry>? ReadAllRecords(
        SafeFileHandle vol, ScanStats stats, CancellationToken ct, out string? reason)
    {
        reason = null;
        const int bufferSize = 1 << 20;
        var inBuffer = Marshal.AllocHGlobal(24);
        var outBuffer = Marshal.AllocHGlobal(bufferSize);
        var map = new Dictionary<ulong, UsnEntry>();
        ulong start = 0;
        try
        {
            while (true)
            {
                if (ct.IsCancellationRequested) break;
                Marshal.WriteInt64(inBuffer, 0, unchecked((long)start));
                Marshal.WriteInt64(inBuffer, 8, 0);
                Marshal.WriteInt64(inBuffer, 16, long.MaxValue);
                bool ok = NativeMethods.DeviceIoControl(vol, NativeMethods.FSCTL_ENUM_USN_DATA,
                    inBuffer, 24, outBuffer, bufferSize, out uint returned, IntPtr.Zero);
                if (!ok || returned <= 8) { reason = "FSCTL_ENUM_USN_DATA 自检失败（Journal 可能不存在或被禁用）"; break; }

                ulong next = unchecked((ulong)Marshal.ReadInt64(outBuffer, 0));
                int offset = 8;
                int parsed = 0;
                while (offset + 8 <= returned)
                {
                    int recordLength = Marshal.ReadInt32(outBuffer, offset);
                    if (recordLength < 60 || offset + recordLength > returned) break;
                    ushort major = unchecked((ushort)Marshal.ReadInt16(outBuffer, offset + 4));
                    if (major == 2)   // V3 / ReFS 记录不参与，避免过度承诺
                    {
                        ulong frn = unchecked((ulong)Marshal.ReadInt64(outBuffer, offset + 8));
                        ulong parent = unchecked((ulong)Marshal.ReadInt64(outBuffer, offset + 16));
                        uint attrs = unchecked((uint)Marshal.ReadInt32(outBuffer, offset + 52));
                        ushort nameLength = unchecked((ushort)Marshal.ReadInt16(outBuffer, offset + 56));
                        ushort nameOffset = unchecked((ushort)Marshal.ReadInt16(outBuffer, offset + 58));
                        long ft = Marshal.ReadInt64(outBuffer, offset + 32);
                        string name = "";
                        if (nameLength > 0 && nameOffset + nameLength <= recordLength)
                            name = Marshal.PtrToStringUni(IntPtr.Add(outBuffer, offset + nameOffset), nameLength / 2) ?? "";
                        var time = ft > 0 ? DateTime.FromFileTimeUtc(ft) : DateTime.MinValue;
                        if (name.Length > 0) map[frn] = new UsnEntry(frn, parent, name, attrs, time);
                        parsed++;
                    }
                    offset += recordLength;
                }

                if (parsed == 0) { reason = "USN 记录解析失败"; break; }
                if (next == start || next == 0) break;
                start = next;
                if (map.Count > MaxMftRecords) { reason = "MFT 记录数超过内存预算"; break; }
            }
            return map;
        }
        finally
        {
            Marshal.FreeHGlobal(inBuffer);
            Marshal.FreeHGlobal(outBuffer);
        }
    }

    private static IEnumerable<RawEntry> BuildMftEntries(
        Dictionary<ulong, UsnEntry> map, string volume, string rootPrefix, ScanStats stats, CancellationToken ct)
    {
        // 兄弟文件共享祖先：缓存 FRN→完整路径，把逐文件的 O(深度) 祖先遍历摊还到接近 O(1)。
        var pathCache = new Dictionary<ulong, string>();
        var batch = new List<PendingEntry>(MetadataBatch);
        var fetched = new RawEntry[MetadataBatch];
        var failures = new bool[MetadataBatch];

        // 路径重建必须串行（共享 pathCache），元数据抓取按批并行——与兼容扫描同一套口径。
        using var it = map.Values.GetEnumerator();
        while (!ct.IsCancellationRequested)
        {
            batch.Clear();
            while (batch.Count < MetadataBatch && it.MoveNext())
            {
                var entry = it.Current;
                var path = RebuildPath(entry.Frn, map, volume, pathCache);
                if (path is null)
                {
                    stats.UnresolvablePaths++;
                    continue;   // 重建失败：仅统计、不参与删除（文档 4.1.2）
                }
                if (!path.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                batch.Add(new PendingEntry(path, (FileAttributes)entry.Attributes, 0,
                    entry.LastWriteUtc, (entry.Attributes & FileAttributeDirectory) != 0));
            }
            if (batch.Count == 0) yield break;

            int n = batch.Count;
            if (n >= MetadataParallelThreshold)
            {
                Parallel.For(0, n, MetadataParallel, i => FetchMetadataFromIdentity(batch[i], fetched, failures, i));
            }
            else
            {
                for (int i = 0; i < n; i++) FetchMetadataFromIdentity(batch[i], fetched, failures, i);
            }

            for (int i = 0; i < n; i++)
            {
                if (failures[i])
                {
                    stats.AttributeFailures++;
                    continue;   // 无法读取元数据的对象不进候选清单，避免误删
                }
                var raw = fetched[i];
                stats.TotalEntries++;
                stats.TotalBytes += raw.AllocationSize;
                yield return raw;
            }
        }
    }

    /// <summary>MFT 记录本身不带大小，逻辑尺寸与占用都要从文件句柄/路径补取。</summary>
    private static void FetchMetadataFromIdentity(PendingEntry item, RawEntry[] destination, bool[] failures, int index)
    {
        var identity = GetIdentity(item.Path);
        failures[index] = identity is null;
        long logical = identity?.LogicalSize ?? 0;
        long alloc = item.IsDirectory ? 0 : GetAllocationSize(item.Path, logical);
        destination[index] = new RawEntry(
            identity?.VolumeSerial ?? 0, identity?.FileId ?? 0, item.Path,
            logical, alloc, identity?.LastWriteUtc ?? item.LastWriteUtc, item.Attributes, item.IsDirectory);
    }

    /// <summary>
    /// 沿父链重建完整路径；cache 复用祖先结果并回填沿途每个 FRN，
    /// 使同一目录下的 N 个文件不再各自重复走同一段祖先链。
    /// </summary>
    internal static string? RebuildPath(
        ulong frn, Dictionary<ulong, UsnEntry> map, string volume, Dictionary<ulong, string> cache)
    {
        if (cache.TryGetValue(frn, out var cached)) return cached;

        var chain = new List<ulong>();
        ulong current = frn;
        string? resolved = null;
        for (int depth = 0; depth < 256; depth++)
        {
            if (cache.TryGetValue(current, out var ancestorPath)) { resolved = ancestorPath; break; }
            if ((current & 0x0000FFFFFFFFFFFFUL) == 5 || current == 0)   // MFT 根目录索引 5
            { resolved = volume.TrimEnd('\\'); break; }
            if (!map.TryGetValue(current, out var e)) return null;
            chain.Add(current);
            if (e.Parent == current) { resolved = volume.TrimEnd('\\'); break; }
            current = e.Parent;
        }
        if (resolved is null || chain.Count == 0) return null;

        // 由根向叶回填：chain 是「叶→根」顺序，倒序拼接即为每个祖先的完整路径。
        string path = resolved;
        for (int i = chain.Count - 1; i >= 0; i--)
        {
            path = path + "\\" + map[chain[i]].Name;
            cache[chain[i]] = path;
        }
        return cache[frn];
    }
}
