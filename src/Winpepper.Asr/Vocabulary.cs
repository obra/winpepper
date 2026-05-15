namespace Winpepper.Asr;

public sealed class Vocabulary
{
    public IReadOnlyList<string> Tokens { get; }
    public int Size => Tokens.Count;
    public int BlankId { get; }

    private Vocabulary(IReadOnlyList<string> tokens, int blankId) { Tokens = tokens; BlankId = blankId; }

    public static Vocabulary FromFile(string path)
    {
        var raw = File.ReadAllLines(path).Select(l => l.TrimEnd('\r')).ToList();
        while (raw.Count > 0 && raw[^1].Length == 0) raw.RemoveAt(raw.Count - 1);

        // Two formats supported:
        //   1. One token per line (simple form, used in tests):     "▁hello"
        //   2. "<text> <id>" per line (HuggingFace exports, real):  "▁hello 7"
        // Detect format by checking whether every non-empty line ends with " <decimal>".
        var withIds = raw.Count > 0 && raw.All(l =>
        {
            var i = l.LastIndexOf(' ');
            if (i < 0 || i == l.Length - 1) return false;
            for (var k = i + 1; k < l.Length; k++)
                if (l[k] < '0' || l[k] > '9') return false;
            return true;
        });

        var tokens = withIds
            ? raw.Select(l => l[..l.LastIndexOf(' ')]).ToList()
            : raw;

        // Last token is the blank by convention.
        return new Vocabulary(tokens, tokens.Count - 1);
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
