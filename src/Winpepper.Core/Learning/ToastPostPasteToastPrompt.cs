using Winpepper.Core.Notifications;

namespace Winpepper.Core.Learning;

/// <summary>
/// Renders the post-paste learning toast via <see cref="IToastService"/> and
/// maps the chosen tag back to a <see cref="PostPasteDecision"/>. Spec §8.2 (5)–(6).
/// </summary>
public sealed class ToastPostPasteToastPrompt : IPostPasteToastPrompt
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);

    private readonly IToastService _toasts;

    public ToastPostPasteToastPrompt(IToastService toasts)
    {
        _toasts = toasts ?? throw new ArgumentNullException(nameof(toasts));
    }

    public async Task<PostPasteDecision> AskAsync(LearningCandidate c, CancellationToken ct)
    {
        var body = $"Learn correction: `{c.Wrong}` -> `{c.Right}`?";
        var buttons = new[]
        {
            new ToastButton("yes", "Yes"),
            new ToastButton("preferred", "Preferred"),
            new ToastButton("no", "No"),
        };
        var chosen = await _toasts.ShowAsync("Winpepper", body, buttons, Timeout).ConfigureAwait(false);
        return chosen switch
        {
            "yes" => PostPasteDecision.Yes,
            "preferred" => PostPasteDecision.Preferred,
            _ => PostPasteDecision.No,
        };
    }
}
