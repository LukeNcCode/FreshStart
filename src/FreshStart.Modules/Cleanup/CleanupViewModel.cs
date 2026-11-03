using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreshStart.Core;
using FreshStart.Core.Abstractions;
using FreshStart.Services.Cleanup;

namespace FreshStart.Modules.Cleanup;

public sealed partial class CleanupViewModel : ObservableObject
{
    private readonly CleanupService _service;
    private readonly IUserPromptService _prompts;
    private readonly IToastService _toast;

    public CleanupViewModel(CleanupService service, IUserPromptService prompts, IToastService toast)
    {
        _service = service;
        _prompts = prompts;
        _toast = toast;
    }

    public ObservableCollection<CleanupCategory> Categories { get; } = new();

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = "正在扫描临时文件…";

    [ObservableProperty]
    private string _totalText = "—";

    [RelayCommand]
    private async Task LoadAsync()
    {
        await ScanAsync();
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusText = "正在扫描临时文件…";
        try
        {
            var categories = _service.GetCategories();
            long total = 0;
            foreach (var category in categories)
            {
                await _service.ScanAsync(category, CancellationToken.None);
                total += category.Bytes;
            }

            Categories.Clear();
            foreach (var category in categories) Categories.Add(category);
            TotalText = CleanupCategory.Format(total);
            StatusText = "扫描完成；清理会永久删除缓存文件，无法自动回滚";
        }
        catch (Exception ex)
        {
            Log.Error("扫描临时文件失败", ex);
            StatusText = "扫描失败，请查看日志";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task CleanAsync(CleanupCategory? category)
    {
        if (category is null || IsBusy || category.FileCount == 0) return;
        var confirmed = await _prompts.ConfirmAsync(new UserPrompt
        {
            Title = $"清理「{category.Title}」？",
            Message = $"将删除 {category.FileCount:N0} 个文件，约 {category.SizeText}",
            Details = new[]
            {
                "临时文件/缓存删除后无法恢复",
                "正在被程序使用的文件会被跳过",
                "不会影响已安装程序与个人文档"
            },
            ConfirmText = "清理",
            CancelText = "取消",
            IsDangerous = true
        });
        if (!confirmed) return;

        IsBusy = true;
        try
        {
            var result = await _service.CleanAsync(category, CancellationToken.None);
            _toast.Show(result.Message, result.Success ? ToastKind.Success : ToastKind.Error);
            await ScanAsync();
        }
        catch (Exception ex)
        {
            Log.Error("清理临时文件失败", ex);
            _toast.Show($"清理失败：{ex.Message}", ToastKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
