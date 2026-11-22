using Rec = Sweep.Core.Recommendation.Recommendation;

namespace Sweep.Core.Safety;

/// <summary>
/// 全流程唯一出口。顺序不可调换：规范化 → 白名单 → critical → 受保护路径 → 跨用户 → AI 收敛。
/// 文档 4.4 / 8.1 红线 4、8。
/// </summary>
public static class SafetyGate
{
    public static Rec Apply(Rec r, SafetyContext ctx)
    {
        // 0. 规范化必须先于一切匹配，否则白名单与受保护路径可被畸形路径绕过
        var path = PathNormalizer.Normalize(r.Path);
        if (path is null)
            return r with { Action = RecommendationAction.Keep, Reason = "路径规范化失败，出于安全考虑保留" };
        r = r with { Path = path };

        // 1. 白名单否决权最高：任何置信度、任何来源都不能覆盖
        if (ctx.Whitelist.Matches(path))
            return r with { Action = RecommendationAction.Keep, Reason = "命中用户白名单/保护目录" };

        // 排除扩展名（.pst / .sqlite 等用户数据）
        var ext = Path.GetExtension(path);
        if (ext.Length > 0 && ctx.ExcludeExtensions.Any(e =>
                string.Equals(e, ext, StringComparison.OrdinalIgnoreCase)))
            return r with { Action = RecommendationAction.Keep, Reason = "扩展名在排除列表中" };

        // 2. critical 永远转人工，不允许出现在自动清理列表
        if (r.Risk == RiskLevel.Critical)
            return r with { Action = RecommendationAction.Review, Reason = "极高风险项，需人工复核：" + r.Reason };

        // 3. 受保护路径（规范化后前缀匹配，大小写不敏感）
        if (ProtectedPaths.Contains(path, ctx))
            return r with { Action = RecommendationAction.Keep, Reason = "系统或用户受保护路径" };

        // 4. 跨用户目录默认禁止：Helper 提权后也不得删除其他用户的文件
        if (!ProtectedPaths.Owns(path, ctx.CurrentUserProfile))
            return r with { Action = RecommendationAction.Keep, Reason = "属于其他用户目录" };

        // 5. AI 来源一律只作建议：低置信转人工，高置信也必须逐项显式确认
        if (r.Source == RecommendationSource.Ai)
            return r with
            {
                Action = r.Confidence < 0.85 ? RecommendationAction.Review : r.Action,
                RequiresExplicitConfirm = true,
                Reason = "AI 建议：" + r.Reason
            };

        return r;
    }

    /// <summary>
    /// 系统接口操作（DISM / powercfg / VSS / 清空回收站）的专用闸门分支：
    /// 不是删除文件路径，故不做受保护路径前缀判定，但强制 Review + 显式确认 + 高风险，且拒绝 AI 来源。
    /// </summary>
    public static Rec ApplySystemOperation(Rec r, SafetyContext ctx)
    {
        var path = PathNormalizer.NormalizePattern(r.Path);
        r = r with { Path = path };
        if (r.Source == RecommendationSource.Ai)
            return r with { Action = RecommendationAction.Keep, Reason = "AI 不得触发系统维护操作" };
        return r with
        {
            Action = RecommendationAction.Review,
            Risk = r.Risk < RiskLevel.High ? RiskLevel.High : r.Risk,
            RequiresExplicitConfirm = true,
            Reason = "系统接口操作（不可回退）：" + r.Reason
        };
    }

    /// <summary>静默清理准入：只接受 Rule + Low，AI 建议永不进入（8.1 红线 7）。</summary>
    public static bool IsSilentCleanupAllowed(Rec r) =>
        r.Source == RecommendationSource.Rule &&
        r.Risk == RiskLevel.Low &&
        r.Action == RecommendationAction.Delete &&
        !r.RequiresExplicitConfirm;
}
