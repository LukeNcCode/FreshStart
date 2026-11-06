using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using FreshStart.Core.Diagnostics;

namespace FreshStart.App.Services;

public sealed class ActivityConsoleLine
{
    public string Time { get; init; } = string.Empty;
    public string Prefix { get; init; } = "·";
    public string Message { get; init; } = string.Empty;
    public string Tone { get; init; } = "Normal";
}

/// <summary>
/// 独立任务控制台的数据源。所有写操作通过 OperationRunner 汇入，
/// 环境克隆等直接任务通过 Begin/Report/Complete 汇入。
/// </summary>
public sealed partial class ActivityConsoleService : ObservableObject
{
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _heartbeat;
    private DateTimeOffset _startedAt;
    private DateTimeOffset _lastActivityAt;
    private string _lastMessage = string.Empty;
    private bool _collecting = true;

    public ActivityConsoleService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _heartbeat = new DispatcherTimer(
            TimeSpan.FromSeconds(1), DispatcherPriority.Background,
            (_, _) => Tick(), dispatcher);
        _heartbeat.Stop();
        NativeCommandTrace.Published += (_, activity) => PublishNative(activity);
    }

    public ObservableCollection<ActivityConsoleLine> Lines { get; } = new();

    public event EventHandler? ActivityStarted;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private string _currentTitle = "FRESHSTART TASK CONSOLE";

    [ObservableProperty]
    private string _currentMessage = "等待任务启动";

    [ObservableProperty]
    private double _progressPercent;

    [ObservableProperty]
    private string _elapsedText = "00:00";

    [ObservableProperty]
    private string _statusText = "待命";

    [ObservableProperty]
    private string _statusTone = "Idle";

    public void SetCollecting(bool collecting)
    {
        _collecting = collecting;
        if (!collecting)
        {
            IsRunning = false;
            StatusText = "已关闭";
            StatusTone = "Idle";
            _heartbeat.Stop();
        }
        else if (!IsRunning)
        {
            StatusText = "待命";
            CurrentMessage = "等待任务启动";
        }
    }

    private void PublishNative(NativeCommandTraceEvent activity)
    {
        if (!_collecting) return;
        RunOnUi(() =>
        {
            if (!activity.Completed)
            {
                if (!IsRunning) BeginCore("NATIVE COMMANDS", activity.Command);
                else AddLine("$", activity.Command, "Accent");
                return;
            }

            if (!IsRunning) BeginCore("NATIVE COMMANDS", activity.Command);
            foreach (var line in NormalizeOutput(activity.Output))
            {
                AddLine(" ", line, "Muted");
            }
            AddLine(
                activity.ExitCode == 0 ? "✓" : "!",
                $"exit {activity.ExitCode?.ToString() ?? "?"} · {activity.DurationMs:0} ms",
                activity.ExitCode == 0 ? "Success" : "Error");
            IsRunning = false;
            ProgressPercent = 100;
            HasError = activity.ExitCode != 0;
            StatusText = activity.ExitCode == 0 ? "完成" : "失败";
            StatusTone = activity.ExitCode == 0 ? "Success" : "Error";
            _heartbeat.Stop();
        });
    }

    public void Clear()
    {
        RunOnUi(() => Lines.Clear());
    }

    private void BeginCore(string title, string message)
    {
        _startedAt = DateTimeOffset.Now;
        _lastActivityAt = _startedAt;
        _lastMessage = message;
        IsRunning = true;
        HasError = false;
        CurrentTitle = title;
        CurrentMessage = message;
        ProgressPercent = 0;
        StatusText = "运行中";
        StatusTone = "Running";
        ElapsedText = "00:00";
        _heartbeat.Start();
        AddLine("›", message, "Accent");
        ActivityStarted?.Invoke(this, EventArgs.Empty);
    }

    private void ReportCore(string message, int? percent, bool isError)
    {
        if (!IsRunning)
        {
            BeginCore(CurrentTitle, "收到后台任务进度");
        }
        if (message.Length == 0) return;
        _lastActivityAt = DateTimeOffset.Now;
        CurrentMessage = message;
        if (percent is not null)
        {
            ProgressPercent = Math.Clamp(percent.Value, 0, 100);
        }
        if (isError)
        {
            HasError = true;
            StatusTone = "Error";
        }
        if (!string.Equals(_lastMessage, message, StringComparison.Ordinal) || isError)
        {
            AddLine(isError ? "!" : "·", message, isError ? "Error" : "Muted");
            _lastMessage = message;
        }
    }

    private void CompleteCore(string message, bool success, bool cancelled = false)
    {
        if (!IsRunning)
        {
            BeginCore(CurrentTitle, "后台任务结束");
        }
        CurrentMessage = message;
        ProgressPercent = success ? 100 : ProgressPercent;
        HasError = !success && !cancelled;
        IsRunning = false;
        StatusText = cancelled ? "已取消" : success ? "已完成" : "失败";
        StatusTone = success ? "Success" : cancelled ? "Idle" : "Error";
        _heartbeat.Stop();
        UpdateElapsed();
        AddLine(success ? "✓" : cancelled ? "×" : "!", message, success ? "Success" : cancelled ? "Muted" : "Error");
    }

    private void Tick()
    {
        UpdateElapsed();
    }

    private void UpdateElapsed()
    {
        if (_startedAt == default) return;
        var elapsed = DateTimeOffset.Now - _startedAt;
        ElapsedText = elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
            : $"{(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00}";
    }

    private void AddLine(string prefix, string message, string tone)
    {
        Lines.Add(new ActivityConsoleLine
        {
            Time = DateTime.Now.ToString("HH:mm:ss"),
            Prefix = prefix,
            Message = message,
            Tone = tone
        });
        while (Lines.Count > 300) Lines.RemoveAt(0);
    }

    private static IEnumerable<string> NormalizeOutput(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) yield break;
        var bounded = output.Length > 8000 ? output[..8000] + "\n…" : output;
        foreach (var line in bounded.ReplaceLineEndings("\n")
                     .Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.TrimEnd();
            if (trimmed.Length > 0) yield return trimmed;
        }
    }

    private void RunOnUi(Action action)
    {
        if (_dispatcher.CheckAccess()) action();
        else _dispatcher.BeginInvoke(action, DispatcherPriority.Background);
    }
}
