using AdvancedRag.Core.Ai;
using AdvancedRag.Core.Storage;

namespace AdvancedRag.Pipelines.Gep.Embeddings;

/// <summary>A document text to embed, with a title giving its location in the report.</summary>
public sealed record EmbeddingInput(string Title, string Text);

/// <summary>
/// Embeddings through the shared Gemini client. The client owns the HTTP details, retries and the per-minute budget;
/// this class keeps what is specific to this report: the gemini-embedding-2 text prefixes, batching, normalization.
/// </summary>
public sealed class GepEmbedder(IGeminiClient gemini, IOptions<GepOptions> options, ILogger<GepEmbedder> logger)
{
    private GepOptions Settings => options.Value;

    /// <summary>
    /// Stored vectors are reused only when this id matches. The "TextPrefix" suffix records how the task was
    /// expressed (inside the text, as gemini-embedding-2 requires); it matches the id the original app wrote,
    /// so existing embeddings stay valid.
    /// </summary>
    public string ModelId => Settings.EmbeddingModelId;

    public int Dimensions => Settings.EmbeddingDimensions;

    /// <summary>Embeds the inputs in slices of two batches, reporting progress after each slice.</summary>
    public async Task<IReadOnlyList<float[]>> EmbedDocumentsAsync(IReadOnlyList<EmbeddingInput> inputs,
        IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var results = new List<float[]>(inputs.Count);
        foreach (var slice in inputs.Chunk(Math.Max(1, Settings.EmbeddingBatchSize) * 2))
        {
            var vectors = await EmbedAsync(slice.Select(DocumentText).ToList(), cancellationToken);
            results.AddRange(vectors);
            progress?.Report($"Embedding {results.Count}/{inputs.Count} chunks");
            logger.LogInformation("Embedded {Done}/{Total} chunks", results.Count, inputs.Count);
        }
        return results;
    }

    public async Task<float[]> EmbedQueryAsync(string query, CancellationToken cancellationToken)
    {
        var text = $"task: {Settings.QueryTaskInstruction} | query: {query}";
        return (await EmbedAsync([text], cancellationToken))[0];
    }

    /// <summary>
    /// gemini-embedding-2 has no task field; the documented document format is "title: {title} | text: {content}".
    /// The chunk's heading path goes into the title so the vector "knows" the chunk is, e.g., in
    /// Chapter 2 > South Asia > Outlook.
    /// </summary>
    private static string DocumentText(EmbeddingInput input) => $"title: {input.Title} | text: {input.Text}";

    private async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        var embeddings = await gemini.EmbedAsync(new EmbedRequest(
            Settings.EmbeddingModel, Settings.EmbeddingDimensions, texts, TaskType: null,
            BatchSize: Settings.EmbeddingBatchSize, RequestsPerMinute: Settings.EmbeddingRequestsPerMinute), cancellationToken);

        // Defensive: normalize ourselves so cosine similarity is correct for any model/dimension.
        return embeddings.Select(e => VectorMath.Normalize(e)).ToList();
    }
}
