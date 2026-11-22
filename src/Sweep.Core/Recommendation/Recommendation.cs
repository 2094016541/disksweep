namespace Sweep.Core.Recommendation;

/// <summary>规则、AI、用户手选汇入同一类型，只以 Source 区分（文档 4.3）。</summary>
public sealed record Recommendation(
    string Path,
    RecommendationSource Source,
    RecommendationAction Action,
    RiskLevel Risk,
    double Confidence,
    string Reason,
    string? RuleId,
    bool RequiresExplicitConfirm = false);
