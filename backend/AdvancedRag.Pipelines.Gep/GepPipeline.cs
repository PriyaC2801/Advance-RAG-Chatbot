using System.Globalization;
using AdvancedRag.Core.Ai;
using AdvancedRag.Core.Rag;
using AdvancedRag.Core.Storage;
using AdvancedRag.Pipelines.Gep.Answering;
using AdvancedRag.Pipelines.Gep.Chunking;
using AdvancedRag.Pipelines.Gep.Evaluation;
using AdvancedRag.Pipelines.Gep.Models;

namespace AdvancedRag.Pipelines.Gep;

/// <summary>
/// The World Bank "Global Economic Prospects" (June 2026) pipeline: structure-aware chunking that follows the report's
/// chapters, regions, tables and figures, hybrid retrieval over PostgreSQL + pgvector, and a grounded, validated answer.
/// This class only maps the original app's services onto the unified <see cref="IRagPipeline"/> contract.
/// </summary>
public sealed class GepPipeline(
    GepIngestionService ingestion,
    ChunkingService chunking,
    ChatService chat,
    EvaluationService evaluation,
    Retrieval.GepVectorStore store,
    IGeminiClient gemini,
    IOptions<GepOptions> options,
    StoragePaths paths,
    ILogger<GepPipeline> logger) : IEvaluablePipeline
{
    public const string Id = "gep";

    private static readonly DocumentInfo Document = new(
        Id,
        "Global Economic Prospects",
        "World Bank · June 2026 · 200 pages",
        "The World Bank's twice-yearly report on the global economy: the outlook for advanced and emerging economies, " +
        "six regional outlooks, special-topic chapters on debt and fiscal policy, statistical tables and chart data. " +
        "Questions are answered only from passages retrieved from the report, with citations to chapter, section and page.",
        "GEP-Jun-2026.pdf",
        [
            new("Extraction", "PdfPig positioned-glyph extraction",
                "Rebuilds text lines from individual letters at x/y positions, splits the two text columns at the gutter and keeps font name and size on every line."),
            new("Extraction", "Font-driven layout classifier",
                "Chapter, section, subsection, box, table and figure roles are decided from font size and font name (26pt Garamond = chapter title, 16pt Helvetica Bold = section, small Helvetica = chart text), not from keywords."),
            new("Extraction", "Document structure walker",
                "Reads pages like a reader and tracks chapter, section, subsection, region and box, so text under 'South Asia' can never land in the same segment as 'Sub-Saharan Africa'."),
            new("Extraction", "Chart reading with a vision model",
                "Each figure's page is sent as a one-page PDF to Gemini gemini-3.5-flash, which transcribes the chart values; results are cached in figure-data.json and marked as approximate."),
            new("Chunking", "Structure-aware chunker",
                "Prose is cut at sentence boundaries (up to 1500 characters, 300 overlap, tiny tail merged); box articles, regional outlooks, annexes and references keep their own content type."),
            new("Chunking", "Self-describing table rows",
                "Tables are chunked by row group and every value gets its column label ('India: 2026f: 6.6; ...'), so a chunk without the header row can never confuse forecast years; table notes are stored beside the table."),
            new("Chunking", "Figure chunks",
                "A figure becomes one chunk: title, caption and the chart data read by the vision model."),
            new("Chunking", "Dehyphenation learned from the report",
                "Words split across lines are rejoined only when the joined form appears un-split elsewhere in the document ('infla-tion' joins, 'non-resource' stays)."),
            new("Enrichment", "Heading-path metadata",
                "Every chunk carries chapter, section, subsection, region (set only from headings or table region rows), content type, PDF page range and printed page range."),
            new("Enrichment", "Table notes lookup",
                "The notes printed under a table (fiscal-year bases, units) are attached to table chunks at answer time so 'India 2026f' is not misread as calendar year."),
            new("Embedding", "Gemini gemini-embedding-2 (768 dimensions)",
                "Documents are embedded as 'title: <report, edition, heading path> | text: <chunk>', questions as 'task: question answering | query: <question>'; vectors are normalized, batches of 45 within a 90 texts/minute budget."),
            new("Embedding", "Hash-based incremental embedding",
                "Each vector is stored with a hash of the exact embedded text, so re-ingesting only embeds chunks that changed."),
            new("Retrieval", "pgvector cosine similarity",
                "Similarity is computed in PostgreSQL (table gep_chunks, HNSW index) between the question vector and the candidate chunks."),
            new("Retrieval", "Question analysis and filters",
                "Detects 'Chapter 3', region names and aliases (SAR, SSA), countries (mapped to regions learned from the tables), 'Executive Summary' and comparisons, and narrows the candidates accordingly."),
            new("Retrieval", "Explained metadata adjustments",
                "Small score nudges, each recorded: +0.05 matching region, -0.05 other region, +0.02 heading match, -0.03 references and front matter."),
            new("Retrieval", "BM25 keyword boost and rescue",
                "Exact-word search over heading path and text adds up to +0.06 and rescues a chunk just below the 0.70 threshold when it contains most of the question's weighted terms."),
            new("Retrieval", "Per-region comparison retrieval",
                "A comparison question retrieves each named region separately so one region cannot crowd out the other."),
            new("Generation", "Grounded JSON-schema prompt",
                "Gemini gemini-3.5-flash-lite sees only the labelled excerpts (<source id=\"S1\">) within a 12,000-character budget and must reply {status, answer, citedSourceIds} with [S#] markers."),
            new("Generation", "Evidence assessment before generation",
                "Sufficient, Limited or Insufficient is decided from retrieval scores and region coverage; with no relevant chunk the model is not called and the fixed insufficient-evidence reply is returned."),
            new("Validation", "Answer validator",
                "Checks the reply is the requested JSON, removes citations to sources that were not supplied, refuses an answer with no valid citation and lets the model lower (never raise) the evidence status."),
            new("Evaluation", "Structural evaluation cases",
                "Eight questions check where evidence must come from (chapter, content type, regions), that regions are answered separately, that every number appears in the cited text and that off-topic questions get the fixed reply; no expected answer text is stored."),
        ],
        [
            "What is the growth outlook for South Asia?",
            "What growth is projected in 2026 for the region that includes Pakistan?",
            "Compare the growth outlook for South Asia and Sub-Saharan Africa.",
            "What are the growth forecasts for India and Bangladesh?",
            "What does the report say about hidden debt?",
            "According to Figure 1.1, what share of advanced economies had their 2026 growth forecast downgraded?",
        ],
        Modes: [],
        DefaultMode: null,
        SupportsEvaluation: true);

    public DocumentInfo Info => Document;

    // ---- status ----

    public async Task<PipelineStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var pdfFound = File.Exists(paths.Document(settings.FileName));
        var chunkIndex = await chunking.GetIndexAsync(cancellationToken);

        var loaded = false;
        string? message = null;
        try
        {
            loaded = await ingestion.TryLoadAsync(cancellationToken);
        }
        catch (VectorStoreUnavailableException ex)
        {
            message = ex.Message;
        }

        if (message is null && !loaded)
        {
            message = chunkIndex is null
                ? "Not ingested yet. Run ingestion to read the PDF, chunk it and build the index."
                : ingestion.IsIngesting
                    ? "An ingestion is running."
                    : "Chunks exist but the stored embeddings are missing or do not match them; run ingestion again.";
        }

        var chunks = chunkIndex?.Chunks ?? [];
        var details = new Dictionary<string, string>
        {
            ["indexLoaded"] = loaded.ToString(),
            ["sections"] = chunks.Select(c => (c.ChapterTitle, c.SectionTitle)).Distinct().Count().ToString(CultureInfo.InvariantCulture),
            ["chapters"] = chunks.Select(c => c.ChapterTitle).Distinct().Count().ToString(CultureInfo.InvariantCulture),
            ["table"] = settings.TableName,
            ["figurePagesWithChartData"] = chunks
                .Where(c => c.ContentType == ContentType.FigureCaption && c.Text.Contains(FigureDataService.DataHeader, StringComparison.Ordinal))
                .Select(c => c.PdfPageNumberStart).Distinct().Count().ToString(CultureInfo.InvariantCulture),
        };
        foreach (var group in chunks.GroupBy(c => c.ContentType).OrderBy(g => g.Key))
        {
            details[$"chunks.{group.Key}"] = group.Count().ToString(CultureInfo.InvariantCulture);
        }

        DateTimeOffset? indexedUtc = null;
        var embeddingsFile = Path.Combine(paths.Data(settings.DocumentFolder), Embeddings.EmbeddingStore.FileName);
        if (loaded && File.Exists(embeddingsFile)) indexedUtc = File.GetLastWriteTimeUtc(embeddingsFile);

        return new PipelineStatus(Id, loaded ? IndexState.Ready : IndexState.NotIndexed, pdfFound,
            chunkIndex?.PageCount ?? 0, chunks.Count, gemini.IsConfigured,
            store.ModelId ?? settings.EmbeddingModelId, indexedUtc, message, details);
    }

    // ---- ingestion ----

    public Task<IngestResult> IngestAsync(bool force, string? mode, IProgress<string> progress, CancellationToken cancellationToken) =>
        ingestion.IngestAsync(force, progress, cancellationToken);

    // ---- asking ----

    public async Task<AskResult> AskAsync(string question, string? mode, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(question) || question.Trim().Length < 3)
        {
            throw new BadRequestException("Please ask a question of at least 3 characters.");
        }
        if (question.Length > 1000)
        {
            throw new BadRequestException("The question is too long (limit: 1000 characters).");
        }

        if (!await ingestion.TryLoadAsync(cancellationToken) || store.Chunks.Count == 0)
        {
            throw new IndexNotReadyException(ingestion.IsIngesting
                ? "The Global Economic Prospects index is being built; try again when ingestion has finished."
                : "The Global Economic Prospects report has not been indexed yet. Run ingestion first.");
        }

        return await chat.AskAsync(question, cancellationToken);
    }

    // ---- chunks ----

    public async Task<ChunkPage> GetChunksAsync(ChunkQuery query, CancellationToken cancellationToken)
    {
        var chunkIndex = await chunking.GetIndexAsync(cancellationToken);
        if (chunkIndex is null) return new ChunkPage(0, []);

        IEnumerable<DocumentChunk> matches = chunkIndex.Chunks;
        if (!string.IsNullOrWhiteSpace(query.Kind))
        {
            matches = matches.Where(c => string.Equals(c.ContentType.ToString(), query.Kind.Trim(), StringComparison.OrdinalIgnoreCase));
        }
        if (query.Page is { } page)
        {
            matches = matches.Where(c => c.PdfPageNumberStart <= page && page <= c.PdfPageNumberEnd);
        }
        if (!string.IsNullOrWhiteSpace(query.Contains))
        {
            matches = matches.Where(c => c.Text.Contains(query.Contains, StringComparison.OrdinalIgnoreCase));
        }

        var all = matches.OrderBy(c => c.ChunkIndex).ToList();
        var items = all.Skip(Math.Max(0, query.Skip)).Take(Math.Clamp(query.Take, 1, 500)).Select(ToView).ToList();
        return new ChunkPage(all.Count, items);
    }

    private static ChunkView ToView(DocumentChunk c)
    {
        var metadata = new Dictionary<string, string>
        {
            ["chapter"] = $"{(c.ChapterNumber is { } n ? $"Chapter {n}: " : "")}{c.ChapterTitle}",
            ["section"] = c.SectionTitle,
            ["headingPath"] = c.HeadingPath,
            ["characters"] = c.Text.Length.ToString(CultureInfo.InvariantCulture),
        };
        if (c.SubsectionTitle is not null) metadata["subsection"] = c.SubsectionTitle;
        if (c.Region is not null) metadata["region"] = c.Region;
        if (c.PrintedPageStart is not null)
        {
            metadata["printedPages"] = c.PrintedPageEnd is null || c.PrintedPageEnd == c.PrintedPageStart
                ? c.PrintedPageStart : $"{c.PrintedPageStart}–{c.PrintedPageEnd}";
        }
        return new ChunkView(c.Id, c.ChunkIndex, c.ContentType.ToString(), c.PdfPageNumberStart, c.PdfPageNumberEnd,
            c.HeadingPath, c.Text, metadata);
    }

    // ---- evaluation ----

    public async Task<EvalReport> RunEvaluationAsync(EvalRequest request, CancellationToken cancellationToken)
    {
        if (!await ingestion.TryLoadAsync(cancellationToken) || store.Chunks.Count == 0)
        {
            throw new IndexNotReadyException("The Global Economic Prospects report has not been indexed yet. Run ingestion first.");
        }
        logger.LogInformation("Running the GEP evaluation (repeat {Repeat}, case {Case})", request.Repeat, request.CaseId ?? "all");
        return await evaluation.RunAsync(request.CaseId, request.Repeat, cancellationToken);
    }

    public async Task<IReadOnlyList<EvalReport>> GetLatestEvaluationsAsync(CancellationToken cancellationToken)
    {
        var latest = await evaluation.LoadLatestAsync(cancellationToken);
        return latest is null ? [] : [latest];
    }
}
