using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AdvancedRag.Core.Storage;
using AdvancedRag.Pipelines.Gep.Pdf;

namespace AdvancedRag.Pipelines.Gep.Chunking;

public sealed record ChunkBuildResult(bool Rebuilt, ChunkIndexFile Index, TimeSpan Duration);

/// <summary>
/// Builds chunks.json from the PDF (extract → classify → chunk → read figures → save) and serves the chunks.
/// Skips the work when the PDF hash and settings fingerprint match the existing file.
/// </summary>
public sealed class ChunkingService(
    DocumentExtractionService extraction,
    IDocumentChunker chunker,
    IFigureDataService figureData,
    ChunkStore store,
    IOptions<GepOptions> options,
    StoragePaths paths,
    ILogger<ChunkingService> logger)
{
    /// <summary>Bump when the chunking code changes in a way that should invalidate old chunks.json files.</summary>
    private const string ChunkerVersion = "5"; // 5: chart data appended to figure chunks; 4: wrapped table row labels merged

    private readonly SemaphoreSlim _gate = new(1, 1);
    private ChunkIndexFile? _cached;

    private string DataDirectory => paths.Data(options.Value.DocumentFolder);

    public async Task<ChunkBuildResult> BuildAsync(bool force, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var stopwatch = Stopwatch.StartNew();
            progress?.Report("Checking the PDF…");
            var pdfHash = await HashFileAsync(extraction.PdfPath, cancellationToken);
            var fingerprint = SettingsFingerprint();

            var existing = _cached ?? await store.LoadAsync(DataDirectory, cancellationToken);
            if (!force && existing is not null && existing.SourceSha256 == pdfHash && existing.SettingsFingerprint == fingerprint)
            {
                _cached = existing;
                logger.LogInformation("chunks.json is up to date ({ChunkCount} chunks); skipping rebuild", existing.Chunks.Count);
                return new ChunkBuildResult(false, existing, stopwatch.Elapsed);
            }

            progress?.Report("Reading the PDF layout (200 pages)…");
            var extracted = await extraction.GetAsync(cancellationToken);
            progress?.Report("Chunking by chapter, section, table and figure…");
            var chunks = chunker.Chunk(extracted.Document.Pages, options.Value.DocumentId);
            chunks = await figureData.EnrichAsync(chunks, extraction.PdfPath, pdfHash, DataDirectory, progress, cancellationToken);
            var index = new ChunkIndexFile(
                options.Value.DocumentId,
                extracted.Document.FileName,
                pdfHash,
                fingerprint,
                DateTimeOffset.UtcNow,
                extracted.Document.PageCount,
                chunks);

            await store.SaveAsync(DataDirectory, index, cancellationToken);
            _cached = index;
            logger.LogInformation("Built {ChunkCount} chunks in {ElapsedMs} ms", chunks.Count, stopwatch.ElapsedMilliseconds);
            return new ChunkBuildResult(true, index, stopwatch.Elapsed);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The current chunk index from memory or disk; null if chunking has never run.</summary>
    public async Task<ChunkIndexFile?> GetIndexAsync(CancellationToken cancellationToken)
    {
        if (_cached is not null) return _cached;
        _cached = await store.LoadAsync(DataDirectory, cancellationToken);
        return _cached;
    }

    private string SettingsFingerprint()
    {
        var json = JsonSerializer.Serialize(options.Value.Chunking) + ChunkerVersion + (options.Value.FigureData.Enabled ? "+figures" : "");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)))[..16];
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
    }
}
