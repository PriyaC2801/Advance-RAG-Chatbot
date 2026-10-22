namespace AdvancedRag.Core.Rag;

// ---- What the UI shows about a document and how it is processed ----------------------------------------------

/// <summary>One technique a pipeline uses, grouped by stage (Extraction, Chunking, Embedding, Retrieval, Generation, Validation, Evaluation).</summary>
public sealed record Technique(string Stage, string Name, string Description);

/// <summary>A retrieval variant a pipeline can run (for example the iMac pipeline's dense / hybrid / advanced modes).</summary>
public sealed record ModeInfo(string Id, string Label, string Description);

public sealed record DocumentInfo(
    string Id,                                   // "gep" | "gpo" | "imac" (used in URLs)
    string Title,
    string Subtitle,
    string Description,
    string FileName,
    IReadOnlyList<Technique> Techniques,
    IReadOnlyList<string> SampleQuestions,
    IReadOnlyList<ModeInfo> Modes,               // empty when the pipeline has a single mode
    string? DefaultMode,
    bool SupportsEvaluation);

// ---- Status / ingestion ------------------------------------------------------------------------------------

public enum IndexState { NotIndexed, Indexing, Ready, Failed }

public sealed record PipelineStatus(
    string DocumentId,
    IndexState State,
    bool PdfFound,
    int PageCount,
    int ChunkCount,
    bool ApiKeyConfigured,
    string? EmbeddingModel,
    DateTimeOffset? IndexedUtc,
    string? Message,                              // progress while Indexing, error text when Failed
    IReadOnlyDictionary<string, string> Details); // pipeline-specific facts (chunk kinds, index loaded, ...)

public sealed record IngestResult(
    string DocumentId,
    string Status,                                // "Ingested" | "UpToDate"
    int ChunkCount,
    int EmbeddedNow,
    int ReusedEmbeddings,
    TimeSpan Duration,
    IReadOnlyDictionary<string, string> Details);

// ---- Asking ------------------------------------------------------------------------------------------------

public sealed record AskRequest(string DocumentId, string Question, string? Mode = null);

/// <summary>A source the answer was built from. Labels match the inline citations in the answer text ("S1" for [S1], "1" for [1]).</summary>
public sealed record Citation(
    string Label,
    string ChunkId,
    int? Page,
    string? PageLabel,                            // printed page number when it differs from the PDF page
    string? Section,                              // heading path, e.g. "Chapter 2 > South Asia > Outlook"
    string? Kind,                                 // table | text | vision | narrative | figure ...
    string Excerpt,
    double? Score,
    IReadOnlyDictionary<string, string>? Details);

/// <summary>One stage of the pipeline for this question; the UI renders these as the "how this answer was generated" trace.</summary>
public sealed record PipelineStep(string Name, string Description, long? DurationMs, IReadOnlyDictionary<string, string>? Details);

/// <summary>A retrieved passage with how it scored. <see cref="Notes"/> explains score adjustments in plain words.</summary>
public sealed record RankedPassage(
    string Label,
    string ChunkId,
    double Score,
    int? DenseRank,
    int? KeywordRank,
    IReadOnlyList<string> Notes);

public sealed record AskDebug(
    string Pipeline,                              // human readable, e.g. "Structure-aware report pipeline"
    string? Mode,
    IReadOnlyList<PipelineStep> Steps,
    IReadOnlyList<RankedPassage> Passages,
    string? Model,
    string? SystemPrompt,
    string? UserMessage,
    long TotalMs,
    IReadOnlyDictionary<string, string> Extra);

public sealed record AskResult(
    string DocumentId,
    string Question,
    string Answer,
    string EvidenceStatus,                        // Sufficient | Limited | Insufficient (pipelines without a graded check use Sufficient / Insufficient)
    bool HasEvidence,
    IReadOnlyList<Citation> Citations,
    AskDebug Debug);

// ---- Chunks (developer view) ------------------------------------------------------------------------------

public sealed record ChunkQuery(string? Kind = null, int? Page = null, string? Contains = null, int Skip = 0, int Take = 50);

public sealed record ChunkView(
    string Id,
    int Index,
    string? Kind,
    int? Page,
    int? PageEnd,
    string? Section,
    string Text,
    IReadOnlyDictionary<string, string> Metadata);

public sealed record ChunkPage(int Total, IReadOnlyList<ChunkView> Items);

// ---- Evaluation --------------------------------------------------------------------------------------------

public sealed record EvalCaseResult(
    string Id,
    string Question,
    bool Passed,
    string? Answer,
    IReadOnlyList<string> Failures,
    IReadOnlyDictionary<string, string> Metrics);

public sealed record EvalReport(
    string DocumentId,
    string? Mode,
    DateTimeOffset RanUtc,
    int Passed,
    int Total,
    IReadOnlyList<EvalCaseResult> Cases,
    IReadOnlyDictionary<string, string> Summary);

public sealed record EvalRequest(string? Mode = null, int Repeat = 1, string? CaseId = null);

// ---- The contract every pipeline implements ----------------------------------------------------------------

/// <summary>
/// One document with its own way of ingesting, chunking, retrieving and answering. Implementations are registered as
/// <see cref="IRagPipeline"/> singletons; the controllers only ever talk to this interface, so adding a fourth
/// document means adding a fourth pipeline, not touching the API.
/// </summary>
public interface IRagPipeline
{
    DocumentInfo Info { get; }

    Task<PipelineStatus> GetStatusAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Builds (or refreshes) the index. Called on a background task by <see cref="IngestionCoordinator"/>, so it may take minutes;
    /// report progress with <paramref name="progress"/> ("Reading page 3 of 12", "Embedding 45/113 chunks").
    /// Must be safe to call when the index is already built (reuse cached embeddings unless <paramref name="force"/>).
    /// </summary>
    Task<IngestResult> IngestAsync(bool force, string? mode, IProgress<string> progress, CancellationToken cancellationToken);

    /// <summary>Answers one question from this document. Throws <see cref="IndexNotReadyException"/> if not indexed.</summary>
    Task<AskResult> AskAsync(string question, string? mode, CancellationToken cancellationToken);

    Task<ChunkPage> GetChunksAsync(ChunkQuery query, CancellationToken cancellationToken);
}

/// <summary>Implemented by pipelines whose <see cref="DocumentInfo.SupportsEvaluation"/> is true.</summary>
public interface IEvaluablePipeline : IRagPipeline
{
    Task<EvalReport> RunEvaluationAsync(EvalRequest request, CancellationToken cancellationToken);

    /// <summary>The last saved report(s); empty when none was run yet.</summary>
    Task<IReadOnlyList<EvalReport>> GetLatestEvaluationsAsync(CancellationToken cancellationToken);
}
