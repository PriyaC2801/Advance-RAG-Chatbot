using AdvancedRag.Core.Ai;
using AdvancedRag.Core.Rag;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace AdvancedRag.Api.Infrastructure;

/// <summary>
/// Turns known failures into clear HTTP responses (RFC 7807 ProblemDetails) in one place, so controllers stay simple.
/// Messages are safe to show: no API keys, stack traces or raw requests.
/// </summary>
public sealed class ApiExceptionHandler(IProblemDetailsService problemDetails, ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        (int Status, string Title, string Detail)? mapped = exception switch
        {
            GeminiApiException { Kind: GeminiErrorKind.MissingApiKey } e =>
                (StatusCodes.Status503ServiceUnavailable, "Gemini API key not configured", e.Message),
            GeminiApiException { Kind: GeminiErrorKind.InvalidApiKey } =>
                (StatusCodes.Status502BadGateway, "Gemini rejected the API key", "Check the key stored in user secrets."),
            GeminiApiException { Kind: GeminiErrorKind.RateLimitedOrQuotaExhausted } e =>
                (StatusCodes.Status429TooManyRequests, "Gemini rate limit or quota reached", e.Message),
            GeminiApiException { Kind: GeminiErrorKind.Timeout } e =>
                (StatusCodes.Status504GatewayTimeout, "Gemini timed out", e.Message),
            GeminiApiException e =>
                (StatusCodes.Status502BadGateway, "Gemini request failed", e.Message),
            VectorStoreUnavailableException e =>
                (StatusCodes.Status503ServiceUnavailable, "Vector database unavailable", e.Message),
            IndexNotReadyException e =>
                (StatusCodes.Status409Conflict, "Search index not ready", e.Message),
            ConflictException e =>
                (StatusCodes.Status409Conflict, "Conflict", e.Message),
            BadRequestException e =>
                (StatusCodes.Status400BadRequest, "Invalid request", e.Message),
            KeyNotFoundException e =>
                (StatusCodes.Status404NotFound, "Not found", e.Message),
            FileNotFoundException =>
                (StatusCodes.Status404NotFound, "Document PDF not found", "Place the PDF in the documents folder (see Storage:RootDirectory)."),
            _ => null,
        };

        if (mapped is null) return false; // let the default handler produce a generic 500

        logger.LogWarning("{ExceptionType}: {Message}", exception.GetType().Name, exception.Message);
        context.Response.StatusCode = mapped.Value.Status;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Status = mapped.Value.Status,
                Title = mapped.Value.Title,
                Detail = mapped.Value.Detail,
            },
        });
    }
}
