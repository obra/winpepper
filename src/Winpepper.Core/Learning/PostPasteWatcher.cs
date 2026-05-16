namespace Winpepper.Core.Learning;

/// <summary>
/// Orchestrates the post-paste learning flow. Spec §8.2.
/// Subscribes via <see cref="IFocusedElementTextWatcher"/>, runs each change
/// through <see cref="LearningDiffAnalyzer"/>, prompts user on first accepted
/// candidate, applies their decision.
/// </summary>
public sealed class PostPasteWatcher : IDisposable
{
    private readonly IFocusedElementTextWatcher _watcher;
    private readonly ICorrectionWriter _writer;
    private readonly IPostPasteToastPrompt _prompt;
    private readonly TimeSpan _watchWindow;
    private readonly HashSet<(string Wrong, string Right)> _sessionSuppress = new();
    private readonly object _gate = new();
    private bool _disposed;

    public PostPasteWatcher(
        IFocusedElementTextWatcher watcher,
        ICorrectionWriter writer,
        IPostPasteToastPrompt prompt,
        TimeSpan? watchWindow = null)
    {
        _watcher = watcher ?? throw new ArgumentNullException(nameof(watcher));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _prompt = prompt ?? throw new ArgumentNullException(nameof(prompt));
        _watchWindow = watchWindow ?? TimeSpan.FromSeconds(30);
    }

    public async Task BeginAsync(PostPasteContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (_disposed) return;

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource(_watchWindow);
        cts.Token.Register(() => tcs.TrySetResult(false));

        var sub = _watcher.Subscribe(ctx.ElementId, async change =>
        {
            if (cts.IsCancellationRequested) return;
            var candidate = LearningDiffAnalyzer.Analyze(ctx.InjectedText, change.NewText);
            if (candidate is null) return;

            lock (_gate)
            {
                if (_sessionSuppress.Contains((candidate.Wrong, candidate.Right))) return;
            }

            PostPasteDecision decision;
            try { decision = await _prompt.AskAsync(candidate, cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { decision = PostPasteDecision.No; }

            ApplyDecision(candidate, decision);
            tcs.TrySetResult(true);
        });

        try { await tcs.Task.ConfigureAwait(false); }
        finally { sub.Dispose(); }
    }

    private void ApplyDecision(LearningCandidate c, PostPasteDecision decision)
    {
        switch (decision)
        {
            case PostPasteDecision.Yes: _writer.AddReplacement(c.Wrong, c.Right); break;
            case PostPasteDecision.Preferred: _writer.AddPreferred(c.Right); break;
            case PostPasteDecision.No: lock (_gate) _sessionSuppress.Add((c.Wrong, c.Right)); break;
        }
    }

    public void Dispose() { _disposed = true; }
}
