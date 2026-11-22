namespace Sweep.Core.Software;

/// <summary>一次目录测量的结果状态；0 字节和「没量」是两件事，必须分开表达。</summary>
public enum MeasureStatus
{
    /// <summary>还没量过（界面上显示登记估值或「未登记」）。</summary>
    NotMeasured,

    /// <summary>测量完成，<see cref="MeasureOutcome.Bytes"/> 就是目录内文件合计（可能确实是 0）。</summary>
    Measured,

    /// <summary>登记的目录不存在——多半是卸载后的残留登记，不是测量失败。</summary>
    DirectoryMissing,

    /// <summary>目录存在但没有读取权限（常见于 ProgramData 下自建目录）。</summary>
    AccessDenied,

    /// <summary>其它读取失败（路径非法、设备未就绪等）。</summary>
    Failed
}

/// <summary>
/// 目录测量的完整结果。以前只返回 long，于是「量到 0 字节」和「没量成」在界面上都是 0，
/// 用户看到的「未登记」既可能是没测过、也可能是目录已经不存在。这里把状态一起带出来。
/// </summary>
public sealed record MeasureOutcome(long Bytes, MeasureStatus Status)
{
    public static MeasureOutcome Missing { get; } = new(0, MeasureStatus.DirectoryMissing);
    public static MeasureOutcome Denied { get; } = new(0, MeasureStatus.AccessDenied);
    public static MeasureOutcome Failed { get; } = new(0, MeasureStatus.Failed);

    public bool IsMeasured => Status == MeasureStatus.Measured;

    /// <summary>界面上的简短说明；测量成功时为空串。</summary>
    public string ProblemText => Status switch
    {
        MeasureStatus.DirectoryMissing => "登记的安装目录不存在，可能是卸载残留；可用「扫描残留」核对",
        MeasureStatus.AccessDenied => "目录存在但没有读取权限，可用管理员身份重扫",
        MeasureStatus.Failed => "目录读取失败（路径非法或设备未就绪）",
        _ => ""
    };
}
