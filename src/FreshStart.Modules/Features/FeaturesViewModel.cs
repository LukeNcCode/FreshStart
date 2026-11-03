using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreshStart.Core;
using FreshStart.Core.Abstractions;
using FreshStart.Core.Operations;
using FreshStart.Modules.Controls;
using FreshStart.Services.Features;

namespace FreshStart.Modules.Features;

public sealed partial class FeaturesViewModel : ObservableObject
{
    private readonly OptionalFeatureService _service;
    private readonly OperationRunner _runner;
    private readonly IToastService _toast;
    private readonly IUserPromptService _prompts;
    private readonly List<OptionalFeatureInfo> _all = new();

    public FeaturesViewModel(
        OptionalFeatureService service,
        OperationRunner runner,
        IToastService toast,
        IUserPromptService prompts)
    {
        _service = service;
        _runner = runner;
        _toast = toast;
        _prompts = prompts;
    }

    public PagedList<OptionalFeatureInfo> Features { get; } = new((feature, keyword) =>
        string.IsNullOrWhiteSpace(keyword)
        || feature.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)
        || feature.DisplayName.Contains(keyword, StringComparison.OrdinalIgnoreCase));

    [ObservableProperty] private string _search = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = "正在读取可选功能…";

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (IsBusy) return;
        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        IsBusy = true;
        StatusText = "正在读取 Windows 可选功能…";
        try
        {
            var list = await _service.ListAsync().ConfigureAwait(true);
            _all.Clear();
            _all.AddRange(list);
            Features.Reset(_all, Search);
            StatusText = $"共 {_all.Count} 个功能（需要管理员权限）";
        }
        catch (Exception ex)
        {
            Log.Error("读取可选功能失败", ex);
            StatusText = "读取失败，请查看日志";
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnSearchChanged(string value) => Features.ApplyFilter(value);

    [RelayCommand]
    private async Task ToggleAsync(OptionalFeatureInfo? feature)
    {
        if (feature is null || IsBusy) return;

        var enable = !feature.Enabled;
        var confirmed = await _prompts.ConfirmAsync(new UserPrompt
        {
            Title = $"{(enable ? "启用" : "禁用")}「{feature.DisplayName}」？",
            Message = "可选功能修改可能需要几分钟，部分功能需要重启后生效。",
            Details = new[] { $"功能名：{feature.Name}", $"当前状态：{feature.StateText}" },
            ConfirmText = enable ? "启用" : "禁用",
            CancelText = "取消",
            IsDangerous = !enable
        });
        if (!confirmed) return;

        IsBusy = true;
        try
        {
            var execution = await _runner.ExecuteAsync(new OptionalFeatureOperation(_service, feature.Name, enable));
            if (execution.Success)
            {
                _toast.Show(execution.Result.Message, ToastKind.Success);
                await ReloadAsync();
            }
            else if (!execution.Cancelled)
            {
                _toast.Show(execution.Result.Message, ToastKind.Error);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }
}
