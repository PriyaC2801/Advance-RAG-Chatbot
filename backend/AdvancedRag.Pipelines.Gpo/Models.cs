namespace AdvancedRag.Pipelines.Gpo;

/// <summary>The raw text of one page of the PDF, before chunking.</summary>
internal record ExtractedPage(int PageNumber, string Text);

/// <summary>A chunk of page text before it has an embedding attached.</summary>
internal record ChunkDraft(int ChunkIndex, int PageNumber, string Text);

/// <summary>A stored chunk.</summary>
internal sealed record DocumentChunk(string Key, int ChunkIndex, int PageNumber, string Text);

/// <summary>A chunk returned from a search (or fusion) with its score.</summary>
internal record ScoredChunk(DocumentChunk Chunk, double? Score);

/// <summary>A fused result together with where each retriever ranked it (1-based).</summary>
internal sealed record FusedChunk(DocumentChunk Chunk, double Score, int? DenseRank, int? KeywordRank);
