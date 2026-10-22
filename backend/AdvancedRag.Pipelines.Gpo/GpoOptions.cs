namespace AdvancedRag.Pipelines.Gpo;

/// <summary>Settings of the GPO pipeline, bound from <c>Pipelines:Gpo</c>. Defaults are the original project's values.</summary>
public sealed class GpoOptions
{
    public const string SectionName = "Pipelines:Gpo";

    public string FileName { get; set; } = "GPO-Annual-report.pdf";
    public string TableName { get; set; } = "gpo_chunks";

    public string EmbeddingModel { get; set; } = "gemini-embedding-001";
    public int EmbeddingDimensions { get; set; } = 768;
    public string ChatModel { get; set; } = "gemini-3.5-flash-lite";
    public double Temperature { get; set; } = 0.1;

    /// <summary>Texts per batchEmbedContents call and the client-side budget of embedded texts per minute.</summary>
    public int EmbeddingBatchSize { get; set; } = 20;
    public int EmbeddingRequestsPerMinute { get; set; } = 90;

    /// <summary>Dense and keyword search each pull this many candidates; fusing them picks the final TopK.</summary>
    public int CandidatePoolSize { get; set; } = 20;
    public int TopK { get; set; } = 5;

    /// <summary>The constant from the original RRF paper and the usual default.</summary>
    public int RrfK { get; set; } = 60;

    public int WordsPerChunk { get; set; } = 300;
    public int MaxOverlapWords { get; set; } = 60;
    public int MaxOverlapSentences { get; set; } = 2;
}
