using DiskSweep.Core;
using DiskSweep.Core.Scanning;
using Xunit;

namespace DiskSweep.Tests;

/// <summary>文档 4.7：磁盘健康时静默清理绝不执行；容量未知时保守跳过。</summary>
public class SchedulePolicyTests
{
    private static DiskUsage Usage(long total, long free) => new(@"C:\", (ulong)total, (ulong)free);

    [Theory]
    [InlineData(1000, 900, 15, false)]   // 剩余 90%，远高于阈值
    [InlineData(1000, 150, 15, false)]   // 恰好等于阈值，不触发
    [InlineData(1000, 149, 15, true)]    // 低于阈值才触发
    [InlineData(1000, 0, 15, true)]      // 磁盘已满
    public void OnlyRunsBelowFreeSpaceThreshold(long total, long free, int threshold, bool expected) =>
        Assert.Equal(expected, SchedulePolicy.ShouldRun(Usage(total, free), threshold));

    [Fact]
    public void UnknownCapacity_NeverTriggers() =>
        Assert.False(SchedulePolicy.ShouldRun(Usage(0, 0), 15));

    [Fact]
    public void InvalidThreshold_IsClamped()
    {
        Assert.False(SchedulePolicy.ShouldRun(Usage(1000, 500), -10));   // 阈值下限 0
        Assert.True(SchedulePolicy.ShouldRun(Usage(1000, 500), 200));    // 阈值上限 100
    }

    [Fact]
    public void InconsistentCapacity_NeverTriggers() =>
        Assert.False(SchedulePolicy.ShouldRun(Usage(100, 200), 15));
}
