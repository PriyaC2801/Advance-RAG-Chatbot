namespace AdvancedRag.Pipelines.Imac;

/// <summary>Settings of the iMac pipeline, bound from the "Pipelines:Imac" configuration section. Defaults are the original app's values.</summary>
public sealed class ImacOptions
{
    public const string SectionName = "Pipelines:Imac";

    public string DocumentFileName { get; set; } = "iMac_PER_Oct2024.pdf";

    public string EmbeddingModel { get; set; } = "gemini-embedding-001";

    /// <summary>Answers questions. Free-tier Gemini allows only a few chat requests per day per model.</summary>
    public string ChatModel { get; set; } = "gemini-3.5-flash-lite";

    /// <summary>Describes chart pages. A separate model has a separate free-tier quota from <see cref="ChatModel"/>; must accept images.</summary>
    public string VisionModel { get; set; } = "gemini-3.1-flash-lite";

    /// <summary>Must match vector(1536) in the imac_chunks table. Vectors from different models/sizes are not comparable.</summary>
    public int EmbeddingDimensions { get; set; } = 1536;

    /// <summary>Free-tier embedding quotas count every text in a batch, so batches stay small.</summary>
    public int EmbeddingBatchSize { get; set; } = 32;

    /// <summary>Client-side budget of embedded texts per minute (free tier: 100).</summary>
    public int EmbeddingRequestsPerMinute { get; set; } = 90;

    /// <summary>Send RETRIEVAL_DOCUMENT / RETRIEVAL_QUERY with embeddings (Gemini's recommended use). The original app sent none.</summary>
    public bool UseEmbeddingTaskTypes { get; set; } = true;

    /// <summary>How many passages the model gets to read.</summary>
    public int TopK { get; set; } = 5;

    /// <summary>Dense and keyword search each pull this many candidates before they are fused.</summary>
    public int CandidatesPerList { get; set; } = 20;

    /// <summary>Reciprocal Rank Fusion constant: score = 1 / (RrfK + rank).</summary>
    public int RrfK { get; set; } = 60;

    /// <summary>Sentence groups for text chunks are cut at about this many (estimated) tokens.</summary>
    public int MaxChunkTokens { get; set; } = 220;
}
