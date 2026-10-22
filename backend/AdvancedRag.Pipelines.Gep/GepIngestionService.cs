using System.Diagnostics;
using AdvancedRag.Core.Rag;
using AdvancedRag.Core.Storage;
using AdvancedRag.Pipelines.Gep.Chunking;
using AdvancedRag.Pipelines.Gep.Embeddings;
using AdvancedRag.Pipelines.Gep.Models;
using AdvancedRag.Pipelines.Gep.Retrieval;

namespace AdvancedRag.Pipelines.Gep;

/// <summary>
/// The ingestion flow: PDF → chunks → chart data → embeddings → persisted files → PostgreSQL table + in-memory metadata.
/// Only chunks whose embedding input changed are sent to Gemini, so re-running costs nothing when the report and
/// settings are unchanged.
/// </summary>
public sealed class GepIngestionService(
    ChunkingService chunking,
    GepEmbedder embedder,
    EmbeddingStore embeddingStore,
    GepVectorStore vectorStore,
    IOptions<GepOptions> options,
    StoragePaths paths,
    ILogger<GepIngestionService> logger)
{
    /// <summary>Serializes ingestion and index loading (one at a time).</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string DataDirectory => paths.Data(options.Value.DocumentFolder);

    private volatile bool _ingesting;

    /// <summary>True while an ingestion is running (an index load then reports "not loaded" instead of waiting).</summary>
    public bool IsIngesting => _ingesting;

    /// <summary>What we embed for a chunk: its location in the report as a title, plus its text.</summary>
    public EmbeddingInput ToEmbeddingInput(DocumentChunk chunk) => new(
        $"{options.Value.Title}, {options.Value.Edition} — {chunk.HeadingPath}",
        chunk.Text);

    public async Task<IngestResult> IngestAsync(bool force, IProgress<string> progress, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _ingesting = true;
            var pdfPath = paths.Document(options.Value.FileName);
            if (!File.Exists(pdfPath))
            {
                throw new FileNotFoundException($"The PDF was not found at {pdfPath}.");
            }

            var stopwatch = Stopwatch.StartNew();
            var build = await chunking.BuildAsync(force, progress, cancellationToken);
            var chunks = build.Index.Chunks;

            // Reuse stored vectors whose input text is unchanged and that came from the same model settings.
            var existing = await embeddingStore.LoadAsync(DataDirectory, cancellationToken);
            var reusable = new Dictionary<(string ChunkId, string TextHash), float[]>();
            if (!force && existing is not null && existing.ModelId == embedder.ModelId)
            {
                foreach (var e in existing.Items) reusable[(e.ChunkId, e.TextHash)] = e.Vector;
            }

            var inputs = chunks.Select(c => (Chunk: c, Input: ToEmbeddingInput(c))).ToList();
            var stored = new List<StoredEmbedding>(chunks.Count);
            var missing = new List<(DocumentChunk Chunk, EmbeddingInput Input, string Hash)>();
            foreach (var (chunk, input) in inputs)
            {
                var hash = EmbeddingStore.HashInput(input);
                if (reusable.TryGetValue((chunk.Id, hash), out var vector)) stored.Add(new(chunk.Id, hash, vector));
                else missing.Add((chunk, input, hash));
            }

            if (missing.Count > 0)
            {
                logger.LogInformation("Embedding {Missing} of {Total} chunks with {Model}", missing.Count, chunks.Count, embedder.ModelId);
                progress.Report($"Embedding 0/{missing.Count} chunks");
                var vectors = await embedder.EmbedDocumentsAsync(missing.Select(m => m.Input).ToList(), progress, cancellationToken);
                stored.AddRange(missing.Zip(vectors, (m, v) => new StoredEmbedding(m.Chunk.Id, m.Hash, v)));
            }

            var upToDate = !build.Rebuilt && missing.Count == 0 && existing is not null && existing.ModelId == embedder.ModelId;
            if (!upToDate)
            {
                // Keep only vectors of current chunks, in chunk order.
                var byId = stored.ToDictionary(s => s.ChunkId);
                var ordered = chunks.Select(c => byId[c.Id]).ToList();
                await embeddingStore.SaveAsync(DataDirectory,
                    new EmbeddingIndexFile(embedder.ModelId, embedder.Dimensions, DateTimeOffset.UtcNow, ordered),
                    cancellationToken);
            }

            // When nothing changed and the table already matches, there is nothing to copy.
            if (!(upToDate && (vectorStore.IsLoaded || await TryAdoptTableAsync(chunks, cancellationToken))))
            {
                progress.Report($"Writing {chunks.Count} chunks to PostgreSQL…");
                await LoadIntoVectorStoreAsync(chunks, stored, cancellationToken);
            }

            var details = new Dictionary<string, string>
            {
                ["embeddingModel"] = embedder.ModelId,
                ["chunksRebuilt"] = build.Rebuilt.ToString(),
                ["table"] = options.Value.TableName,
            };
            return new IngestResult(GepPipeline.Id, upToDate ? "UpToDate" : "Ingested", chunks.Count,
                missing.Count, chunks.Count - missing.Count, stopwatch.Elapsed, details);
        }
        finally
        {
            _ingesting = false;
            _gate.Release();
        }
    }

    /// <summary>
    /// Loads a previous ingestion into memory (before the first question or status request), so ingestion does not
    /// have to run every time the API starts. Returns false if nothing was ingested yet or the stored data does not
    /// match the current chunks/model. Throws <see cref="VectorStoreUnavailableException"/> if the database is down.
    /// </summary>
    public async Task<bool> TryLoadAsync(CancellationToken cancellationToken)
    {
        if (vectorStore.IsLoaded) return true;
        if (_ingesting) return false;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (vectorStore.IsLoaded) return true;

            var chunkIndex = await chunking.GetIndexAsync(cancellationToken);
            if (chunkIndex is null) return false;

            // The table may already hold exactly these chunks (the normal case after a restart).
            if (await TryAdoptTableAsync(chunkIndex.Chunks, cancellationToken)) return true;

            // Otherwise copy the files into the table, if they are consistent.
            var embeddings = await embeddingStore.LoadAsync(DataDirectory, cancellationToken);
            if (embeddings is null) return false;

            if (embeddings.ModelId != embedder.ModelId)
            {
                logger.LogWarning("embeddings.json was made with {Stored}, but {Current} is configured; run ingestion again",
                    embeddings.ModelId, embedder.ModelId);
                return false;
            }

            var stored = embeddings.Items.ToDictionary(e => e.ChunkId);
            var complete = chunkIndex.Chunks.All(c =>
                stored.TryGetValue(c.Id, out var e) && e.TextHash == EmbeddingStore.HashInput(ToEmbeddingInput(c)));
            if (!complete)
            {
                logger.LogWarning("embeddings.json does not match chunks.json; run ingestion again");
                return false;
            }

            await LoadIntoVectorStoreAsync(chunkIndex.Chunks, embeddings.Items, cancellationToken);
            logger.LogInformation("Loaded {Count} chunks with embeddings into PostgreSQL", chunkIndex.Chunks.Count);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private Task<bool> TryAdoptTableAsync(IReadOnlyList<DocumentChunk> chunks, CancellationToken cancellationToken) =>
        vectorStore.TryAdoptExistingAsync(chunks,
            chunks.ToDictionary(c => c.Id, c => EmbeddingStore.HashInput(ToEmbeddingInput(c))),
            embedder.ModelId, cancellationToken);

    private Task LoadIntoVectorStoreAsync(IReadOnlyList<DocumentChunk> chunks, IReadOnlyList<StoredEmbedding> vectors,
        CancellationToken cancellationToken)
    {
        var byId = vectors.ToDictionary(v => v.ChunkId);
        return vectorStore.ReplaceAsync(
            chunks.Select(c => new IndexedChunk(c, byId[c.Id].Vector, byId[c.Id].TextHash)).ToList(),
            embedder.ModelId, cancellationToken);
    }
}
