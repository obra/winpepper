namespace Winpepper.Cleanup;

/// <summary>Cleanup prompt profile, spec §6.4.</summary>
public enum CleanupProfile
{
    /// <summary>Default conversational dictation cleanup.</summary>
    Ordinary,

    /// <summary>Minimal rewriting: punctuation only, no filler removal.</summary>
    Literal,

    /// <summary>User-supplied base prompt.</summary>
    Custom,
}
