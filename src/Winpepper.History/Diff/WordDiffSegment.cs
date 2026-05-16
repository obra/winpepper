namespace Winpepper.History.Diff;

public enum WordDiffKind
{
    Equal,
    Insert,
    Delete,
}

/// <summary>
/// One run of words in the diff. <see cref="Text"/> always includes the
/// trailing whitespace originally separating it from the next token so that
/// concatenating Equal+Delete reconstructs the original input and
/// Equal+Insert reconstructs the rerun.
/// </summary>
public sealed record WordDiffSegment(WordDiffKind Kind, string Text);
