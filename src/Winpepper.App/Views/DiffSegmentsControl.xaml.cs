#if WINDOWS
using Microsoft.UI;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Winpepper.History.Diff;

namespace Winpepper.App.Views;

public sealed partial class DiffSegmentsControl : UserControl
{
    private IReadOnlyList<WordDiffSegment> _segments = Array.Empty<WordDiffSegment>();

    public DiffSegmentsControl()
    {
        this.InitializeComponent();
    }

    public IReadOnlyList<WordDiffSegment> Segments
    {
        get => _segments;
        set { _segments = value; Render(); }
    }

    private void Render()
    {
        DiffText.Blocks.Clear();
        var para = new Paragraph();
        foreach (var seg in _segments)
        {
            var run = new Run { Text = seg.Text };
            switch (seg.Kind)
            {
                case WordDiffKind.Insert:
                    run.Foreground = new SolidColorBrush(Colors.Green);
                    break;
                case WordDiffKind.Delete:
                    run.Foreground = new SolidColorBrush(Colors.Red);
                    run.TextDecorations = Windows.UI.Text.TextDecorations.Strikethrough;
                    break;
                case WordDiffKind.Equal:
                default:
                    break;
            }
            para.Inlines.Add(run);
        }
        DiffText.Blocks.Add(para);
    }
}
#endif
