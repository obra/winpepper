namespace Winpepper.Platform.Hotkeys;

/// <summary>A raw transition observed by the low-level keyboard hook.</summary>
public readonly record struct RawKeyTransition(
    int VirtualKey,
    int ScanCode,
    bool IsDown,
    bool IsInjected,
    bool IsRepeat);
