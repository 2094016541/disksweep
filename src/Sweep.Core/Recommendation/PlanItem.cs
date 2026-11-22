using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Sweep.Core.Recommendation;

/// <summary>UI 直接绑定的最小单元；IsSelected 的默认值一律由闸门输出决定。</summary>
public sealed class PlanItem : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private bool _isSelected;
    private bool _isExpanded;

    public required string Path { get; init; }
    public required Recommendation Recommendation { get; set; }
    public required string Category { get; init; }
    public required long AllocationSize { get; init; }
    public required ulong VolumeSerial { get; init; }
    public required ulong FileId { get; init; }
    public required bool IsDirectory { get; init; }
    public required string Handling { get; init; }
    public required bool RequiresAdmin { get; init; }
    public required string Description { get; init; }
    public required string ReclaimNote { get; init; }
    public required bool IsSystemOperation { get; init; }
    public required bool LowReclaimEstimate { get; init; }
    public required int AgeDays { get; init; }

    public bool DefaultSelected { get; init; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (_isExpanded == value) return; _isExpanded = value; OnPropertyChanged(); }
    }

    public RiskLevel Risk => Recommendation.Risk;
    public RecommendationAction Action => Recommendation.Action;
    public RecommendationSource Source => Recommendation.Source;
    public bool RequiresExplicitConfirm => Recommendation.RequiresExplicitConfirm;
    public string SizeText => FormatSize(AllocationSize);

    public static string FormatSize(long bytes)
    {
        if (bytes < 1024) return bytes + " B";
        string[] units = { "KB", "MB", "GB", "TB" };
        double value = bytes;
        int i = -1;
        while (value >= 1024 && i < units.Length - 1) { value /= 1024; i++; }
        return $"{value:0.#} {units[i]}";
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
