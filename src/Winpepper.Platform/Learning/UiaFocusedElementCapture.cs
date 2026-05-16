namespace Winpepper.Platform.Learning;

/// <summary>
/// Pure helpers for translating UIA RuntimeIds into the opaque string ids that
/// <c>UiaFocusedElementTextWatcher</c> (Windows-only) and the pure-C#
/// <c>PostPasteWatcher</c> exchange. Spec §8.2 (1)–(2).
/// </summary>
public static class UiaFocusedElementCapture
{
    public static string RuntimeIdToString(int[]? runtimeId)
    {
        if (runtimeId is null || runtimeId.Length == 0) return string.Empty;
        return string.Join('.', runtimeId);
    }
}
