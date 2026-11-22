using System.Globalization;
using DiskSweep.App.Converters;
using Xunit;

namespace DiskSweep.ViewModelTests;

/// <summary>空间分析表格显示优化：维度徽章拆分 + 极小占比文本的可见性口径。</summary>
public class AnalysisDisplayTests
{
    [Fact]
    public void CategoryDimension_SplitsDirectoryAndType()
    {
        var conv = new CategoryDimensionConverter();

        Assert.Equal("目录", conv.Convert("目录 · Windows", typeof(string), "tag", CultureInfo.InvariantCulture));
        Assert.Equal("类型", conv.Convert("类型 · .sys", typeof(string), "tag", CultureInfo.InvariantCulture));
        Assert.Equal("", conv.Convert("无前缀", typeof(string), "tag", CultureInfo.InvariantCulture));

        Assert.Equal("Windows", conv.Convert("目录 · Windows", typeof(string), "name", CultureInfo.InvariantCulture));
        Assert.Equal(".sys", conv.Convert("类型 · .sys", typeof(string), "name", CultureInfo.InvariantCulture));
        Assert.Equal("无前缀", conv.Convert("无前缀", typeof(string), "name", CultureInfo.InvariantCulture));

        Assert.Equal(true, conv.Convert("目录 · Windows", typeof(bool), "isdir", CultureInfo.InvariantCulture));
        Assert.Equal(false, conv.Convert("类型 · .sys", typeof(bool), "isdir", CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData(1L, 1000L, "0.1%")]   // 极小占比仍显示可读文本，进度条靠 MinWidth=3 保证可见
    [InlineData(2L, 1000L, "0.2%")]
    [InlineData(140L, 10000L, "1.4%")]
    [InlineData(80L, 100L, "80%")]
    [InlineData(0L, 100L, "0%")]
    public void PercentOfMax_KeepsSmallValuesReadable(long value, long max, string expected)
    {
        var conv = new PercentOfMaxConverter();
        Assert.Equal(expected, conv.Convert(new object[] { value, max }, typeof(string), null, CultureInfo.InvariantCulture));
    }
}
