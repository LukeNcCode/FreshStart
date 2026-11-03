using System.Windows;
using System.Windows.Media.Animation;

namespace FreshStart.App.Animation;

/// <summary>
/// 精确复刻 cubic-bezier(0.4, 0.0, 0.2, 1)。
/// WPF 内置的 CubicEase 不是这条曲线，页面切换和侧边栏滑动统一使用本类。
/// </summary>
public sealed class CubicBezierEase : EasingFunctionBase
{
    private const double X1 = 0.4;
    private const double Y1 = 0.0;
    private const double X2 = 0.2;
    private const double Y2 = 1.0;

    protected override double EaseInCore(double normalizedTime)
    {
        if (normalizedTime <= 0) return 0;
        if (normalizedTime >= 1) return 1;

        // 二分求参数 u，使 BezierX(u) == normalizedTime；20 次足够达到亚像素精度
        double low = 0, high = 1, u = normalizedTime;
        for (var i = 0; i < 20; i++)
        {
            var x = Bezier(u, X1, X2);
            if (Math.Abs(x - normalizedTime) < 1e-5) break;
            if (x < normalizedTime) low = u;
            else high = u;
            u = (low + high) / 2;
        }

        return Bezier(u, Y1, Y2);
    }

    private static double Bezier(double t, double p1, double p2)
    {
        var oneMinus = 1 - t;
        return 3 * oneMinus * oneMinus * t * p1 + 3 * oneMinus * t * t * p2 + t * t * t;
    }

    protected override Freezable CreateInstanceCore() => new CubicBezierEase();
}
