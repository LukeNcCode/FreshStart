using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreshStart.Core;
using FreshStart.Core.Abstractions;
using FreshStart.Core.Operations;
using FreshStart.Services.Restore;

namespace FreshStart.Modules.Restore;

public sealed partial class RestoreViewModel : ObservableObject
{
    private readonly GlobalRestoreService _service;
    private readonly OperationRunner _runner;
    private readonly IToastService _toast;
    private readonly IUserPromptService _prompts;

    public RestoreViewModel(
        GlobalRestoreService service,
        OperationRunner runner,
        IToastService toast,
        IUserPromptService prompts)
    {
        _service = service;
        _runner = runner;
        _toast = toast;
        _prompts = prompts;
    }

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = "正在读取基准…";

    [ObservableProperty]
    private string _baselineText = "—";

    [ObservableProperty]
    private bool _hasBaseline;

    [RelayCommand]
    private void Load()
    {
        var baseline = _service.Load();
        HasBaseline = baseline is not null;
        BaselineText = baseline is null
            ? "尚未创建基准"
            : $"{baseline.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm} · {baseline.Values.Count} 项注册表值";
        StatusText = baseline is null
            ? "基准会在首次正常启动时自动创建；也可以现在手动创建（记录当前状态）"
            : "还原会把 FreshStart 管理的注册表调控项恢复到基准；不包含服务、计划任务、驱动与已卸载程序";
    }

    [RelayCommand]
    private async Task CreateBaselineAsync()
    {
        if (IsBusy) return;
        var confirmed = await _prompts.ConfirmAsync(new UserPrompt
        {
            Title = "用当前状态覆盖基准？",
            Message = "后续“全局还原”会恢复到现在的设置，而不是最初安装时的状态。",
            Details = new[] { "只记录 FreshStart 管理的注册表调控项", "不会备份文件、服务或已卸载程序" },
            ConfirmText = "覆盖基准",
            CancelText = "取消",
            IsDangerous = true
        });
        if (!confirmed) return;

        IsBusy = true;
        try
        {
            await _service.CreateBaselineAsync();
            Load();
            _toast.Show("基准已创建", ToastKind.Success);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RestoreAsync()
    {
        if (IsBusy) return;
        var baseline = _service.Load();
        if (baseline is null) return;

        IsBusy = true;
        try
        {
            var execution = await _runner.ExecuteAsync(_service.CreateOperation(baseline));
            if (execution.Success)
            {
                _toast.Show("全局还原完成，重启或重新登录后生效", ToastKind.Success);
                StatusText = "还原完成；建议重启或重新登录";
            }
            else if (!execution.Cancelled)
            {
                _toast.Show(execution.Result.Message, ToastKind.Error);
            }
        }
        catch (Exception ex)
        {
            Log.Error("全局还原失败", ex);
            _toast.Show($"还原失败：{ex.Message}", ToastKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
