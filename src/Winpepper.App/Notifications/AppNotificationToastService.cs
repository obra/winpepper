#if WINDOWS
using System.Collections.Concurrent;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Winpepper.Core.Notifications;

namespace Winpepper.App.Notifications;

/// <summary>
/// Production <see cref="IToastService"/> backed by WinAppSDK's
/// <c>AppNotificationManager</c>. Each toast carries a unique id; the manager's
/// <c>NotificationInvoked</c> event resolves the matching pending task.
/// </summary>
public sealed class AppNotificationToastService : IToastService, IDisposable
{
    private readonly AppNotificationManager _mgr;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _pending = new();

    public AppNotificationToastService()
    {
        _mgr = AppNotificationManager.Default;
        _mgr.NotificationInvoked += OnInvoked;
        _mgr.Register();
    }

    public Task<string> ShowAsync(string title, string body, IReadOnlyList<ToastButton> buttons, TimeSpan timeout)
    {
        var id = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var builder = new AppNotificationBuilder()
            .AddText(title)
            .AddText(body);
        foreach (var btn in buttons)
        {
            builder.AddButton(new AppNotificationButton(btn.Label)
                .AddArgument("toastId", id)
                .AddArgument("tag", btn.Tag));
        }
        builder.AddArgument("toastId", id);
        _mgr.Show(builder.BuildNotification());

        _ = Task.Delay(timeout).ContinueWith(_ =>
        {
            if (_pending.TryRemove(id, out var stale)) stale.TrySetResult("");
        });

        return tcs.Task;
    }

    private void OnInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        if (!args.Arguments.TryGetValue("toastId", out var id)) return;
        if (!_pending.TryRemove(id, out var tcs)) return;
        args.Arguments.TryGetValue("tag", out var tag);
        tcs.TrySetResult(tag ?? "");
    }

    public void Dispose()
    {
        _mgr.NotificationInvoked -= OnInvoked;
        _mgr.Unregister();
    }
}
#endif
