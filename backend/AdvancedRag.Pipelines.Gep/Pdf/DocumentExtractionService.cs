using AdvancedRag.Core.Storage;

namespace AdvancedRag.Pipelines.Gep.Pdf;

/// <summary>Classified pages plus the heading list derived from them.</summary>
public sealed record ExtractionResult(ExtractedDocument Document, IReadOnlyList<HeadingCandidate> Headings);

/// <summary>
/// Runs extraction + classification once and keeps the result in memory (registered as a
/// singleton). Extracting 200 pages takes a few seconds, so we do not repeat it on every request.
/// Chunks are persisted by ingestion; this cache only avoids re-reading the PDF within one process.
/// </summary>
public sealed class DocumentExtractionService(
    IDocumentTextExtractor extractor,
    GepLayoutClassifier classifier,
    IOptions<GepOptions> options,
    StoragePaths paths)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ExtractionResult? _cached;

    public string PdfPath => paths.Document(options.Value.FileName);

    public async Task<ExtractionResult> GetAsync(CancellationToken cancellationToken)
    {
        if (_cached is not null) return _cached;

        // Only one request runs the extraction; others wait and then reuse the result.
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_cached is not null) return _cached;

            var raw = await extractor.ExtractAsync(PdfPath, cancellationToken);
            var pages = raw.Pages.Select(classifier.Classify).ToList();
            var document = raw with { Pages = pages };
            _cached = new ExtractionResult(document, GepLayoutClassifier.ExtractHeadings(pages));
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }
}
