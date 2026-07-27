using System;
using System.Collections.Generic;

namespace Winpepper.Platform.Injection;

/// <summary>
/// Splits injection text into chunks of at most <c>chunkSize</c> UTF-16 code
/// units for the guarded (interruptible) send loop, extending a chunk by one
/// code unit when needed so a surrogate pair is never split across a chunk
/// boundary (an interrupt between the halves would leave a mangled character
/// in the old window). Pure managed; no Win32 dependency.
/// </summary>
public static class InjectionChunker
{
    public static IReadOnlyList<string> Split(string text, int chunkSize)
    {
        if (chunkSize <= 0) throw new ArgumentOutOfRangeException(nameof(chunkSize));
        if (string.IsNullOrEmpty(text)) return Array.Empty<string>();

        var chunks = new List<string>((text.Length / chunkSize) + 1);
        var i = 0;
        while (i < text.Length)
        {
            var len = Math.Min(chunkSize, text.Length - i);
            // Never end a chunk on the high half of a surrogate pair.
            if (char.IsHighSurrogate(text[i + len - 1]) && i + len < text.Length)
                len++;
            chunks.Add(text.Substring(i, len));
            i += len;
        }
        return chunks;
    }
}
