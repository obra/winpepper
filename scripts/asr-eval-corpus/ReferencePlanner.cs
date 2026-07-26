using System.IO;

namespace AsrEvalCorpus;

public enum ReferenceAction
{
    Skip,
    WriteEmpty,
    Transcribe,
}

public static class ReferencePlanner
{
    /// <summary>The reference transcript sits next to the clip: clips/&lt;id&gt;.reference.txt.</summary>
    public static string ReferencePath(string corpusDir, CorpusEntry entry)
        => Path.Combine(corpusDir, Path.ChangeExtension(entry.WavPath, ".reference.txt"));

    public static ReferenceAction Decide(CorpusEntry entry, bool referenceExists, bool force)
    {
        if (entry.Exclude) return ReferenceAction.Skip;
        if (referenceExists && !force) return ReferenceAction.Skip;
        return entry.ExpectedSilent ? ReferenceAction.WriteEmpty : ReferenceAction.Transcribe;
    }
}
