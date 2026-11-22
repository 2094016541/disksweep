using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using DiskSweep.Core.Recommendation;

namespace DiskSweep.App.Converters;

/// <summary>
/// 纯展示层转换器：只把 ViewModel 的标量翻成几何/字符串/可见性，
/// 不改变任何状态，也不引用任何业务服务。
/// </summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Collapsed;
}

/// <summary>
/// 环形进度：把 0–100 的百分比翻成一段圆弧（从 12 点方向顺时针）。
/// 结果 Freeze 后可跨线程复用，也不参与主题换肤（颜色由 Path 的 Stroke 令牌决定）。
/// </summary>
public sealed class PercentToArcConverter : IValueConverter
{
    private const double BoxSize = 112;
    private const double StrokeThickness = 10;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double percent = value switch
        {
            double d => d,
            int i => i,
            _ => 0
        };
        percent = Math.Clamp(percent, 0, 100);

        double radius = (BoxSize - StrokeThickness) / 2;
        var center = new Point(BoxSize / 2, BoxSize / 2);

        if (percent <= 0.05) return Geometry.Empty;

        double sweep = 360 * percent / 100;
        if (percent >= 99.95)
        {
            var circle = new EllipseGeometry(center, radius, radius);
            circle.Freeze();
            return circle;
        }

        var figure = new PathFigure
        {
            StartPoint = PointOn(center, radius, -90),
            IsClosed = false,
            IsFilled = false
        };
        figure.Segments.Add(new ArcSegment(
            PointOn(center, radius, -90 + sweep),
            new Size(radius, radius),
            rotationAngle: 0,
            isLargeArc: sweep > 180,
            sweepDirection: SweepDirection.Clockwise,
            isStroked: true));

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        geometry.Freeze();
        return geometry;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static Point PointOn(Point center, double radius, double degrees)
    {
        double radians = degrees * Math.PI / 180;
        return new Point(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians));
    }
}

/// <summary>
/// 占本次扫描的百分比：把 [分类字节, 本次扫描总字节] 翻成「27%」这样的短文本，
/// 与同一单元格里的长方形进度条共用同一个分母，最大项不再固定是 100%。
/// </summary>
public sealed class PercentOfMaxConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2) return "0%";
        double value = ToDouble(values[0]);
        double max = ToDouble(values[1]);
        if (max <= 0 || value <= 0) return "0%";
        double percent = Math.Clamp(value / max * 100, 0, 100);
        return percent >= 9.95 ? $"{percent:0}%" : $"{percent:0.#}%";
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static double ToDouble(object? value) => value switch
    {
        long l => l,
        int i => i,
        double d => d,
        _ => 0
    };
}

/// <summary>
/// 拆分「目录 · X」/「类型 · X」分类标题：参数 "tag" 返回维度短标签，"name" 返回去前缀后的名称，
/// "isdir" 返回是否为目录（XAML 用 DataTrigger 选色）。供空间分析表格的维度徽章使用，纯展示不改数据。
/// </summary>
public sealed class CategoryDimensionConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string text = value as string ?? "";
        string mode = parameter as string ?? "tag";
        bool isDir = text.StartsWith("目录 ·", StringComparison.Ordinal);
        bool isType = text.StartsWith("类型 ·", StringComparison.Ordinal);

        if (mode == "name")
        {
            if (isDir) return text["目录 ·".Length..].TrimStart();
            if (isType) return text["类型 ·".Length..].TrimStart();
            return text;
        }
        if (mode == "isdir") return isDir;
        return isDir ? "目录" : isType ? "类型" : "";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>把字节数格式化成与列表一致的人类可读体积（复用 Core 的格式规则，避免两套口径）。</summary>
public sealed class ByteSizeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is long bytes ? PlanItem.FormatSize(bytes) : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
