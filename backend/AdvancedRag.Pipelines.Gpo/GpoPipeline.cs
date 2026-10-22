using System.Diagnostics;
using System.Globalization;
using AdvancedRag.Core.Ai;
using AdvancedRag.Core.Rag;
using AdvancedRag.Core.Storage;
using UglyToad.PdfPig;

namespace AdvancedRag.Pipelines.Gpo;

/// <summary>
/// The baseline ("classic") RAG pipeline over the GPO Annual Report: sentence chunks of ~300 words, Gemini embeddings,
/// dense + keyword retrieval fused by Reciprocal Rank Fusion, and one grounded generation call whose EVIDENCE: YES/NO
/// judgment is the only relevance filter (no similarity cutoff).
/// </summary>
internal sealed class GpoPipeline : IRagPipeline
{
    private const string NoEvidenceAnswer = "The document does not provide enough information to answer this question.";
    private const int ExcerptLength = 400;

    private readonly GpoOptions _options;
    private readonly IGeminiClient _gemini;
    private readonly GpoChunkStore _store;
    private readonly PdfPigTextExtractor _extractor;
    private readonly ChunkingService _chunker;
    private readonly StoragePaths _paths;
    private readonly GeminiOptions _geminiOptions;
    private readonly ILogger<GpoPipeline> _logger;

    // One ingestion at a time: it drops and recreates the table.
    private readonly SemaphoreSlim _ingestGate = new(1, 1);

    private int _pageCount;
    private string? _pageCountFile;
    private DateTimeOffset? _indexedUtc;

    public GpoPipeline(
        GpoOptions options,
        IGeminiClient gemini,
        GpoChunkStore store,
        PdfPigTextExtractor extractor,
        ChunkingService chunker,
        StoragePaths paths,
        IOptions<GeminiOptions> geminiOptions,
        ILogger<GpoPipeline> logger)
    {
        _options = options;
        _gemini = gemini;
        _store = store;
        _extractor = extractor;
        _chunker = chunker;
        _paths = paths;
        _geminiOptions = geminiOptions.Value;
        _logger = logger;

        Info = BuildInfo(options);
    }

    public DocumentInfo Info { get; }

    private string PdfPath => _paths.Document(_options.FileName);

    private static DocumentInfo BuildInfo(GpoOptions o) => new(
        Id: "gpo",
        Title: "GPO Annual Report",
        Subtitle: "U.S. Government Publishing Office · Fiscal 2018 · 82 pages",
        Description: "The 82-page annual report of the U.S. Government Publishing Office, answered with a classic hybrid RAG baseline: " +
                     "sentence chunks, dense + keyword retrieval fused by Reciprocal Rank Fusion, and a grounded answer with an evidence check.",
        FileName: o.FileName,
        Techniques:
        [
            new("Extraction", "PdfPig layout-ordered text", "Words are grouped into blocks (so two-column pages yield whole columns) and put in reading order; ligatures and line-end hyphenation are repaired."),
            new("Extraction", "Header / footer removal", "Short blocks repeated on many pages (running headers, footers, page numbers) are dropped as boilerplate."),
            new("Chunking", $"Sentence chunks (~{o.WordsPerChunk} words)", $"Whole sentences are packed into chunks of about {o.WordsPerChunk} words with 1-2 sentences of overlap; a chunk never crosses a page, so page citations stay exact."),
            new("Embedding", $"{o.EmbeddingModel} ({o.EmbeddingDimensions} dims)", "Chunks are embedded with taskType RETRIEVAL_DOCUMENT and the question with RETRIEVAL_QUERY; vectors are stored in PostgreSQL + pgvector (HNSW cosine index)."),
            new("Retrieval", "Dense cosine search", $"The {o.CandidatePoolSize} chunks closest in meaning to the question (pgvector cosine distance)."),
            new("Retrieval", "Keyword full-text search", $"The {o.CandidatePoolSize} best chunks by Postgres full-text search (ts_rank_cd over a generated tsvector, matching any question term)."),
            new("Retrieval", $"Reciprocal Rank Fusion (k = {o.RrfK})", $"Both rankings are merged by summing 1 / (k + rank); the top {o.TopK} chunks become the context. No similarity cutoff is applied."),
            new("Generation", $"{o.ChatModel}, temperature {o.Temperature.ToString("0.0#", CultureInfo.InvariantCulture)}", "A strict grounding prompt: answer only from the numbered excerpts, preserve exact numbers, treat excerpt text as data."),
            new("Validation", "Model evidence check", "The model must start with EVIDENCE: YES or NO; its judgment, not a score threshold, decides whether the answer counts as grounded."),
            new("Validation", "Sources from our retrieval", "Citations are the chunks we retrieved, never the model's claim about what it used."),
        ],
        SampleQuestions:
        [
            "What was GPO's total revenue in fiscal 2018?",
            "How many passports did GPO produce for the State Department?",
            "What was the net income from operations?",
            "Which Federal agencies were GPO's largest customers?",
            "What is the largest single component of GPO's annual expenses?",
        ],
        Modes: [],
        DefaultMode: null,
        SupportsEvaluation: false);

    // ---- Status ----------------------------------------------------------------------------------------------

    public async Task<PipelineStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var pdfFound = File.Exists(PdfPath);
        var (chunkCount, error) = await _store.TryCountAsync(cancellationToken);

        var details = new Dictionary<string, string>
        {
            ["table"] = _options.TableName,
            ["chunkKinds"] = "text",
            ["chatModel"] = _options.ChatModel,
            ["retrieval"] = $"dense top {_options.CandidatePoolSize} + keyword top {_options.CandidatePoolSize} -> RRF (k={_options.RrfK}) -> top {_options.TopK}",
        };

        string? message = null;
        if (error is not null) message = $"Vector database unavailable: {error}";
        else if (!pdfFound) message = $"PDF not found: {PdfPath}";

        return new PipelineStatus(
            Info.Id,
            chunkCount > 0 ? IndexState.Ready : IndexState.NotIndexed,
            pdfFound,
            pdfFound ? GetPageCount() : 0,
            chunkCount,
            _geminiOptions.IsApiKeyConfigured,
            _options.EmbeddingModel,
            chunkCount > 0 ? _indexedUtc : null,
            message,
            details);
    }

    private int GetPageCount()
    {
        if (_pageCount > 0 && _pageCountFile == PdfPath) return _pageCount;
        try
        {
            using var document = PdfDocument.Open(PdfPath);
            _pageCount = document.NumberOfPages;
            _pageCountFile = PdfPath;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the page count of {Pdf}", PdfPath);
        }
        return _pageCount;
    }

    // ---- Ingestion -------------------------------------------------------------------------------------------

    public async Task<IngestResult> IngestAsync(bool force, string? mode, IProgress<string> progress, CancellationToken cancellationToken)
    {
        if (!File.Exists(PdfPath)) throw new BadRequestException($"The PDF was not found at {PdfPath}.");

        await _ingestGate.WaitAsync(cancellationToken);
        try
        {
            var stopwatch = Stopwatch.StartNew();

            // PdfPig parsing is CPU-bound, so it runs on a thread-pool thread instead of blocking the caller.
            var pages = await Task.Run(() => _extractor.ExtractPages(PdfPath, progress, cancellationToken), cancellationToken);
            _pageCount = pages.Count;
            _pageCountFile = PdfPath;

            if (pages.All(p => string.IsNullOrWhiteSpace(p.Text)))
                throw new BadRequestException("No extractable text was found in this PDF. It may be a scanned/image-only document, which this pipeline does not OCR.");

            progress.Report("Chunking pages");
            var chunks = _chunker.Chunk(pages);

            // Chunking is deterministic, so a table with the same number of chunks was fully indexed on an earlier run
            // and there is no need to pay for the embedding calls again.
            var existing = await _store.CountAsync(cancellationToken);
            if (!force && existing == chunks.Count)
            {
                _logger.LogInformation("GPO already indexed ({Count} chunks); skipping embedding", existing);
                progress.Report($"Already indexed ({existing} chunks); skipping embedding");
                _indexedUtc ??= DateTimeOffset.UtcNow;
                return new IngestResult(Info.Id, "UpToDate", existing, 0, existing, stopwatch.Elapsed,
                    new Dictionary<string, string> { ["pages"] = pages.Count.ToString(CultureInfo.InvariantCulture) });
            }

            var embeddings = new List<float[]>(chunks.Count);
            for (var i = 0; i < chunks.Count; i += _options.EmbeddingBatchSize)
            {
                var batch = chunks.Skip(i).Take(_options.EmbeddingBatchSize).Select(c => c.Text).ToList();
                progress.Report($"Embedding {Math.Min(i + batch.Count, chunks.Count)}/{chunks.Count} chunks");
                embeddings.AddRange(await _gemini.EmbedAsync(new EmbedRequest(
                    _options.EmbeddingModel, _options.EmbeddingDimensions, batch, "RETRIEVAL_DOCUMENT",
                    _options.EmbeddingBatchSize, _options.EmbeddingRequestsPerMinute), cancellationToken));
            }

            progress.Report("Storing chunks in PostgreSQL");
            await _store.ReplaceAllAsync(chunks, embeddings, cancellationToken);
            _indexedUtc = DateTimeOffset.UtcNow;

            _logger.LogInformation("Indexed GPO: {Pages} pages -> {Chunks} chunks embedded and stored", pages.Count, chunks.Count);
            return new IngestResult(Info.Id, "Ingested", chunks.Count, chunks.Count, 0, stopwatch.Elapsed,
                new Dictionary<string, string> { ["pages"] = pages.Count.ToString(CultureInfo.InvariantCulture) });
        }
        finally
        {
            _ingestGate.Release();
        }
    }

    // ---- Asking ----------------------------------------------------------------------------------------------

    public async Task<AskResult> AskAsync(string question, string? mode, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(question)) throw new BadRequestException("The question must not be empty.");
        question = question.Trim();

        var total = Stopwatch.StartNew();
        var steps = new List<PipelineStep>();

        if (await _store.CountAsync(cancellationToken) == 0)
            throw new IndexNotReadyException("The GPO Annual Report has not been indexed yet. Start ingestion first.");

        // 1. Embed the question (RETRIEVAL_QUERY).
        var sw = Stopwatch.StartNew();
        var queryEmbedding = (await _gemini.EmbedAsync(new EmbedRequest(
            _options.EmbeddingModel, _options.EmbeddingDimensions, [question], "RETRIEVAL_QUERY",
            1, _options.EmbeddingRequestsPerMinute), cancellationToken))[0];
        steps.Add(new("Embed question", $"Embedded the question with {_options.EmbeddingModel} (RETRIEVAL_QUERY, {_options.EmbeddingDimensions} dims).",
            sw.ElapsedMilliseconds, new Dictionary<string, string> { ["model"] = _options.EmbeddingModel }));

        // 2-3. Hybrid retrieval: meaning-based (dense) and exact-term (keyword) candidates.
        sw.Restart();
        var dense = await _store.SearchDenseAsync(queryEmbedding, _options.CandidatePoolSize, cancellationToken);
        steps.Add(new("Dense search", $"pgvector cosine search for the {_options.CandidatePoolSize} nearest chunks.", sw.ElapsedMilliseconds,
            new Dictionary<string, string>
            {
                ["candidates"] = dense.Count.ToString(CultureInfo.InvariantCulture),
                ["topScore"] = dense.Count > 0 ? dense[0].Score!.Value.ToString("0.000", CultureInfo.InvariantCulture) : "-",
            }));

        sw.Restart();
        var keyword = await _store.SearchKeywordsAsync(question, _options.CandidatePoolSize, cancellationToken);
        steps.Add(new("Keyword search", $"Postgres full-text search (ts_rank_cd) for the top {_options.CandidatePoolSize} chunks matching any question term.", sw.ElapsedMilliseconds,
            new Dictionary<string, string> { ["candidates"] = keyword.Count.ToString(CultureInfo.InvariantCulture) }));

        // 4. Reciprocal Rank Fusion.
        sw.Restart();
        var matches = ReciprocalRankFusion.Fuse(_options.TopK, _options.RrfK, dense, keyword);
        steps.Add(new("RRF fusion", $"Merged both rankings with Reciprocal Rank Fusion (k = {_options.RrfK}) and kept the top {_options.TopK}.", sw.ElapsedMilliseconds,
            new Dictionary<string, string>
            {
                ["kept"] = matches.Count.ToString(CultureInfo.InvariantCulture),
                ["inBoth"] = matches.Count(m => m.DenseRank is not null && m.KeywordRank is not null).ToString(CultureInfo.InvariantCulture),
            }));

        var passages = matches.Select((m, i) => new RankedPassage(
            (i + 1).ToString(CultureInfo.InvariantCulture), m.Chunk.Key, m.Score, m.DenseRank, m.KeywordRank, NotesFor(m))).ToList();

        // We deliberately do NOT gate on a raw similarity-score threshold here: testing
        // showed cosine-similarity scores for a clearly unrelated question can be as high
        // as, or higher than, scores for a genuinely relevant one (short text embeddings
        // have a high similarity "baseline" regardless of topic). A numeric cutoff would
        // both reject good matches and let bad ones through. The real check is the model's
        // own "EVIDENCE: YES/NO" judgment below, made with the full question and context.
        if (matches.Count == 0)
        {
            return new AskResult(Info.Id, question, NoEvidenceAnswer, "Insufficient", false, [],
                BuildDebug(steps, passages, null, null, total.ElapsedMilliseconds));
        }

        // 5. Prompt assembly.
        sw.Restart();
        var userPrompt = RagPromptBuilder.BuildUserPrompt(question, matches);
        steps.Add(new("Prompt assembly", "System rules + numbered [Excerpt n - page p] CONTEXT + QUESTION.", sw.ElapsedMilliseconds,
            new Dictionary<string, string> { ["promptChars"] = userPrompt.Length.ToString(CultureInfo.InvariantCulture) }));

        // 6. Generation.
        sw.Restart();
        var chat = await _gemini.GenerateAsync(new ChatRequest(
            _options.ChatModel, RagPromptBuilder.SystemPrompt, userPrompt, _options.Temperature), cancellationToken);
        var parsed = RagResponseParser.Parse(chat.Text);
        var generationDetails = new Dictionary<string, string>
        {
            ["model"] = _options.ChatModel,
            ["evidence"] = parsed.HasEvidence ? "YES" : "NO",
        };
        if (chat.FinishReason is not null) generationDetails["finishReason"] = chat.FinishReason;
        if (chat.Usage.TotalTokens is { } tokens) generationDetails["totalTokens"] = tokens.ToString(CultureInfo.InvariantCulture);
        steps.Add(new("Generation", $"{_options.ChatModel} answered and judged whether the excerpts were sufficient (EVIDENCE: YES/NO).", sw.ElapsedMilliseconds, generationDetails));

        // 7. Sources always come from OUR retrieval, never from what the model claims it
        // used - this is what guarantees citations can't be invented or mis-numbered.
        sw.Restart();
        var citations = parsed.HasEvidence
            ? matches.Select((m, i) => new Citation(
                (i + 1).ToString(CultureInfo.InvariantCulture),
                m.Chunk.Key,
                m.Chunk.PageNumber,
                null,
                null,
                "text",
                Excerpt(m.Chunk.Text),
                m.Score,
                new Dictionary<string, string>
                {
                    ["denseRank"] = m.DenseRank?.ToString(CultureInfo.InvariantCulture) ?? "-",
                    ["keywordRank"] = m.KeywordRank?.ToString(CultureInfo.InvariantCulture) ?? "-",
                })).ToList()
            : [];
        steps.Add(new("Attach sources", parsed.HasEvidence
            ? "Attached the retrieved chunks as sources (from our retrieval, not the model's claim)."
            : "Model reported insufficient evidence, so no sources are attached.", sw.ElapsedMilliseconds,
            new Dictionary<string, string> { ["sources"] = citations.Count.ToString(CultureInfo.InvariantCulture) }));

        var answer = !string.IsNullOrWhiteSpace(parsed.Answer) ? parsed.Answer : NoEvidenceAnswer;
        return new AskResult(Info.Id, question, answer,
            parsed.HasEvidence ? "Sufficient" : "Insufficient", parsed.HasEvidence, citations,
            BuildDebug(steps, passages, RagPromptBuilder.SystemPrompt, userPrompt, total.ElapsedMilliseconds));
    }

    private AskDebug BuildDebug(List<PipelineStep> steps, List<RankedPassage> passages, string? system, string? user, long totalMs) => new(
        "Classic hybrid RAG (baseline)", null, steps, passages, _options.ChatModel, system, user, totalMs,
        new Dictionary<string, string>
        {
            ["topK"] = _options.TopK.ToString(CultureInfo.InvariantCulture),
            ["candidatePoolSize"] = _options.CandidatePoolSize.ToString(CultureInfo.InvariantCulture),
            ["rrfK"] = _options.RrfK.ToString(CultureInfo.InvariantCulture),
        });

    private static IReadOnlyList<string> NotesFor(FusedChunk m)
    {
        var notes = new List<string>();
        if (m.DenseRank is { } d) notes.Add($"Dense rank {d}");
        if (m.KeywordRank is { } k) notes.Add($"Keyword rank {k}");
        notes.Add(m.DenseRank is not null && m.KeywordRank is not null
            ? "Found by both retrievers, so both rankings add to the fused score"
            : "Found by one retriever only");
        return notes;
    }

    private static string Excerpt(string text) =>
        text.Length <= ExcerptLength ? text : text[..ExcerptLength].TrimEnd() + "...";

    // ---- Chunks ----------------------------------------------------------------------------------------------

    public async Task<ChunkPage> GetChunksAsync(ChunkQuery query, CancellationToken cancellationToken)
    {
        // Kind is ignored: every chunk of this pipeline is plain text.
        var (total, items) = await _store.ListAsync(query.Page, query.Contains, query.Skip, query.Take, cancellationToken);
        var views = items.Select(c => new ChunkView(
            c.Key, c.ChunkIndex, "text", c.PageNumber, null, null, c.Text,
            new Dictionary<string, string>
            {
                ["words"] = (c.Text.Count(ch => ch == ' ') + 1).ToString(CultureInfo.InvariantCulture),
            })).ToList();
        return new ChunkPage(total, views);
    }
}
