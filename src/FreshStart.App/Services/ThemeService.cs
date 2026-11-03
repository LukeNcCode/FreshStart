using System.Windows;
using System.Windows.Media;
using FreshStart.Core;
using FreshStart.Core.Abstractions;
using FreshStart.Core.Settings;
using FreshStart.Core.UI;
using FreshStart.Modules.Visual;

namespace FreshStart.App.Services;

/// <summary>主题切换：只替换 App.Resources.MergedDictionaries[0]，所有页面用 DynamicResource 自动更新。</summary>
public sealed class ThemeService : IThemeService
{
    private readonly SettingsService _settings;
    private string _current = "Light";
    private string _accent;

    public ThemeService(SettingsService settings)
    {
        _settings = settings;
        _current = settings.Current.Theme;
        _accent = string.IsNullOrWhiteSpace(settings.Current.AccentColor) ? "#007AFF" : settings.Current.AccentColor;
    }

    public bool IsDark => string.Equals(_current, "Dark", StringComparison.OrdinalIgnoreCase);

    public string CurrentTheme => _current;

    public event EventHandler? ThemeChanged;

    /// <summary>启动时在创建窗口之前调用。</summary>
    public void ApplyInitial()
    {
        ApplyCore(_current, raiseEvent: false);
        ApplyAccent((Color)ColorConverter.ConvertFromString(_accent));
    }

    public Task SetThemeAsync(string theme, CancellationToken ct = default)
    {
        var normalized = string.Equals(theme, "Dark", StringComparison.OrdinalIgnoreCase) ? "Dark" : "Light";
        if (normalized == _current) return Task.CompletedTask;
        ApplyCore(normalized, raiseEvent: true);
        return Task.CompletedTask;
    }

    public void SetAccent(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return;
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            _accent = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
            _settings.Current.AccentColor = _accent;
            _settings.SaveNow();
            ApplyAccent(color);
        }
        catch (Exception ex)
        {
            Log.Warn($"设置主题色失败: {hex} - {ex.Message}");
        }
    }

    private void ApplyAccent(Color color)
    {
        var app = Application.Current;
        if (app is null) return;

        var brush = new SolidColorBrush(color);
        brush.Freeze();
        var tint = new SolidColorBrush(Color.FromArgb(0x22, color.R, color.G, color.B));
        tint.Freeze();
        var gradientEnd = Color.FromRgb(
            (byte)Math.Min(255, color.R + 42),
            (byte)Math.Min(255, color.G + 78),
            (byte)Math.Min(255, color.B + 30));
        var gradient = new LinearGradientBrush(color, gradientEnd, new Point(0, 0), new Point(1, 1));
        gradient.Freeze();
        var glow = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.5, 0.5),
            Center = new Point(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5
        };
        glow.GradientStops.Add(new GradientStop(Color.FromArgb(0x52, color.R, color.G, color.B), 0));
        glow.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, color.R, color.G, color.B), 1));
        glow.Freeze();

        app.Resources["AccentBrush"] = brush;
        app.Resources["AccentTintBrush"] = tint;
        app.Resources["AccentColor"] = color;
        app.Resources["AccentTintColor"] = Color.FromArgb(0x14, color.R, color.G, color.B);
        app.Resources["AccentGradientBrush"] = gradient;
        app.Resources["AccentGlowBrush"] = glow;
    }

    private void ApplyCore(string theme, bool raiseEvent)
    {
        var app = Application.Current;
        if (app is null) return;

        try
        {
            var dictionary = new ResourceDictionary
            {
                Source = new Uri($"Themes/{theme}.xaml", UriKind.Relative)
            };

            var merged = app.Resources.MergedDictionaries;
            if (merged.Count == 0) merged.Add(dictionary);
            else merged[0] = dictionary;

            _current = theme;
            MotionPreferences.SetEnabled(_settings.Current.AnimationsEnabled);
            ApplyAccent((Color)ColorConverter.ConvertFromString(_accent));
            Log.Info($"主题已切换: {theme}");

            if (raiseEvent) ThemeChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Log.Error($"切换主题失败: {theme}", ex);
        }
    }
}

/// <summary>动效开关，写入 Core 的全局偏好，供模块与页面共享。</summary>
public sealed class MotionService : IMotionService
{
    public bool Enabled => MotionPreferences.AnimationsEnabled;

    public void SetEnabled(bool enabled) => MotionPreferences.SetEnabled(enabled);
}

/// <summary>标题栏右侧按钮使用的小图标。</summary>
public static class ShellIcons
{
    public static string Theme(bool dark) => AppIcons.Get(dark ? "sun" : "moon");
}
