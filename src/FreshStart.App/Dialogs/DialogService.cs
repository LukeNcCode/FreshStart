using System.Windows;
using System.Windows.Threading;
using FreshStart.App.Controls;
using FreshStart.Core.Abstractions;
using FreshStart.Core.Operations;

namespace FreshStart.App.Dialogs;

public sealed class DialogService : IUserPromptService
{
    private ModalHost? _host;

    public void Attach(ModalHost host) => _host = host;

    public Task<DialogResult> ShowAsync(DialogRequest request, CancellationToken ct = default)
    {
        var host = _host;
        if (host is null) return Task.FromResult(new DialogResult(null));
        if (host.Dispatcher.CheckAccess()) return host.ShowAsync(request, ct);

        return host.Dispatcher.InvokeAsync(() => host.ShowAsync(request, ct), DispatcherPriority.Normal).Task.Unwrap();
    }

    public async Task<bool> ConfirmAsync(UserPrompt prompt, CancellationToken ct = default)
    {
        var result = await ShowAsync(new DialogRequest
        {
            Title = prompt.Title,
            Message = prompt.Message,
            Risk = prompt.IsDangerous ? RiskLevel.High : RiskLevel.Caution,
            Details = prompt.Details,
            Footer = "本操作不会上传任何数据。",
            Actions = new[]
            {
                new DialogAction("confirm", prompt.ConfirmText, isPrimary: true, isDestructive: prompt.IsDangerous),
                new DialogAction("cancel", prompt.CancelText, isCancel: true)
            }
        }, ct).ConfigureAwait(true);

        return result.Is("confirm");
    }
}
