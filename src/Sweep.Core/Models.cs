using Sweep.Core.Safety;

namespace Sweep.Core;

public enum ScanMode { Mft, Walk }
public enum RiskLevel { Low = 0, Medium = 1, High = 2, Critical = 3 }
public enum RecommendationSource { Rule, Ai, User }
public enum RecommendationAction { Delete, Keep, Review }

/// <summary>扫描产出的一条原始记录。volume/fileId 用于硬链接去重与 TOCTOU 防护。</summary>
public sealed record RawEntry(
    ulong VolumeSerial,
    ulong FileId,
    string Path,
    long LogicalSize,
    long AllocationSize,
    DateTime LastWriteUtc,
    FileAttributes Attributes,
    bool IsDirectory);

public sealed record ScanSnapshot(
    string SnapshotId,
    string RootPath,
    DateTime StartedUtc,
    DateTime? FinishedUtc,
    ScanMode Mode,
    long TotalFiles,
    long TotalBytes,
    long DeniedCount);

public sealed record CandidateItem(
    string Path,
    string Category,
    long AllocationSize,
    long LogicalSize,
    int AgeDays,
    string Extension,
    FileAttributes Attributes,
    ulong VolumeSerial,
    ulong FileId,
    bool IsDirectory,
    bool IsAlias = false);

public sealed record DiskUsage(string Root, ulong TotalBytes, ulong FreeBytes);

/// <summary>一个已就绪卷的展示信息；Root 为盘根（如 C:\），Label 为卷标或设备类型的兜底文案。</summary>
public sealed record DriveDescriptor(
    string Root,
    string Label,
    string TypeLabel,
    ulong TotalBytes,
    ulong FreeBytes);

public sealed record DeleteFailure(string Path, int HResult, string Reason, string? BlockingProcess);

public sealed record DeleteOutcome(
    bool Committed,
    string SessionId,
    int Succeeded,
    int Failed,
    long ReclaimedBytes,
    string Message,
    IReadOnlyList<DeleteFailure> Failures,
    /// <summary>本次真正删除成功的原始路径，供 UI 精确从清单移除；失败/被拦项不在其中。</summary>
    IReadOnlyList<string>? SucceededPaths = null);

public sealed record CleanupRequest(
    string Path,
    long AllocationSize,
    ulong VolumeSerial,
    ulong FileId,
    RiskLevel Risk,
    string? RuleId,
    RecommendationSource Source,
    bool RequiresExplicitConfirm = false);

public sealed class SafetyContext
{
    public required string CurrentUserProfile { get; init; }
    public required string WindowsDirectory { get; init; }
    public required string SystemDrive { get; init; }
    public required Whitelist Whitelist { get; init; }
    public IReadOnlyList<string> ProtectedExemptions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ExcludeExtensions { get; init; } = Array.Empty<string>();

    public static SafetyContext Create(Whitelist whitelist, IReadOnlyList<string>? excludeExtensions = null)
    {
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrEmpty(win)) win = @"C:\Windows";
        var drive = Path.GetPathRoot(win) ?? @"C:\";
        return new SafetyContext
        {
            CurrentUserProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            WindowsDirectory = win.TrimEnd('\\'),
            SystemDrive = drive.TrimEnd('\\'),
            Whitelist = whitelist,
            ExcludeExtensions = excludeExtensions ?? Array.Empty<string>(),
            // 仅允许系统 Temp 与更新下载缓存这类明确可清理的子目录
            ProtectedExemptions = new[]
            {
                Path.Combine(win, "Temp"),
                Path.Combine(win, "SoftwareDistribution", "Download"),
                Path.Combine(win, "Prefetch")
            }
        };
    }
}
