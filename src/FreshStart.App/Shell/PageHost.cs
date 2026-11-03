using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using FreshStart.App.Animation;
using FreshStart.Core.UI;

namespace FreshStart.App.Shell;

/// <summary>
/// 页面容器：切换时淡入 + 上移 8px（200ms）。首帧不播放动画，避免启动看起来"闪一下"。
/// </summary>
public sealed class PageHost : ContentControl
{
    private bool _hasShownContent;

    public PageHost()
    {
        ClipToBounds = true;
    }

    protected override void OnContentChanged(object oldContent, object newContent)
    {
        base.OnContentChanged(oldContent, newContent);
        if (newContent is null) return;

        var animate = _hasShownContent && MotionPreferences.AnimationsEnabled;
        _hasShownContent = true;

        if (!animate)
        {
            BeginAnimation(OpacityProperty, null);
            Opacity = 1;
            RenderTransform = Transform.Identity;
            return;
        }

        var duration = MotionPreferences.Duration(200);
        Opacity = 0;
        var translate = new TranslateTransform(0, 8);
        RenderTransform = translate;
        var easing = new CubicBezierEase();

        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, new Duration(duration))
        {
            EasingFunction = easing
        });
        translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, new Duration(duration))
        {
            EasingFunction = easing
        });
    }
}
