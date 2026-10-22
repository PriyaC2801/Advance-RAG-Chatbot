namespace AdvancedRag.Core.Ai;

/// <summary>
/// Transport settings shared by every pipeline. The API key must NEVER be committed:
///   dotnet user-secrets set "Gemini:ApiKey" "your-key"   (or the environment variable Gemini__ApiKey).
/// Model names live in each pipeline's own options, because each pipeline has its own embedding model and dimensions.
/// </summary>
public sealed class GeminiOptions
{
    public const string SectionName = "Gemini";

    public string ApiKey { get; set; } = string.Empty;

    public string BaseUrl { get; set; } = "https://generativelanguage.googleapis.com/v1beta";

    /// <summary>Per-attempt timeout. Vision calls on a full page can be slow, so this is generous.</summary>
    public int RequestTimeoutSeconds { get; set; } = 90;

    /// <summary>Retries for 429, 5xx and timeouts, with backoff.</summary>
    public int MaxRetries { get; set; } = 4;

    /// <summary>First wait after HTTP 429; doubles per retry (capped at 60 s) because quotas are per minute.</summary>
    public double RateLimitBackoffSeconds { get; set; } = 15;

    public bool IsApiKeyConfigured => !string.IsNullOrWhiteSpace(ApiKey);
}
