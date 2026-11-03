using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreshStart.Core;
using FreshStart.Core.Abstractions;
using FreshStart.Core.Operations;
using FreshStart.Modules.Lan;
using FreshStart.Services.Network;

namespace FreshStart.Modules.Network;

public sealed partial class NetworkViewModel : ObservableObject
{
    private readonly NetworkService _service;
    private readonly OperationRunner _runner;
    private readonly IToastService _toast;
    private int _dnsVersion;

    public NetworkViewModel(
        NetworkService service,
        OperationRunner runner,
        IToastService toast,
        LanShareViewModel lan)
    {
        _service = service;
        _runner = runner;
        _toast = toast;
        Lan = lan;
        Presets = NetworkService.Presets;
        _selectedPreset = Presets[0];
    }

    public LanShareViewModel Lan { get; }

    [ObservableProperty]
    private bool _showNetwork = true;

    [ObservableProperty]
    private bool _showLan;

    partial void OnShowNetworkChanged(bool value)
    {
        if (value) ShowLan = false;
    }

    partial void OnShowLanChanged(bool value)
    {
        if (value) ShowNetwork = false;
    }

    public ObservableCollection<NetworkAdapterInfo> Adapters { get; } = new();
    public IReadOnlyList<DnsPreset> Presets { get; }

    [ObservableProperty]
    private NetworkAdapterInfo? _selectedAdapter;

    [ObservableProperty]
    private DnsPreset? _selectedPreset;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = "正在读取网络适配器…";

    [ObservableProperty]
    private string _currentDnsText = "—";

    [ObservableProperty]
    private string _pingHost = "223.5.5.5";

    [ObservableProperty]
    private string _pingText = "尚未测试";

    [ObservableProperty]
    private bool _isTesting;

    partial void OnSelectedAdapterChanged(NetworkAdapterInfo? value)
    {
        if (value is null) return;
        _ = RefreshDnsAsync(value);
        _ = RefreshIpAsync(value);
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        await ReloadAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync() => await ReloadAsync();

    private async Task ReloadAsync()
    {
        IsBusy = true;
        try
        {
            var adapters = await _service.GetAdaptersAsync().ConfigureAwait(true);
            var currentName = SelectedAdapter?.Name;
            Adapters.Clear();
            foreach (var adapter in adapters) Adapters.Add(adapter);

            SelectedAdapter = Adapters.FirstOrDefault(adapter => adapter.Name == currentName)
                              ?? Adapters.FirstOrDefault(adapter => adapter.IsUp && NetworkService.IsPrimary(adapter))
                              ?? Adapters.FirstOrDefault(adapter => adapter.IsUp)
                              ?? Adapters.FirstOrDefault();
            StatusText = Adapters.Count == 0
                ? "未找到以太网/WLAN 适配器"
                : "选择适配器后可切换 IPv4 DNS；修改前会显示当前服务器并备份";
        }
        catch (Exception ex)
        {
            Log.Error("读取网络适配器失败", ex);
            StatusText = "读取网络适配器失败，请查看日志";
            _toast.Show("读取网络适配器失败", ToastKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshDnsAsync(NetworkAdapterInfo adapter)
    {
        var version = ++_dnsVersion;
        try
        {
            var servers = await _service.GetDnsAsync(adapter.Index).ConfigureAwait(true);
            if (version != _dnsVersion) return;
            CurrentDnsText = servers.Count == 0 ? "自动（DHCP）" : string.Join(", ", servers);
            adapter.DnsServers.Clear();
            adapter.DnsServers.AddRange(servers);
            OnPropertyChanged(nameof(SelectedAdapter));
        }
        catch (Exception ex)
        {
            if (version == _dnsVersion) CurrentDnsText = "读取失败";
            Log.Warn($"读取 DNS 失败: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task ApplyDnsAsync()
    {
        if (IsBusy || SelectedAdapter is null || SelectedPreset is null) return;
        IsBusy = true;
        try
        {
            var adapter = SelectedAdapter;
            var operation = new NetworkDnsOperation(_service, adapter, SelectedPreset.Servers, SelectedPreset.Name);
            var execution = await _runner.ExecuteAsync(operation);
            if (execution.Success)
            {
                _toast.Show($"DNS 已切换为 {SelectedPreset.Name}", ToastKind.Success);
                await ReloadAsync();
                if (SelectedAdapter is not null) await RefreshDnsAsync(SelectedAdapter);
            }
            else if (!execution.Cancelled)
            {
                _toast.Show(execution.Result.Message, ToastKind.Error);
            }
        }
        catch (Exception ex)
        {
            Log.Error("切换 DNS 失败", ex);
            _toast.Show($"切换 DNS 失败：{ex.Message}", ToastKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task FlushDnsAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var result = await _service.FlushDnsAsync(CancellationToken.None);
            _toast.Show(result.Message, result.Success ? ToastKind.Success : ToastKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [ObservableProperty]
    private string _ipModeText = "自动（DHCP）";

    [ObservableProperty]
    private string _currentIpText = "—";

    [ObservableProperty]
    private string _ipAddress = string.Empty;

    [ObservableProperty]
    private string _ipPrefix = "24";

    [ObservableProperty]
    private string _ipGateway = string.Empty;

    [ObservableProperty]
    private bool _ipStatic;

    private async Task RefreshIpAsync(NetworkAdapterInfo adapter)
    {
        try
        {
            var config = await _service.GetIpConfigAsync(adapter.Index).ConfigureAwait(true);
            IpStatic = !config.Dhcp;
            IpModeText = config.ModeText;
            CurrentIpText = config.Summary;
            IpAddress = config.Address;
            IpPrefix = config.Prefix > 0 ? config.Prefix.ToString() : "24";
            IpGateway = config.Gateway;
        }
        catch (Exception ex)
        {
            Log.Warn($"读取 IP 配置失败: {ex.Message}");
        }
    }

    [RelayCommand]
    private void ToggleIpMode()
    {
        IpStatic = !IpStatic;
        IpModeText = IpStatic ? "静态" : "自动（DHCP）";
    }

    [RelayCommand]
    private async Task ApplyIpAsync()
    {
        if (IsBusy || SelectedAdapter is null) return;

        var prefix = int.TryParse(IpPrefix, out var parsed) ? parsed : 24;
        if (IpStatic && (prefix < 1 || prefix > 32))
        {
            _toast.Show("子网前缀需要在 1–32 之间", ToastKind.Warning);
            return;
        }

        IsBusy = true;
        try
        {
            var operation = new NetworkIpOperation(
                _service, SelectedAdapter, !IpStatic, IpAddress.Trim(), prefix, IpGateway.Trim());
            var execution = await _runner.ExecuteAsync(operation);
            if (execution.Success)
            {
                _toast.Show(execution.Result.Message, ToastKind.Success);
                await RefreshIpAsync(SelectedAdapter);
            }
            else if (!execution.Cancelled)
            {
                _toast.Show(execution.Result.Message, ToastKind.Error);
            }
        }
        catch (Exception ex)
        {
            Log.Error("修改 IP 配置失败", ex);
            _toast.Show($"修改 IP 失败：{ex.Message}", ToastKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task PingAsync()
    {
        if (IsTesting) return;
        IsTesting = true;
        PingText = "正在测试…";
        try
        {
            var result = await _service.PingAsync(PingHost, CancellationToken.None);
            PingText = result.Success
                ? $"{result.Address} · {result.LatencyMs} ms"
                : $"失败：{result.Message}";
        }
        finally
        {
            IsTesting = false;
        }
    }
}
