using AdvancedRag.Pipelines.Gep.Models;

namespace AdvancedRag.Pipelines.Gep.Retrieval;

public sealed record RetrievalRequest(
    string Question,
    int? ChapterFilter = null,
    string? RegionFilter = null,
    IReadOnlyList<ContentType>? ContentTypes = null,
    int? TopK = null,
    bool UseMetadataRanking = true);

/// <summary>One reason a chunk's score moved away from its raw similarity.</summary>
public sealed record ScoreAdjustment(string Reason, double Delta);

public sealed record ScoredChunk(
    DocumentChunk Chunk,
    int Rank,
    double Similarity,
    IReadOnlyList<ScoreAdjustment> Adjustments,
    double FinalScore,
    bool AboveThreshold,
    string? RetrievedForRegion);

public sealed record RetrievalResult(
    QueryAnalysis Analysis,
    IReadOnlyList<string> AppliedFilters,
    IReadOnlyList<string> Notes,
    int CandidateCount,
    double Threshold,
    string EmbeddingModel,
    IReadOnlyList<ScoredChunk> Results)
{
    public bool HasRelevantEvidence => Results.Any(r => r.AboveThreshold);

    /// <summary>Timings of the stages inside the search, for the debug trace.</summary>
    public long AnalysisMilliseconds { get; init; }
    public long EmbeddingMilliseconds { get; init; }
    public int KeywordMatchCount { get; init; }
}

