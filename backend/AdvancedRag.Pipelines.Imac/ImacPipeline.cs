using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AdvancedRag.Core.Ai;
using AdvancedRag.Core.Rag;
using AdvancedRag.Core.Storage;
using AdvancedRag.Pipelines.Imac.Extraction;
using UglyToad.PdfPig;

namespace AdvancedRag.Pipelines.Imac;

/// <summary>
/// Element-aware / multimodal RAG over Apple's iMac Product Environmental Report: tables, call-outs, footnotes and chart
/// pages become separate chunk kinds, retrieval is dense + keyword fused with RRF, and every answer cites numbered passages.
/// </summary>
public sealed partial class ImacPipeline(
    IGeminiClient gemini,
    ImacStore store,
    ImacVisionDescriber vision,
    ImacOptions options,
    StoragePaths paths,
    ILogger<ImacPipeline> logger) : IEvaluablePipeline
{
    private const string PipelineName = "Element-aware multimodal RAG";

    // Ingestion deletes + rebuilds the collection and an eval costs a chat call per question: never two of these at once.
    private readonly object _gate = new();
    private string? _busyWith;

    private int? _pageCount;

    public DocumentInfo Info { get; } = new(
        Id: "imac",
        Title: "iMac Environmental Report",
        Subtitle: "Apple · October 2024 · 12 pages",
        Description: "Apple's 12-page iMac Product Environmental Report, where the facts live in tables, charts, call-outs and footnotes. " +
                     "Each kind of element is extracted and chunked on its own terms, chart pages are read by a vision model, and every answer cites numbered passages or refuses.",
        FileName: "iMac_PER_Oct2024.pdf",
        Techniques:
        [
            new("Extraction", "Table reconstruction from geometry", "Dark header bars give the columns, text baselines give the rows; each table becomes one chunk instead of broken fragments."),
            new("Extraction", "Footnote marker detection", "Superscript numbers (small, raised, Regular font) are found and rewritten as [^n] so the claim and its footnote stay linked."),
            new("Extraction", "Call-out and paragraph blocks", "Docstrum page segmentation keeps side-by-side call-outs and columns apart; the footer is dropped and big stat numbers are glued to their labels."),
            new("Enrichment", "Footnote linking", "Endnote texts are parsed and appended as \"[Note n] ...\" to every chunk that cites them, so a caveat travels with its claim."),
            new("Enrichment", "Vision descriptions of chart pages", "Pages with charts or artwork are rendered to images and described in plain text by gemini-3.1-flash-lite; descriptions are cached by page, image hash and model."),
            new("Chunking", "Element-aware chunking", "Table = one chunk, text = whole sentences up to ~220 tokens (never cut mid-sentence), vision = one chunk per page description."),
            new("Embedding", "gemini-embedding-001 (1536 dimensions)", "Chunk text with a small \"Page · Section · kind\" header is embedded; the header helps search without polluting what the model reads."),
            new("Retrieval", "Dense vector search (pgvector, cosine)", "The 20 nearest chunks by meaning; good at paraphrase (\"manufacturing\" ~ \"Production\")."),
            new("Retrieval", "Keyword search (Postgres tsvector)", "The 20 best full-text matches with any-word matching; good at exact tokens like \"20.9W\" or \"230V\"."),
            new("Retrieval", "Reciprocal Rank Fusion (k = 60)", "score = 1/(60+rank) summed over both lists, fused in one SQL query; the top 5 passages go to the model."),
            new("Generation", "Grounded, cited answers", "The chat model sees only numbered passages [1]..[5], must cite them, mention [Note n] caveats, or reply \"I could not find this in the document.\" (temperature 0)."),
            new("Evaluation", "13 hand-checked questions", "hit@1/3/5, MRR (retrieval) and answer pass rate per retrieval mode, so each technique's contribution can be measured."),
        ],
        SampleQuestions:
        [
            "How much less energy does iMac use compared to the ENERGY STAR requirement?",
            "What is the total carbon footprint of the iMac, and how much comes from product use versus manufacturing?",
            "What material is the iMac stand made from, and what percentage of it is recycled?",
            "What is the iMac's power consumption in Idle—Display On mode at 230V?",
            "What is the iMac's screen resolution?",
        ],
        Modes:
        [
            new(RetrievalModes.ElementsDense, "Elements + dense", RetrievalModes.Describe(RetrievalModes.ElementsDense)),
            new(RetrievalModes.ElementsHybrid, "Elements + hybrid (RRF)", RetrievalModes.Describe(RetrievalModes.ElementsHybrid)),
            new(RetrievalModes.Advanced, "Advanced (+ vision)", RetrievalModes.Describe(RetrievalModes.Advanced)),
        ],
        DefaultMode: RetrievalModes.Advanced,
        SupportsEvaluation: true);

    private string PdfPath => paths.Document(options.DocumentFileName);

    // ---- Status ------------------------------------------------------------------------------------------

    public async Task<PipelineStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var pdfFound = File.Exists(PdfPath);
        var details = new Dictionary<string, string>();
        var chunkCount = 0;
        string? message = null;

        try
        {
            var byKind = await store.CountByKindAsync(RetrievalModes.Collection, cancellationToken);
            chunkCount = byKind.Values.Sum();
            foreach (var kv in byKind) details[$"chunks.{kv.Key}"] = kv.Value.ToString(CultureInfo.InvariantCulture);
            details["indexedModes"] = chunkCount > 0 ? string.Join(", ", RetrievalModes.All) : "none";
            details["note"] = "All three modes search the same stored chunks; they differ in which kinds and rankings they use.";
        }
        catch (Exception ex) when (ex is VectorStoreUnavailableException)
        {
            message = ex.Message;
            details["indexedModes"] = "unknown (database unavailable)";
        }

        if (!pdfFound) message ??= $"PDF not found: {options.DocumentFileName}";
        details["chatModel"] = options.ChatModel;
        details["visionModel"] = options.VisionModel;

        return new PipelineStatus(
            Info.Id,
            chunkCount > 0 ? IndexState.Ready : IndexState.NotIndexed,
            pdfFound,
            pdfFound ? PageCount() : 0,
            chunkCount,
            gemini.IsConfigured,
            options.EmbeddingModel,
            IndexedUtc: null,
            message,
            details);
    }

    private int PageCount()
    {
        if (_pageCount is { } cached) return cached;
        try
        {
            using var document = PdfDocument.Open(PdfPath);
            return (_pageCount = document.NumberOfPages).Value;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read the page count of {Pdf}", PdfPath);
            return 0;
        }
    }

    // ---- Ingestion ---------------------------------------------------------------------------------------

    public async Task<IngestResult> IngestAsync(bool force, string? mode, IProgress<string> progress, CancellationToken cancellationToken)
    {
        // The three modes read the same collection, so every valid mode ingests the same thing; an unknown mode is still a mistake.
        if (mode != null && !RetrievalModes.IsValid(mode)) throw UnknownMode(mode);
        mode ??= Info.DefaultMode!;
        if (!File.Exists(PdfPath)) throw new BadRequestException($"The PDF '{options.DocumentFileName}' was not found in the documents folder.");

        Enter("an ingestion");
        try
        {
            var clock = Stopwatch.StartNew();

            if (!force)
            {
                var existing = await store.CountByKindAsync(RetrievalModes.Collection, cancellationToken);
                if (existing.Values.Sum() > 0)
                {
                    return new IngestResult(Info.Id, "UpToDate", existing.Values.Sum(), 0, existing.Values.Sum(), clock.Elapsed, KindDetails(existing, mode));
                }
            }

            progress.Report("Extracting tables, text and footnotes");
            var chunks = await BuildChunksAsync(progress, cancellationToken);

            logger.LogInformation("Embedding {Count} chunks for '{Mode}'...", chunks.Count, mode);
            var vectors = await EmbedChunksAsync(chunks, progress, cancellationToken);

            progress.Report("Storing chunks");
            await store.ReplaceCollectionAsync(RetrievalModes.Collection, chunks, vectors, cancellationToken);

            var byKind = chunks.GroupBy(c => c.Kind).ToDictionary(g => g.Key, g => g.Count());
            logger.LogInformation("Stored {Count} chunks in '{Mode}': {ByKind}; {WithNotes} with footnotes attached",
                chunks.Count, mode, string.Join(", ", byKind.Select(kv => $"{kv.Key}={kv.Value}")), chunks.Count(c => c.Footnotes.Length > 0));

            var details = KindDetails(byKind, mode);
            details["withFootnotes"] = chunks.Count(c => c.Footnotes.Length > 0).ToString(CultureInfo.InvariantCulture);
            return new IngestResult(Info.Id, "Ingested", chunks.Count, chunks.Count, 0, clock.Elapsed, details);
        }
        finally
        {
            Leave();
        }
    }

    private static Dictionary<string, string> KindDetails(IReadOnlyDictionary<string, int> byKind, string mode)
    {
        var details = byKind.ToDictionary(kv => $"chunks.{kv.Key}", kv => kv.Value.ToString(CultureInfo.InvariantCulture));
        details["mode"] = mode;
        details["indexedModes"] = string.Join(", ", RetrievalModes.All);
        return details;
    }

    // Element-aware chunking: the shape of each chunk follows the shape of the content.
    //   table  -> one chunk (a table cut in half is useless)
    //   text   -> sentence groups of up to ~220 tokens, never cut mid-sentence
    //   vision -> one chunk per page description
    // Every chunk with a footnote marker gets the note text appended, so the caveat travels with the claim.
    private async Task<List<ChunkDraft>> BuildChunksAsync(IProgress<string> progress, CancellationToken cancellationToken)
    {
        var built = ElementBuilder.Build(PdfPath);
        var chunks = new List<ChunkDraft>();

        foreach (var element in built.Elements)
        {
            if (element.Kind == "table")
            {
                chunks.Add(MakeChunk("table", element.Page, element.Section, element.Raw, built.Notes));
                continue;
            }

            foreach (var group in ImacChunker.GroupSentences(element.Raw, options.MaxChunkTokens))
            {
                chunks.Add(MakeChunk("text", element.Page, element.Section, group, built.Notes));
            }
        }

        // Vision descriptions: cached, so this only calls the model for pages not described yet.
        foreach (var v in await vision.DescribeAsync(PdfPath, progress, cancellationToken))
        {
            // The model wrote hints like "[note 7]"; we record them but do not append whole notes (descriptions are long already).
            var notes = Regex.Matches(v.Description, @"\[notes? (\d+)\]", RegexOptions.IgnoreCase)
                .Select(m => int.Parse(m.Groups[1].Value)).Distinct().OrderBy(n => n).ToArray();
            chunks.Add(new ChunkDraft("vision", v.Page, v.Section, notes, v.Description, Header("vision", v.Page, v.Section) + v.Description));
        }

        return chunks;
    }

    private static ChunkDraft MakeChunk(string kind, int page, string? section, string raw, IReadOnlyDictionary<int, string> notes)
    {
        var (content, footnotes) = FootnoteLinker.Link(raw, notes);
        return new ChunkDraft(kind, page, section, footnotes, content, Header(kind, page, section) + content);
    }

    // embed_text = small header + content: the header lets "Use"/"page 7"/"table" words help both searches,
    // while `content` (what the LLM reads) stays free of that scaffolding.
    private static string Header(string kind, int page, string? section) => $"Page {page} · {section} · {kind}\n";

    private async Task<List<float[]>> EmbedChunksAsync(IReadOnlyList<ChunkDraft> chunks, IProgress<string> progress, CancellationToken cancellationToken)
    {
        var vectors = new List<float[]>(chunks.Count);
        for (var i = 0; i < chunks.Count; i += options.EmbeddingBatchSize)
        {
            var batch = chunks.Skip(i).Take(options.EmbeddingBatchSize).Select(c => c.EmbedText).ToList();
            vectors.AddRange(await EmbedAsync(batch, "RETRIEVAL_DOCUMENT", cancellationToken));
            progress.Report($"Embedding {vectors.Count}/{chunks.Count} chunks");
        }
        return vectors;
    }

    // Retries, rate limiting and the batch/quota budget are handled by IGeminiClient; the dimensions are requested
    // explicitly so the vector fits the vector(1536) column whatever the model's native size.
    private async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, string taskType, CancellationToken cancellationToken) =>
        await gemini.EmbedAsync(new EmbedRequest(
            options.EmbeddingModel, options.EmbeddingDimensions, texts,
            TaskType: options.UseEmbeddingTaskTypes ? taskType : null,
            BatchSize: options.EmbeddingBatchSize, RequestsPerMinute: options.EmbeddingRequestsPerMinute), cancellationToken);

    // ---- Asking ------------------------------------------------------------------------------------------

    public async Task<AskResult> AskAsync(string question, string? mode, CancellationToken cancellationToken) =>
        (await AskCoreAsync(question, mode, cancellationToken)).Result;

    private sealed record AskOutcome(AskResult Result, IReadOnlyList<ImacChunk> Passages);

    private async Task<AskOutcome> AskCoreAsync(string question, string? mode, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(question)) throw new BadRequestException("The question is empty.");
        mode ??= Info.DefaultMode!;
        if (!RetrievalModes.IsValid(mode)) throw UnknownMode(mode);
        question = question.Trim();

        var byKind = await store.CountByKindAsync(RetrievalModes.Collection, cancellationToken);
        var kinds = RetrievalModes.KindsFor(mode);
        if (kinds.Sum(k => byKind.GetValueOrDefault(k)) == 0)
        {
            throw new IndexNotReadyException($"Mode '{mode}' is not indexed yet; ingest it first.");
        }

        var total = Stopwatch.StartNew();
        var steps = new List<PipelineStep>();

        // 1. RETRIEVE the passages for this question.
        var clock = Stopwatch.StartNew();
        var vector = (await EmbedAsync([question], "RETRIEVAL_QUERY", cancellationToken))[0];
        var embedMs = clock.ElapsedMilliseconds;
        steps.Add(new PipelineStep("Embed the question",
            $"The question becomes a {options.EmbeddingDimensions}-dimension vector with {options.EmbeddingModel}.", embedMs,
            new Dictionary<string, string> { ["model"] = options.EmbeddingModel, ["dimensions"] = options.EmbeddingDimensions.ToString(CultureInfo.InvariantCulture) }));

        clock.Restart();
        var hybrid = RetrievalModes.IsHybrid(mode);
        var passages = hybrid
            ? await store.HybridSearchAsync(RetrievalModes.Collection, vector, question, kinds, options.CandidatesPerList, options.RrfK, options.TopK, cancellationToken)
            : await store.DenseSearchAsync(RetrievalModes.Collection, vector, kinds, options.TopK, cancellationToken);
        var searchMs = clock.ElapsedMilliseconds;
        var retrievalMs = embedMs + searchMs;

        var kindList = string.Join(", ", kinds);
        steps.Add(new PipelineStep("Dense ranking",
            $"The {options.CandidatesPerList} chunks closest in meaning (cosine distance) among {kindList} chunks.",
            hybrid ? null : searchMs,
            new Dictionary<string, string> { ["kinds"] = kindList, ["candidates"] = options.CandidatesPerList.ToString(CultureInfo.InvariantCulture), ["inTopPassages"] = passages.Count(p => p.DenseRank != null).ToString(CultureInfo.InvariantCulture) }));
        if (hybrid)
        {
            steps.Add(new PipelineStep("Keyword ranking",
                "Postgres full text: chunks matching any question word, best ts_rank_cd first (exact tokens like \"20.9W\").", null,
                new Dictionary<string, string> { ["candidates"] = options.CandidatesPerList.ToString(CultureInfo.InvariantCulture), ["inTopPassages"] = passages.Count(p => p.KeywordRank != null).ToString(CultureInfo.InvariantCulture) }));
            steps.Add(new PipelineStep("Reciprocal Rank Fusion",
                $"score = 1/({options.RrfK}+rank) summed over both lists; the top {options.TopK} become the passages. Dense, keyword and fusion run as one SQL query.", searchMs,
                new Dictionary<string, string> { ["rrfK"] = options.RrfK.ToString(CultureInfo.InvariantCulture), ["topK"] = options.TopK.ToString(CultureInfo.InvariantCulture) }));
        }

        // 2. AUGMENT the prompt with the numbered passages.
        var userMessage = ImacPrompts.BuildUserMessage(question, passages);
        steps.Add(new PipelineStep("Assemble passages",
            "The top passages are numbered [1]..[n]; footnotes attached at ingestion appear as [Note n] inside them.", 0,
            new Dictionary<string, string>
            {
                ["passages"] = passages.Count.ToString(CultureInfo.InvariantCulture),
                ["withFootnotes"] = passages.Count(p => p.Footnotes.Length > 0).ToString(CultureInfo.InvariantCulture),
                ["kinds"] = string.Join(", ", passages.Select(p => p.Kind)),
            }));

        // 3. GENERATE a grounded answer (temperature 0).
        clock.Restart();
        var chat = await gemini.GenerateAsync(new ChatRequest(options.ChatModel, ImacPrompts.SystemPrompt, userMessage, Temperature: 0), cancellationToken);
        var generationMs = clock.ElapsedMilliseconds;
        var answer = chat.Text.Trim();
        steps.Add(new PipelineStep("Generate answer",
            $"{options.ChatModel} reads only the passages and answers with [n] citations, or refuses.", generationMs,
            new Dictionary<string, string> { ["model"] = options.ChatModel, ["temperature"] = "0" }));

        // 4. Sources come from OUR retrieval, never from the model's claim about what it used.
        var refused = answer.Contains(ImacPrompts.Refusal, StringComparison.OrdinalIgnoreCase);
        var citations = refused
            ? []
            : passages.Select((c, i) => new Citation(
                Label: (i + 1).ToString(CultureInfo.InvariantCulture),
                ChunkId: c.Id.ToString(CultureInfo.InvariantCulture),
                Page: c.Page,
                PageLabel: null,
                Section: c.Section,
                Kind: c.Kind,
                Excerpt: c.Content,
                Score: c.Score,
                Details: RankDetails(c))).ToList();

        var ranked = passages.Select((c, i) => new RankedPassage(
            (i + 1).ToString(CultureInfo.InvariantCulture), c.Id.ToString(CultureInfo.InvariantCulture), c.Score, c.DenseRank, c.KeywordRank, Notes(c, hybrid))).ToList();

        var extra = new Dictionary<string, string>
        {
            ["retrievalMs"] = retrievalMs.ToString(CultureInfo.InvariantCulture),
            ["generationMs"] = generationMs.ToString(CultureInfo.InvariantCulture),
            ["modeDescription"] = RetrievalModes.Describe(mode),
            ["candidatesPerList"] = options.CandidatesPerList.ToString(CultureInfo.InvariantCulture),
            ["topK"] = options.TopK.ToString(CultureInfo.InvariantCulture),
            ["rrfK"] = options.RrfK.ToString(CultureInfo.InvariantCulture),
            ["embeddingModel"] = options.EmbeddingModel,
        };
        foreach (var kv in byKind) extra[$"chunks.{kv.Key}"] = kv.Value.ToString(CultureInfo.InvariantCulture);

        var debug = new AskDebug(PipelineName, mode, steps, ranked, options.ChatModel, ImacPrompts.SystemPrompt, userMessage, total.ElapsedMilliseconds, extra);
        var result = new AskResult(Info.Id, question, answer, refused ? "Insufficient" : "Sufficient", !refused, citations, debug);
        return new AskOutcome(result, passages);
    }

    private static Dictionary<string, string> RankDetails(ImacChunk c)
    {
        var details = new Dictionary<string, string>
        {
            ["denseRank"] = c.DenseRank?.ToString(CultureInfo.InvariantCulture) ?? "-",
            ["keywordRank"] = c.KeywordRank?.ToString(CultureInfo.InvariantCulture) ?? "-",
        };
        if (c.Footnotes.Length > 0) details["footnotes"] = string.Join(", ", c.Footnotes);
        return details;
    }

    private static List<string> Notes(ImacChunk c, bool hybrid)
    {
        var notes = new List<string>();
        if (!hybrid) notes.Add($"Score is the cosine similarity ({c.Score:0.000}).");
        else if (c.DenseRank != null && c.KeywordRank != null) notes.Add("Found by both dense and keyword search; the two RRF contributions add up.");
        else if (c.DenseRank != null) notes.Add("Found by dense (meaning) search only.");
        else if (c.KeywordRank != null) notes.Add("Found by keyword search only.");
        if (c.Footnotes.Length > 0) notes.Add($"Footnotes attached: {string.Join(", ", c.Footnotes)}.");
        if (c.Kind == "vision") notes.Add("Description of a chart/icon page written by the vision model.");
        return notes;
    }

    // ---- Chunks (developer view) -------------------------------------------------------------------------

    public async Task<ChunkPage> GetChunksAsync(ChunkQuery query, CancellationToken cancellationToken)
    {
        var kind = string.IsNullOrWhiteSpace(query.Kind) ? null : query.Kind.Trim().ToLowerInvariant();
        var contains = string.IsNullOrWhiteSpace(query.Contains) ? null : query.Contains.Trim();
        var skip = Math.Max(0, query.Skip);
        var take = Math.Clamp(query.Take, 1, 500);

        var (total, items) = await store.ListAsync(RetrievalModes.Collection, kind, query.Page, contains, skip, take, cancellationToken);
        var views = items.Select((c, i) => new ChunkView(
            c.Id.ToString(CultureInfo.InvariantCulture), skip + i, c.Kind, c.Page, c.Page, c.Section, c.Content,
            new Dictionary<string, string>
            {
                ["section"] = c.Section ?? "",
                ["footnotes"] = string.Join(", ", c.Footnotes),
            })).ToList();
        return new ChunkPage(total, views);
    }

    // ---- Shared helpers ----------------------------------------------------------------------------------

    private static BadRequestException UnknownMode(string mode) =>
        new($"Unknown mode '{mode}'. Use: {string.Join(", ", RetrievalModes.All)}.");

    private void Enter(string what)
    {
        lock (_gate)
        {
            if (_busyWith != null) throw new ConflictException($"Cannot start {what}: {_busyWith} is in progress.");
            _busyWith = what;
        }
    }

    private void Leave()
    {
        lock (_gate) _busyWith = null;
    }
}
