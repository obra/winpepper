namespace Winpepper.Asr.Transcription;

/// <summary>Raised when an AssemblyAI request fails in a non-recoverable way.</summary>
public sealed class AssemblyAiException : Exception
{
    public int? StatusCode { get; }
    public bool IsAuthError { get; }

    public AssemblyAiException(string message, int? statusCode = null, bool isAuthError = false, Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
        IsAuthError = isAuthError;
    }
}
