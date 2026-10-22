namespace AdvancedRag.Pipelines.Gep;

/// <summary>
/// Everything the Global Economic Prospects pipeline can be tuned with (configuration section "Pipelines:Gep").
/// The defaults are the values the original single-document app shipped with, calibrated for
/// gemini-embedding-2 / 768 dimensions. The Gemini API key and transport settings (timeouts, retries) are shared
/// and live in the "Gemini" section; model names live here because each pipeline picks its own.
/// </summary>
public sealed class GepOptions
{
    public const string SectionName = "Pipelines:Gep";

    // ---- the document ----

    /// <summary>Stable id stored on every chunk, e.g. "gep-2026-06-c0412".</summary>
    public string DocumentId { get; set; } = "gep-2026-06";
    public string Title { get; set; } = "Global Economic Prospects";
    public string Edition { get; set; } = "June 2026";

    /// <summary>File name inside the shared documents folder.</summary>
    public string FileName { get; set; } = "GEP-Jun-2026.pdf";

    /// <summary>Sub-folder of data/ where chunks.json, embeddings.json and figure-data.json are kept.</summary>
    public string DocumentFolder { get; set; } = "gep";

    /// <summary>PostgreSQL table that holds one row per chunk. Letters, digits and underscores only.</summary>
    public string TableName { get; set; } = "gep_chunks";

    // ---- embeddings ----

    public string EmbeddingModel { get; set; } = "gemini-embedding-2";

    /// <summary>Vector length. 768, 1536 and 3072 are the documented recommended sizes.</summary>
    public int EmbeddingDimensions { get; set; } = 768;

    /// <summary>Texts per batchEmbedContents call. 45 lets two batches fit in one 90-per-minute budget.</summary>
    public int EmbeddingBatchSize { get; set; } = 45;

    /// <summary>
    /// Client-side budget of embedded texts per minute. Gemini counts EVERY text in a batch as one request;
    /// the free tier allows 100 per minute per model, so we stay a little below that.
    /// </summary>
    public int EmbeddingRequestsPerMinute { get; set; } = 90;

    /// <summary>Task instruction written into the query text: "task: {this} | query: {question}".</summary>
    public string QueryTaskInstruction { get; set; } = "question answering";

    // ---- generation ----

    /// <summary>Stable, fast, and supports JSON-schema output.</summary>
    public string GenerationModel { get; set; } = "gemini-3.5-flash-lite";

    /// <summary>
    /// Sampling temperature. null = the model's default, which Google recommends for Gemini 3+ models.
    /// Grounding comes from the prompt and validation, not from this.
    /// </summary>
    public double? GenerationTemperature { get; set; }

    /// <summary>Cap on answer length (includes the model's internal "thinking" tokens).</summary>
    public int GenerationMaxOutputTokens { get; set; } = 4096;

    // ---- per-stage settings ----

    public ChunkingOptions Chunking { get; set; } = new();
    public FigureDataOptions FigureData { get; set; } = new();
    public RetrievalOptions Retrieval { get; set; } = new();
    public AnsweringOptions Answering { get; set; } = new();
    public EvaluationOptions Evaluation { get; set; } = new();

    /// <summary>
    /// Id of the embedding setup, stored with the vectors; they are reused only when it matches. The "TextPrefix" suffix
    /// records how the task is expressed (inside the text, as gemini-embedding-2 requires).
    /// </summary>
    public string EmbeddingModelId => $"{EmbeddingModel}/{EmbeddingDimensions}/TextPrefix";

    /// <summary>
    /// The list defaults are filled in here rather than by initializers: the configuration binder APPENDS to a list
    /// that already has items, so initializer defaults would be duplicated as soon as a config file lists them too.
    /// </summary>
    internal void ApplyListDefaults()
    {
        Chunking.SkippedFrontMatterTitles = Chunking.SkippedFrontMatterTitles is { Count: > 0 } ? Chunking.SkippedFrontMatterTitles : ["Summary of Contents", "Contents"];
        Chunking.RegionNames = Chunking.RegionNames is { Count: > 0 } ? Chunking.RegionNames :
        [
            "East Asia and Pacific",
            "Europe and Central Asia",
            "Latin America and the Caribbean",
            "Middle East, North Africa, Afghanistan, and Pakistan",
            "South Asia",
            "Sub-Saharan Africa",
        ];
        if (Retrieval.RegionAliases.Count == 0)
        {
            Retrieval.RegionAliases = new Dictionary<string, List<string>>
            {
                ["East Asia and Pacific"] = ["EAP", "East Asia"],
                ["Europe and Central Asia"] = ["ECA"],
                ["Latin America and the Caribbean"] = ["LAC", "Latin America"],
                ["Middle East, North Africa, Afghanistan, and Pakistan"] = ["MNA", "Middle East and North Africa"],
                ["South Asia"] = ["SAR", "South Asian"],
                ["Sub-Saharan Africa"] = ["SSA", "Sub-Saharan African"],
            };
        }
    }
}
