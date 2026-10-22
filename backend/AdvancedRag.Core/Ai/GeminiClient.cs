using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace AdvancedRag.Core.Ai;

public sealed class GeminiClient(
    HttpClient http,
    IOptions<GeminiOptions> options,
    EmbeddingRateLimiter rateLimiter,
    ILogger<GeminiClient> logger) : IGeminiClient
{
    private GeminiOptions Settings => options.Value;

    public bool IsConfigured => Settings.IsApiKeyConfigured;

    public async Task<IReadOnlyList<float[]>> EmbedAsync(EmbedRequest request, CancellationToken cancellationToken)
    {
        var results = new List<float[]>(request.Texts.Count);
        foreach (var batch in request.Texts.Chunk(Math.Max(1, request.BatchSize)))
        {
            var requests = new JsonArray();
            foreach (var text in batch)
            {
                var item = new JsonObject
                {
                    ["model"] = $"models/{request.Model}",
                    ["content"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = text }) },
                    ["outputDimensionality"] = request.Dimensions,
                };
                if (request.TaskType is not null) item["taskType"] = request.TaskType;
                requests.Add(item);
            }

            var json = await SendAsync(
                $"models/{request.Model}:batchEmbedContents", new JsonObject { ["requests"] = requests }, cancellationToken,
                beforeEachAttempt: ct => rateLimiter.WaitAsync(request.Model, batch.Length, request.RequestsPerMinute, ct));

            var embeddings = json["embeddings"]?.AsArray();
            if (embeddings is null || embeddings.Count != batch.Length)
            {
                throw new GeminiApiException(GeminiErrorKind.InvalidResponse,
                    $"Expected {batch.Length} embeddings but received {embeddings?.Count ?? 0}.");
            }

            foreach (var e in embeddings)
            {
                var values = e?["values"]?.AsArray().Select(v => v!.GetValue<float>()).ToArray()
                    ?? throw new GeminiApiException(GeminiErrorKind.InvalidResponse, "An embedding had no values.");
                if (values.Length != request.Dimensions)
                {
                    throw new GeminiApiException(GeminiErrorKind.InvalidResponse,
                        $"Expected a {request.Dimensions}-dimension vector but received {values.Length}.");
                }
                results.Add(values);
            }

            if (request.Texts.Count > batch.Length)
            {
                logger.LogInformation("Embedded {Done}/{Total} texts with {Model}", results.Count, request.Texts.Count, request.Model);
            }
        }
        return results;
    }

    public async Task<ChatResult> GenerateAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        var parts = new JsonArray(new JsonObject { ["text"] = request.UserMessage });
        foreach (var image in request.Images ?? [])
        {
            parts.Add(new JsonObject
            {
                ["inlineData"] = new JsonObject
                {
                    ["mimeType"] = image.MimeType,
                    ["data"] = Convert.ToBase64String(image.Bytes),
                },
            });
        }

        var generationConfig = new JsonObject();
        if (request.Temperature is { } t) generationConfig["temperature"] = t;
        if (request.MaxOutputTokens is { } max) generationConfig["maxOutputTokens"] = max;
        if (request.ResponseSchema is not null)
        {
            generationConfig["responseMimeType"] = "application/json";
            generationConfig["responseSchema"] = request.ResponseSchema.DeepClone();
        }

        var body = new JsonObject
        {
            ["contents"] = new JsonArray(new JsonObject { ["role"] = "user", ["parts"] = parts }),
            ["generationConfig"] = generationConfig,
        };
        if (!string.IsNullOrWhiteSpace(request.SystemInstruction))
        {
            body["systemInstruction"] = new JsonObject
            {
                ["parts"] = new JsonArray(new JsonObject { ["text"] = request.SystemInstruction }),
            };
        }

        var json = await SendAsync($"models/{request.Model}:generateContent", body, cancellationToken);

        var candidate = json["candidates"]?.AsArray().FirstOrDefault();
        if (candidate is null)
        {
            var blocked = json["promptFeedback"]?["blockReason"]?.GetValue<string>();
            throw new GeminiApiException(GeminiErrorKind.InvalidResponse,
                blocked is null ? "Gemini returned no answer." : $"Gemini blocked the prompt ({blocked}).");
        }

        var text = string.Concat((candidate["content"]?["parts"]?.AsArray() ?? [])
            .Select(p => p?["text"]?.GetValue<string>()));
        var finishReason = candidate["finishReason"]?.GetValue<string>();
        var usage = json["usageMetadata"];

        logger.LogInformation("Gemini {Model} answered: finish={Finish}, prompt={Prompt} tokens, output={Output} tokens",
            request.Model, finishReason, usage?["promptTokenCount"], usage?["candidatesTokenCount"]);

        return new ChatResult(text, finishReason, new TokenUsage(
            usage?["promptTokenCount"]?.GetValue<int>(),
            usage?["candidatesTokenCount"]?.GetValue<int>(),
            usage?["thoughtsTokenCount"]?.GetValue<int>(),
            usage?["totalTokenCount"]?.GetValue<int>()));
    }

    /// <summary>POSTs a JSON body with timeout and retries; returns the parsed JSON response.</summary>
    private async Task<JsonNode> SendAsync(string path, JsonNode body, CancellationToken cancellationToken,
        Func<CancellationToken, Task>? beforeEachAttempt = null)
    {
        if (!Settings.IsApiKeyConfigured)
        {
            throw new GeminiApiException(GeminiErrorKind.MissingApiKey,
                "Gemini API key is not configured. Run: dotnet user-secrets set \"Gemini:ApiKey\" \"<your-key>\" in backend/AdvancedRag.Api and restart the API.");
        }

        var url = $"{Settings.BaseUrl.TrimEnd('/')}/{path}";
        for (var attempt = 0; ; attempt++)
        {
            if (beforeEachAttempt is not null) await beforeEachAttempt(cancellationToken);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Settings.RequestTimeoutSeconds));

            GeminiApiException failure;
            TimeSpan? delay = null;
            try
            {
                using var message = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
                message.Headers.Add("x-goog-api-key", Settings.ApiKey);
                using var response = await http.SendAsync(message, timeout.Token);
                var text = await response.Content.ReadAsStringAsync(timeout.Token);

                if (response.IsSuccessStatusCode)
                {
                    return JsonNode.Parse(text) ?? throw new GeminiApiException(GeminiErrorKind.InvalidResponse, "Gemini returned an empty response.");
                }

                var (detail, suggested) = ReadError(text, response.StatusCode);
                failure = Classify(response.StatusCode, detail);
                if (failure.Kind == GeminiErrorKind.RateLimitedOrQuotaExhausted)
                {
                    // Per-minute quotas reset within a minute: wait at least Google's suggested delay and never less
                    // than our exponential backoff (15, 30, 60 s).
                    var backoff = TimeSpan.FromSeconds(Math.Min(60, Settings.RateLimitBackoffSeconds * Math.Pow(2, attempt)));
                    delay = suggested is { } d && d > backoff ? d : backoff;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                failure = new GeminiApiException(GeminiErrorKind.Timeout, "Gemini did not respond in time.");
            }
            catch (HttpRequestException ex)
            {
                failure = new GeminiApiException(GeminiErrorKind.ServerError, "Could not reach the Gemini API.", inner: ex);
            }

            var retryable = failure.Kind is GeminiErrorKind.RateLimitedOrQuotaExhausted or GeminiErrorKind.ServerError or GeminiErrorKind.Timeout;
            if (!retryable || attempt >= Settings.MaxRetries) throw failure;

            delay ??= TimeSpan.FromSeconds(Math.Pow(2, attempt + 1)); // 2, 4, 8, 16 s
            logger.LogWarning("Gemini call failed ({Kind}); retry {Attempt} in {Delay}s", failure.Kind, attempt + 1, delay.Value.TotalSeconds);
            await Task.Delay(delay.Value, cancellationToken);
        }
    }

    private static GeminiApiException Classify(HttpStatusCode code, string detail) => code switch
    {
        HttpStatusCode.TooManyRequests => new(GeminiErrorKind.RateLimitedOrQuotaExhausted, $"Gemini rate limit or quota reached: {detail}", (int)code),
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new(GeminiErrorKind.InvalidApiKey, "Gemini rejected the API key.", (int)code),
        HttpStatusCode.BadRequest when detail.Contains("API key", StringComparison.OrdinalIgnoreCase) =>
            new(GeminiErrorKind.InvalidApiKey, "Gemini rejected the API key.", (int)code),
        _ when (int)code >= 500 => new(GeminiErrorKind.ServerError, $"Gemini server error: {detail}", (int)code),
        _ => new(GeminiErrorKind.InvalidRequest, $"Gemini rejected the request: {detail}", (int)code),
    };

    /// <summary>Extracts Google's error message and its RetryInfo.retryDelay (e.g. "13s") from the error body.</summary>
    internal static (string Message, TimeSpan? RetryDelay) ReadError(string? body, HttpStatusCode code)
    {
        var fallback = code.ToString();
        if (string.IsNullOrWhiteSpace(body)) return (fallback, null);
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("error", out var error)) return (fallback, null);

            var message = error.TryGetProperty("message", out var m) ? m.GetString() ?? fallback : fallback;
            message = message.Replace("\n", " ").Trim();
            if (message.Length > 500) message = message[..500];

            TimeSpan? delay = null;
            if (error.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Array)
            {
                foreach (var detail in details.EnumerateArray())
                {
                    if (detail.TryGetProperty("retryDelay", out var rd) && rd.GetString() is { } text && text.EndsWith('s')
                        && double.TryParse(text[..^1], CultureInfo.InvariantCulture, out var seconds))
                    {
                        delay = TimeSpan.FromSeconds(seconds);
                        break;
                    }
                }
            }
            return (message, delay);
        }
        catch (JsonException)
        {
            return (fallback, null);
        }
    }
}
