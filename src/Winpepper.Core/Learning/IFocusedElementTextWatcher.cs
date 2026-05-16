namespace Winpepper.Core.Learning;

/// <summary>
/// Abstraction over UIA's <c>TextEdit_TextChangedEvent</c> /
/// <c>Text_TextChangedEvent</c> subscription. The Windows implementation lives
/// in <c>Winpepper.Platform.Learning</c>; <c>FakeFocusedElementTextWatcher</c>
/// drives unit tests for <c>PostPasteWatcher</c>.
/// </summary>
public interface IFocusedElementTextWatcher
{
    /// <summary>
    /// Subscribe to text changes for the supplied opaque element identifier.
    /// Implementations decide what the identifier means (UIA RuntimeId).
    /// Disposing the returned <see cref="IDisposable"/> tears down the subscription.
    /// </summary>
    IDisposable Subscribe(string elementId, Func<FocusedElementTextChange, Task> onChange);
}
