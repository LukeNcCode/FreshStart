using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using FreshStart.App.Animation;
using FreshStart.Core.UI;

namespace FreshStart.App.Controls;

public partial class ToastHost
{
    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource), typeof(IEnumerable), typeof(ToastHost),
        new PropertyMetadata(null, (d, e) => ((ToastHost)d).ItemsHost.ItemsSource = e.NewValue as IEnumerable));

    public ToastHost()
    {
        InitializeComponent();
    }

    public IEnumerable? ItemsSource
    {
        get => (IEnumerable?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    private void Toast_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Border border) return;
        var duration = MotionPreferences.Duration(200);
        if (duration <= TimeSpan.Zero)
        {
            border.Opacity = 1;
            return;
        }

        border.Opacity = 0;
        var translate = new TranslateTransform(18, 0);
        border.RenderTransform = translate;
        var easing = new CubicBezierEase();
        border.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, new Duration(duration)) { EasingFunction = easing });
        translate.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(18, 0, new Duration(duration)) { EasingFunction = easing });
    }
}
