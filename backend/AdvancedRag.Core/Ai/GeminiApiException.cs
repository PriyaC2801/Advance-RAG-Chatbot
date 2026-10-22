namespace AdvancedRag.Core.Ai;

public enum GeminiErrorKind
{
    MissingApiKey,
    InvalidApiKey,
    RateLimitedOrQuotaExhausted,
    Timeout,
    ServerError,
    InvalidRequest,
    InvalidResponse,
}

/// <summary>
/// A Gemini failure with a category the API layer turns into a safe message and status code.
/// The message never contains the API key or the raw request.
/// </summary>
public sealed class GeminiApiException(GeminiErrorKind kind, string message, int? httpStatus = null, Exception? inner = null)
    : Exception(message, inner)
{
    public GeminiErrorKind Kind { get; } = kind;
    public int? HttpStatus { get; } = httpStatus;
}
