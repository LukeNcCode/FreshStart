using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreshStart.Core;
using FreshStart.Core.Abstractions;
using FreshStart.Services.Score;

namespace FreshStart.Modules.Score;

public sealed partial class ScoreViewModel : ObservableObject
{
    private readonly ScoreService _service;
    private readonly IToastService _toast;
    private ScoreSnapshot? _current;
    private double? _previousTotal;

    public ScoreViewModel(ScoreService service, IToastService toast)
    {
        _service = service;
        _toast = toast;
    }

    public ObservableCollection<ScoreMetric> Metrics { get; } = new();

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _totalText = "—";

    [ObservableProperty]
    private string _deltaText = "尚未评分";

    [ObservableProperty]
    private string _statusText = "采集启动项、服务、任务、磁盘、内存、隐私与软件数量，只读评分";

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (_current is not null) return;
        await ScanAsync();
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusText = "正在采集评分数据…";
        try
        {
            var snapshot = await _service.CaptureAsync();
            if (_current is not null) _previousTotal = _current.Total;
            _current = snapshot;
            Metrics.Clear();
            foreach (var metric in snapshot.Metrics) Metrics.Add(metric);
            TotalText = $"{snapshot.Total:0}";
            DeltaText = _previousTotal is null
                ? "首次评分"
                : snapshot.Total >= _previousTotal
                    ? $"较上次 +{snapshot.Total - _previousTotal.Value:0.#}"
                    : $"较上次 {snapshot.Total - _previousTotal.Value:0.#}";
            StatusText = $"采集时间：{snapshot.CapturedAt.ToLocalTime():HH:mm:ss} · 评分只用于展示，不会自动修改设置";
        }
        catch (Exception ex)
        {
            Log.Error("开荒评分失败", ex);
            StatusText = "评分失败，请查看日志";
            _toast.Show("开荒评分失败", ToastKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SaveReportAsync()
    {
        if (_current is null || IsBusy) return;
        IsBusy = true;
        try
        {
            var (html, _) = await _service.SaveReportAsync(_current);
            StatusText = $"报告已保存：{html}";
            _toast.Show("评分报告已保存", ToastKind.Success);
        }
        catch (Exception ex)
        {
            Log.Error("保存评分报告失败", ex);
            _toast.Show("保存报告失败", ToastKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
