using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreshStart.Core;
using FreshStart.Core.Abstractions;
using FreshStart.Core.Operations;
using FreshStart.Services.Score;
using FreshStart.Services.System;

namespace FreshStart.Modules.QuickActions;

public sealed class QuickActionItem
{
    public QuickActionItem(QuickActionDefinition definition) => Definition = definition;

    public QuickActionDefinition Definition { get; }
    public string Title => Definition.Title;
    public string Description => Definition.Description;
    public string IconData => Visual.AppIcons.Get(Definition.IconKey);
    public bool IsCaution => Definition.Risk == RiskLevel.Caution;
    public string RiskText => Definition.Risk switch
    {
        RiskLevel.High => "高风险",
        RiskLevel.Caution => "谨慎",
        _ => "安全"
    };

    public string TooltipText => $"{Description}\n风险：{RiskText}";
}

/// <summary>开荒工作台 ViewModel：快捷操作全部是"打开系统工具"，首屏不需要任何系统调用。</summary>
public sealed partial class QuickActionsViewModel : ObservableObject
{
    private readonly QuickActionService _service;
    private readonly IToastService _toast;
    private readonly IUserPromptService _prompts;
    private readonly ScoreService _score;
    private bool _scoreLoaded;

    public QuickActionsViewModel(
        QuickActionService service,
        ScoreService score,
        IToastService toast,
        IUserPromptService prompts)
    {
        _service = service;
        _score = score;
        _toast = toast;
        _prompts = prompts;
        Actions = QuickActionCatalog.All.Select(action => new QuickActionItem(action)).ToList();
        IsElevated = ElevationService.IsAdministrator;
    }

    [ObservableProperty]
    private double _scorePercent;

    [ObservableProperty]
    private string _scoreText = "—";

    [ObservableProperty]
    private string _scoreDetail = "正在评分…";

    public async Task LoadScoreAsync()
    {
        if (_scoreLoaded) return;
        _scoreLoaded = true;
        try
        {
            var snapshot = await _score.CaptureAsync();
            ScorePercent = snapshot.Total;
            ScoreText = $"{snapshot.Total:0}";
            ScoreDetail = "综合评分";
        }
        catch (Exception ex)
        {
            Log.Warn($"首页评分失败: {ex.Message}");
            ScoreDetail = "评分失败";
        }
    }

    public IReadOnlyList<QuickActionItem> Actions { get; }

    public bool IsElevated { get; }

    public string ElevationText => IsElevated ? "已以管理员身份运行" : "未提权运行，部分系统操作会失败";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = "所有操作仅在本机执行，不采集、不上传任何数据";

    [RelayCommand]
    private async Task RunAsync(QuickActionItem? item)
    {
        if (item is null || IsBusy) return;

        if (item.Definition.Kind == QuickActionKind.RestartExplorer)
        {
            var confirmed = await _prompts.ConfirmAsync(new UserPrompt
            {
                Title = "重启资源管理器？",
                Message = "将结束 explorer.exe 并由系统自动重启。",
                Details = new[]
                {
                    "任务栏、桌面和已打开的文件资源管理器窗口会短暂消失（约 1–3 秒）",
                    "不会关闭其他应用程序，也不会修改任何配置",
                    "本操作不需要备份，可反复执行"
                },
                ConfirmText = "重启",
                CancelText = "取消"
            });
            if (!confirmed) return;
        }

        IsBusy = true;
        StatusText = $"正在执行：{item.Title}";
        try
        {
            await _service.ExecuteAsync(item.Definition);
            _toast.Show(item.Definition.Kind == QuickActionKind.RestartExplorer
                ? "资源管理器已重启"
                : $"已打开{item.Title}", ToastKind.Success);
        }
        catch (Exception ex)
        {
            Log.Error($"快捷操作失败: {item.Title}", ex);
            _toast.Show($"打开失败：{ex.Message}", ToastKind.Error);
        }
        finally
        {
            IsBusy = false;
            StatusText = "所有操作仅在本机执行，不采集、不上传任何数据";
        }
    }
}
