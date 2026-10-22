using System.Text.Json.Nodes;

namespace AdvancedRag.Core.Ai;

/// <summary>A set of texts to embed with one model.</summary>
/// <param name="Model">e.g. "gemini-embedding-2" or "gemini-embedding-001".</param>
/// <param name="Dimensions">outputDimensionality sent to Gemini.</param>
/// <param name="Texts">Already formatted by the caller (prefixes such as "title: ... | text: ..." are pipeline-specific).</param>
/// <param name="TaskType">RETRIEVAL_DOCUMENT / RETRIEVAL_QUERY; null for models that take the task inside the text.</param>
/// <param name="BatchSize">Texts per batchEmbedContents call.</param>
/// <param name="RequestsPerMinute">Client-side budget of embedded texts per minute for this model (free tier: 100).</param>
public sealed record EmbedRequest(
    string Model,
    int Dimensions,
    IReadOnlyList<string> Texts,
    string? TaskType = null,
    int BatchSize = 45,
    int RequestsPerMinute = 90);

public sealed record InlineImage(byte[] Bytes, string MimeType);

/// <param name="ResponseSchema">A Gemini response schema (OpenAPI subset). When set, the reply is JSON.</param>
public sealed record ChatRequest(
    string Model,
    string? SystemInstruction,
    string UserMessage,
    double? Temperature = null,
    int? MaxOutputTokens = null,
    JsonNode? ResponseSchema = null,
    IReadOnlyList<InlineImage>? Images = null);

public sealed record TokenUsage(int? PromptTokens, int? OutputTokens, int? ThoughtTokens, int? TotalTokens);

public sealed record ChatResult(string Text, string? FinishReason, TokenUsage Usage);

/// <summary>
/// The one place that talks to Gemini (REST). Adds what the raw API lacks: a per-attempt timeout, retries with backoff
/// for 429 / 5xx / timeouts, a per-model embedding rate limiter, and translation of HTTP errors into
/// <see cref="GeminiApiException"/> categories.
/// </summary>
public interface IGeminiClient
{
    bool IsConfigured { get; }

    /// <summary>One vector per text, in order. Vectors are returned as Gemini sent them (not normalized).</summary>
    Task<IReadOnlyList<float[]>> EmbedAsync(EmbedRequest request, CancellationToken cancellationToken);

    Task<ChatResult> GenerateAsync(ChatRequest request, CancellationToken cancellationToken);
}
