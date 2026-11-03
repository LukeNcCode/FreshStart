using System.Windows;
using System.Windows.Media;

namespace FreshStart.Modules.Controls;

/// <summary>
/// 自绘迷你折线图。只使用 DrawingContext，不创建视觉树，
/// 每帧只画一条折线和一块填充，适合 1s 刷新的资源曲线。
/// </summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IEnumerable<double>), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GridBrushProperty = DependencyProperty.Register(
        nameof(GridBrush), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromArgb(20, 0, 0, 0)),
            FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AutoScaleProperty = DependencyProperty.Register(
        nameof(AutoScale), typeof(bool), typeof(Sparkline),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(Sparkline),
        new FrameworkPropertyMetadata(100d, FrameworkPropertyMetadataOptions.AffectsRender));

    public IEnumerable<double>? Values
    {
        get => (IEnumerable<double>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public Brush? Stroke
    {
        get => (Brush?)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public Brush? Fill
    {
        get => (Brush?)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public Brush? GridBrush
    {
        get => (Brush?)GetValue(GridBrushProperty);
        set => SetValue(GridBrushProperty, value);
    }

    public bool AutoScale
    {
        get => (bool)GetValue(AutoScaleProperty);
        set => SetValue(AutoScaleProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 2 || height <= 2) return;

        var gridPen = new Pen(GridBrush ?? Brushes.Transparent, 1);
        for (var i = 1; i < 4; i++)
        {
            var y = height * i / 4.0;
            dc.DrawLine(gridPen, new Point(0, y), new Point(width, y));
        }

        var values = Values?.ToList() ?? new List<double>();
        if (values.Count < 2) return;

        var max = AutoScale ? values.Max() : Maximum;
        if (double.IsNaN(max) || max <= 0) max = 1;

        var points = new List<Point>(values.Count);
        for (var i = 0; i < values.Count; i++)
        {
            var x = values.Count == 1 ? 0 : width * i / (values.Count - 1);
            var normalized = Math.Clamp(values[i] / max, 0, 1);
            points.Add(new Point(x, height - normalized * height));
        }

        var fill = Fill;
        if (fill is not null)
        {
            var fillGeometry = new StreamGeometry();
            using (var ctx = fillGeometry.Open())
            {
                ctx.BeginFigure(new Point(0, height), true, true);
                ctx.LineTo(points[0], true, false);
                foreach (var point in points.Skip(1)) ctx.LineTo(point, true, false);
                ctx.LineTo(new Point(width, height), true, false);
            }
            fillGeometry.Freeze();
            dc.DrawGeometry(fill, null, fillGeometry);
        }

        var lineGeometry = new StreamGeometry();
        using (var ctx = lineGeometry.Open())
        {
            ctx.BeginFigure(points[0], false, false);
            foreach (var point in points.Skip(1)) ctx.LineTo(point, true, false);
        }
        lineGeometry.Freeze();

        var pen = new Pen(Stroke ?? Brushes.DodgerBlue, 1.6)
        {
            LineJoin = PenLineJoin.Round,
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        pen.Freeze();
        dc.DrawGeometry(null, pen, lineGeometry);
    }
}
