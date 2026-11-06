using System.Windows;
using System.Windows.Media.Animation;

namespace FreshStart.App.Animation;

/// <summary>
/// 按 CSS cubic-bezier 控制点实现缓动。默认曲线为 (0.4, 0, 0.2, 1)。
/// WPF 内置的 CubicEase 无法直接表达这些控制点。
/// </summary>
public sealed class CubicBezierEase : EasingFunctionBase
{
    private readonly double _x1;
    private readonly double _y1;
    private readonly double _x2;
    private readonly double _y2;

    public CubicBezierEase(
        double x1 = 0.4,
        double y1 = 0.0,
        double x2 = 0.2,
        double y2 = 1.0)
    {
        _x1 = x1;
        _y1 = y1;
        _x2 = x2;
        _y2 = y2;
    }

    protected override double EaseInCore(double normalizedTime)
    {
        if (normalizedTime <= 0) return 0;
        if (normalizedTime >= 1) return 1;

        // 二分求参数 u，使 BezierX(u) == normalizedTime；20 次足够达到亚像素精度
        double low = 0, high = 1, u = normalizedTime;
        for (var i = 0; i < 20; i++)
        {
            var x = Bezier(u, _x1, _x2);
            if (Math.Abs(x - normalizedTime) < 1e-5) break;
            if (x < normalizedTime) low = u;
            else high = u;
            u = (low + high) / 2;
        }

        return Bezier(u, _y1, _y2);
    }

    private static double Bezier(double t, double p1, double p2)
    {
        var oneMinus = 1 - t;
        return 3 * oneMinus * oneMinus * t * p1 + 3 * oneMinus * t * t * p2 + t * t * t;
    }

    protected override Freezable CreateInstanceCore()
        => new CubicBezierEase(_x1, _y1, _x2, _y2);
}
