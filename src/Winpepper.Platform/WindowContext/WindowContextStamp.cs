namespace Winpepper.Platform.WindowContext;

/// <summary>Pure consume-time ctx_src mapping for the dictation timing line
/// (0b semantics), extracted from PipelineHost's two duplicated arms so the
/// 1a cancellation policy is Linux-testable end to end. "none" whenever the
/// prefetch was not complete when CleanupRunner stopped waiting (consumed ==
/// false) or completed cancelled/faulted/empty; otherwise the Source.</summary>
public static class WindowContextStamp
{
    public static string? CtxSrc(bool? consumedWindowContext, Task<WindowContextResult>? prefetchTask)
        => consumedWindowContext switch
        {
            null => null,   // no context task supplied/enabled -> omit the field
            false => "none",
            true => prefetchTask is { IsCompletedSuccessfully: true } done
                ? done.Result.Source switch
                {
                    WindowContextSource.Uia => "uia",
                    WindowContextSource.Ocr => "ocr",
                    _ => "none",
                }
                : "none",
        };
}
