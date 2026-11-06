using System.Windows;
using System.Windows.Threading;
using FreshStart.App.Controls;
using FreshStart.Core.Abstractions;
using FreshStart.Core.Settings;

namespace FreshStart.App.Services;

/// <summary>独立任务控制台窗口的生命周期与设置持久化。</summary>
public sealed class ActivityConsoleWindowManager : IActivityConsoleHost
{
    private readonly ActivityConsoleService _console;
    private readonly SettingsService _settings;
    private readonly Dispatcher _dispatcher;
    private ActivityConsoleWindow? _window;
    private bool _enabled;

    public ActivityConsoleWindowManager(
        ActivityConsoleService console,
        SettingsService settings,
        Dispatcher dispatcher)
    {
        _console = console;
        _settings = settings;
        _dispatcher = dispatcher;
        _console.ActivityStarted += (_, _) =>
        {
            if (_enabled) ShowWindow();
        };
    }

    public bool IsEnabled => _enabled;

    public event EventHandler<bool>? EnabledChanged;

    public void ApplyInitial(bool enabled)
    {
        _enabled = enabled;
        _console.SetCollecting(enabled);
        if (enabled) ShowWindow();
        EnabledChanged?.Invoke(this, enabled);
    }

    public void SetEnabled(bool enabled)
    {
        if (_enabled == enabled)
        {
            if (enabled) ShowWindow();
            else HideWindow();
            return;
        }

        _enabled = enabled;
        _console.SetCollecting(enabled);
        _settings.Current.TaskConsoleEnabled = enabled;
        _settings.SaveNow();
        if (enabled) ShowWindow();
        else HideWindow();
        EnabledChanged?.Invoke(this, enabled);
    }

    private void ShowWindow()
    {
        if (!_enabled) return;
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(ShowWindow, DispatcherPriority.Normal);
            return;
        }

        if (_window is null)
        {
            _window = new ActivityConsoleWindow(_console);
            _window.Closed += (_, _) =>
            {
                _window = null;
                if (!_enabled) return;
                _enabled = false;
                _console.SetCollecting(false);
                _settings.Current.TaskConsoleEnabled = false;
                _settings.SaveNow();
                EnabledChanged?.Invoke(this, false);
            };
        }
        if (!_window.IsVisible) _window.Show();
        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }
        _window.Activate();
    }

    private void HideWindow()
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(HideWindow, DispatcherPriority.Normal);
            return;
        }
        _window?.Hide();
    }
}
