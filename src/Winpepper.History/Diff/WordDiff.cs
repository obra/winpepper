using DiffPlex;
using DiffPlex.Chunkers;

namespace Winpepper.History.Diff;

/// <summary>
/// Word-level diff over two transcripts. Wraps DiffPlex's <see cref="Differ"/>
/// with a stable output shape and contiguous segment merging so the Lab UI
/// can render runs of green / red / unchanged text without single-word
/// thrashing.
/// </summary>
public static class WordDiff
{
    private static readonly WordChunker Chunker = WordChunker.Instance;

    public static IReadOnlyList<WordDiffSegment> Compute(string original, string rerun)
    {
        var differ = new Differ();
        var diff = differ.CreateDiffs(original, rerun, ignoreWhiteSpace: false, ignoreCase: false, Chunker);

        var oldTokens = diff.PiecesOld;
        var newTokens = diff.PiecesNew;
        var blocks = diff.DiffBlocks;

        var result = new List<WordDiffSegment>();
        var oldIdx = 0;
        var newIdx = 0;

        foreach (var block in blocks)
        {
            // Equal block before this diff block.
            if (block.DeleteStartA > oldIdx)
            {
                Append(result, WordDiffKind.Equal,
                    string.Concat(oldTokens.AsSpan(oldIdx, block.DeleteStartA - oldIdx).ToArray()));
            }

            if (block.DeleteCountA > 0)
            {
                Append(result, WordDiffKind.Delete,
                    string.Concat(oldTokens.AsSpan(block.DeleteStartA, block.DeleteCountA).ToArray()));
            }
            if (block.InsertCountB > 0)
            {
                Append(result, WordDiffKind.Insert,
                    string.Concat(newTokens.AsSpan(block.InsertStartB, block.InsertCountB).ToArray()));
            }

            oldIdx = block.DeleteStartA + block.DeleteCountA;
            newIdx = block.InsertStartB + block.InsertCountB;
        }

        // Trailing equal tail.
        if (oldIdx < oldTokens.Length)
        {
            Append(result, WordDiffKind.Equal,
                string.Concat(oldTokens.AsSpan(oldIdx, oldTokens.Length - oldIdx).ToArray()));
        }

        return result;
    }

    private static void Append(List<WordDiffSegment> list, WordDiffKind kind, string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (list.Count > 0 && list[^1].Kind == kind)
        {
            list[^1] = list[^1] with { Text = list[^1].Text + text };
            return;
        }
        list.Add(new WordDiffSegment(kind, text));
    }
}
