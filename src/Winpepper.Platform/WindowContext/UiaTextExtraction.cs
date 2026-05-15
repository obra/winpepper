#if WINDOWS
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace Winpepper.Platform.WindowContext;

/// <summary>
/// Extracts text from a single UIA element using the pattern-preference order
/// from spec §6.1:
///   1. TextPattern.DocumentRange.GetText(8000)
///   2. ValuePattern.Value
///   3. LegacyIAccessiblePattern.Value  -- TODO: not exposed by managed
///      System.Windows.Automation (Microsoft.WindowsDesktop.App). Reaching it
///      requires the unmanaged Interop.UIAutomationClient COM API. Deferred.
///   4. Name
/// Returns null when nothing was extractable.
/// </summary>
internal static class UiaTextExtraction
{
    private const int TextPatternCap = 8000;

    public static string? Extract(AutomationElement element)
    {
        // 1) TextPattern
        try
        {
            if (element.TryGetCurrentPattern(TextPattern.Pattern, out var textObj)
                && textObj is TextPattern tp)
            {
                var range = tp.DocumentRange;
                var text = range.GetText(TextPatternCap);
                if (!string.IsNullOrWhiteSpace(text)) return text;
            }
        }
        catch { /* fall through */ }

        // 2) ValuePattern
        try
        {
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var valueObj)
                && valueObj is ValuePattern vp)
            {
                var v = vp.Current.Value;
                if (!string.IsNullOrWhiteSpace(v)) return v;
            }
        }
        catch { }

        // 3) LegacyIAccessiblePattern -- not available in managed UIA; see class doc.

        // 4) Name
        try
        {
            var name = element.Current.Name;
            if (!string.IsNullOrWhiteSpace(name)) return name;
        }
        catch { }

        return null;
    }
}
#endif
