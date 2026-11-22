using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiskSweep.App.Services;
using DiskSweep.Core;
using DiskSweep.Core.Executors;
using DiskSweep.Core.Recommendation;

namespace DiskSweep.App.ViewModels;

public sealed record PreviewRow(string Path, string Size, string Risk, string Reason, bool Highlight);

/// <summary>
/// 清理前逐项预览。永久删除的确认词校验、AI 建议禁用规则都在这里，
/// View 只做绑定与关闭窗口的桥接（8.1 红线的 UI 侧校验点）。
/// </summary>
public sealed partial class PreviewViewModel : ObservableObject
{
    private readonly IDialogService _dialogs;
    private readonly IWindowController _window;
    private readonly IReadOnlyList<PlanItem> _items;

    public IReadOnlyList<PreviewRow> Rows { get; }
    public string ConsequencesText { get; }
    public PreviewDecision? Result { get; private set; }

    [ObservableProperty] private bool permanent;
    [ObservableProperty] private string confirmWord = "";
    [ObservableProperty] private bool confirmEnabled;
    [ObservableProperty] private string hintText = "留空即使用默认的回收站模式（可恢复）";
    [ObservableProperty] private string confirmCaption = "确认清理";

    public PreviewViewModel(
        IDialogService dialogs,
        IWindowController window,
        IReadOnlyList<PlanItem> items,
        DiskSweep.Core.Storage.AppSettings settings)
    {
        _dialogs = dialogs;
        _window = window;
        _items = items;
        _ = settings;   // 删除模式恒为回收站；预览只展示，不持久化开关

        Rows = items.Select(p =>
        {
            bool system = p.IsSystemOperation;
            bool ai = p.Source == RecommendationSource.Ai;
            string consequence = system && SystemCleanup.Consequences.TryGetValue(p.Handling, out var note)
                ? "不可回退：" + note
                : p.Action == RecommendationAction.Review
                    ? "需人工复核：" + p.Recommendation.Reason
                    : p.Recommendation.Reason;
            return new PreviewRow(p.Path, p.SizeText, p.Risk.ToString(), consequence,
                system || ai || p.LowReclaimEstimate);
        }).ToList();

        var critical = items.Where(p => p.IsSystemOperation &&
                                        SystemCleanup.Consequences.ContainsKey(p.Handling)).ToList();
        ConsequencesText = critical.Count == 0
            ? $"共 {items.Count} 项 · 预估可释放 {PlanItem.FormatSize(items.Sum(i => i.AllocationSize))}（估算值，非承诺值）。持久化删除模式恒为「回收站」。"
            : $"含 {critical.Count} 项系统维护操作，执行后不可回退，请确认已阅读上方逐项后果说明。";
    }

    partial void OnPermanentChanged(bool value)
    {
        ConfirmEnabled = value;
        HintText = value
            ? $"请输入确认词「{DeleteExecutor.PermanentConfirmWord}」；永久删除不可恢复，且此模式下 AI 建议一律禁用。"
            : "留空即使用默认的回收站模式（可恢复）";
        ConfirmCaption = value ? "确认永久删除" : "确认清理";
    }

    [RelayCommand]
    private void Confirm()
    {
        var word = Permanent ? ConfirmWord.Trim() : null;
        if (Permanent && word != DeleteExecutor.PermanentConfirmWord)
        {
            _dialogs.Notify($"永久删除必须输入确认词「{DeleteExecutor.PermanentConfirmWord}」。本次未执行任何删除。",
                warning: true);
            return;
        }
        if (Permanent && _items.Any(p => p.Source == RecommendationSource.Ai))
        {
            _dialogs.Notify("永久删除模式下 AI 建议一律禁用，请取消这些选项。", warning: true);
            return;
        }
        Result = new PreviewDecision(true, Permanent, word);
        _window.Close(true);
    }

    [RelayCommand]
    private void Cancel()
    {
        Result = PreviewDecision.Cancelled;
        _window.Close(false);
    }
}
