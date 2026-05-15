#if WINDOWS
using System.Windows.Automation;
using Microsoft.Extensions.Logging;

namespace Winpepper.Platform.WindowContext;

/// <summary>
/// Walks the UIA ContentView subtree of the supplied window and returns
/// extracted text elements. Spec §6.1.
/// </summary>
public sealed class UiaTreeReader
{
    private readonly ILogger<UiaTreeReader> _log;
    private const int MaxElementsVisited = 2000; // hard guard against pathological trees

    public UiaTreeReader(ILogger<UiaTreeReader> log) { _log = log; }

    public List<UiaExtractedElement> ReadForeground(IntPtr foregroundHwnd, CancellationToken ct)
    {
        var results = new List<UiaExtractedElement>();
        if (foregroundHwnd == IntPtr.Zero) return results;

        AutomationElement root;
        try
        {
            root = AutomationElement.FromHandle(foregroundHwnd);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "AutomationElement.FromHandle failed; UIA path unavailable");
            return results;
        }

        var walker = TreeWalker.ContentViewWalker;
        var visited = 0;
        var stack = new Stack<AutomationElement>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            if (visited++ > MaxElementsVisited) break;

            var current = stack.Pop();

            try
            {
                var text = UiaTextExtraction.Extract(current);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    var rect = current.Current.BoundingRectangle;
                    results.Add(new UiaExtractedElement(text!, (int)rect.Left, (int)rect.Top));
                }
            }
            catch (Exception ex) { _log.LogTrace(ex, "Element extract failed; skipping"); }

            // Push children in reverse so siblings come out in document order.
            try
            {
                var children = new List<AutomationElement>();
                var child = walker.GetFirstChild(current);
                while (child != null)
                {
                    children.Add(child);
                    child = walker.GetNextSibling(child);
                }
                for (var i = children.Count - 1; i >= 0; i--) stack.Push(children[i]);
            }
            catch (Exception ex) { _log.LogTrace(ex, "Sibling walk failed"); }
        }

        return results;
    }
}
#endif
