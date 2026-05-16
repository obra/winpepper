#if WINDOWS
using System.Collections.Concurrent;
using System.Windows.Automation;
using Microsoft.Extensions.Logging;
using Winpepper.Core.Learning;

namespace Winpepper.Platform.Learning;

/// <summary>
/// Subscribes to UIA <c>TextEdit_TextChangedEvent</c> on the focused element
/// (falls back to <c>Text_TextChangedEvent</c>). Spec §8.2 (2).
/// </summary>
public sealed class UiaFocusedElementTextWatcher : IFocusedElementTextWatcher
{
    private readonly ILogger<UiaFocusedElementTextWatcher> _log;
    private readonly ConcurrentDictionary<string, AutomationElement> _byId = new();

    public UiaFocusedElementTextWatcher(ILogger<UiaFocusedElementTextWatcher> log) { _log = log; }

    /// <summary>
    /// Register a live UIA element under the supplied id so a later
    /// <see cref="Subscribe"/> call can find it. The orchestrator registers
    /// right before injection completes.
    /// </summary>
    public void RegisterFocusedElement(string elementId, AutomationElement element)
    {
        if (string.IsNullOrEmpty(elementId)) return;
        _byId[elementId] = element;
    }

    public IDisposable Subscribe(string elementId, Func<FocusedElementTextChange, Task> onChange)
    {
        if (!_byId.TryGetValue(elementId, out var element))
        {
            _log.LogDebug("UiaFocusedElementTextWatcher: no element registered for id {Id}", elementId);
            return new NoopDisposable();
        }

        AutomationEvent? subscribedEvent;
        AutomationEventHandler handler = (s, e) =>
        {
            try
            {
                if (s is not AutomationElement el) return;
                var text = ReadText(el);
                if (text is null) return;
                _ = onChange(new FocusedElementTextChange(elementId, text, DateTime.UtcNow));
            }
            catch (Exception ex) { _log.LogTrace(ex, "text-change handler threw"); }
        };

        try
        {
            Automation.AddAutomationEventHandler(
                TextPattern.TextChangedEvent, element, TreeScope.Element, handler);
            subscribedEvent = TextPattern.TextChangedEvent;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "TextEdit_TextChangedEvent unavailable; falling back to ValuePattern poll");
            subscribedEvent = null;
        }

        return new Subscription(() =>
        {
            try
            {
                if (subscribedEvent is not null)
                    Automation.RemoveAutomationEventHandler(subscribedEvent, element, handler);
            }
            catch (Exception ex) { _log.LogTrace(ex, "RemoveAutomationEventHandler failed"); }
            _byId.TryRemove(elementId, out _);
        });
    }

    private static string? ReadText(AutomationElement el)
    {
        try
        {
            if (el.TryGetCurrentPattern(TextPattern.Pattern, out var tp) && tp is TextPattern tpat)
                return tpat.DocumentRange.GetText(8000);
        }
        catch { }
        try
        {
            if (el.TryGetCurrentPattern(ValuePattern.Pattern, out var vp) && vp is ValuePattern vpat)
                return vpat.Current.Value;
        }
        catch { }
        try { return el.Current.Name; } catch { return null; }
    }

    private sealed class Subscription : IDisposable
    {
        private readonly Action _dispose;
        public Subscription(Action dispose) { _dispose = dispose; }
        public void Dispose() => _dispose();
    }

    private sealed class NoopDisposable : IDisposable { public void Dispose() { } }
}
#endif
