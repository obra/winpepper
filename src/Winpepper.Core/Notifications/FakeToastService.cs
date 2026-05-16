namespace Winpepper.Core.Notifications;

public sealed class FakeToastService : IToastService
{
    public sealed record Call(string Title, string Body, ToastButton[] Buttons);

    public List<Call> Calls { get; } = new();
    private string _next = "";

    public void AutoSelect(string tag) => _next = tag;

    public async Task<string> ShowAsync(string title, string body, IReadOnlyList<ToastButton> buttons, TimeSpan timeout)
    {
        Calls.Add(new Call(title, body, buttons.ToArray()));
        if (string.IsNullOrEmpty(_next))
        {
            await Task.Delay(timeout < TimeSpan.FromMilliseconds(50) ? timeout : TimeSpan.FromMilliseconds(50));
            return "";
        }
        return _next;
    }
}
