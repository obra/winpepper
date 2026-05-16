namespace Winpepper.Core.Notifications;

public interface IToastService
{
    Task<string> ShowAsync(string title, string body, IReadOnlyList<ToastButton> buttons, TimeSpan timeout);
}
