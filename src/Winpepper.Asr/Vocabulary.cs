namespace Winpepper.Asr;

public sealed class Vocabulary
{
    public IReadOnlyList<string> Tokens { get; }
    public int Size => Tokens.Count;
    public int BlankId { get; }

    private Vocabulary(IReadOnlyList<string> tokens, int blankId) { Tokens = tokens; BlankId = blankId; }

    public static Vocabulary FromFile(string path)
    {
        var lines = File.ReadAllLines(path).Select(l => l.TrimEnd('\r')).ToList();
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return new Vocabulary(lines, lines.Count - 1);
    }

    public string Decode(IEnumerable<int> tokenIds)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var id in tokenIds)
        {
            if (id == BlankId) continue;
            if (id < 0 || id >= Tokens.Count) continue;
            var tok = Tokens[id];
            if (tok.StartsWith("▁", StringComparison.Ordinal))
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(tok[1..]);
            }
            else
            {
                sb.Append(tok);
            }
        }
        return sb.ToString();
    }
}
