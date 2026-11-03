using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreshStart.Core;
using FreshStart.Core.Abstractions;
using FreshStart.Services.Lan;
using Microsoft.Win32;

namespace FreshStart.Modules.Lan;

public sealed partial class LanShareViewModel : ObservableObject
{
    private readonly LanShareService _service;
    private readonly IToastService _toast;
    private string? _folder;

    public LanShareViewModel(LanShareService service, IToastService toast)
    {
        _service = service;
        _toast = toast;
    }

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string _folderText = "尚未选择共享目录";

    [ObservableProperty]
    private string _urlText = "";

    [ObservableProperty]
    private string _codeText = "";

    [ObservableProperty]
    private string _statusText = "仅绑定局域网地址，使用一次性随机路径；不提供上传与删除";

    [RelayCommand]
    private void ChooseFolder()
    {
        var dialog = new OpenFolderDialog { Title = "选择局域网共享目录" };
        if (dialog.ShowDialog() != true) return;
        _folder = dialog.FolderName;
        FolderText = _folder;
    }

    [RelayCommand]
    private void Start()
    {
        if (IsRunning) return;
        if (string.IsNullOrWhiteSpace(_folder))
        {
            _toast.Show("请先选择共享目录", ToastKind.Warning);
            return;
        }

        try
        {
            UrlText = _service.Start(_folder);
            CodeText = _service.ShortCode;
            IsRunning = true;
            StatusText = "共享已启动；手机/电脑在同一局域网打开下方地址即可下载";
        }
        catch (Exception ex)
        {
            Log.Error("启动局域网共享失败", ex);
            StatusText = $"启动失败：{ex.Message}";
            _toast.Show("启动局域网共享失败", ToastKind.Error);
        }
    }

    [RelayCommand]
    private void Stop()
    {
        _service.Stop();
        IsRunning = false;
        UrlText = "";
        CodeText = "";
        StatusText = "共享已停止";
    }

    [RelayCommand]
    private void CopyUrl()
    {
        if (string.IsNullOrWhiteSpace(UrlText)) return;
        try
        {
            Clipboard.SetText(UrlText);
            _toast.Show("地址已复制", ToastKind.Success);
        }
        catch
        {
            _toast.Show("复制失败", ToastKind.Warning);
        }
    }
}
