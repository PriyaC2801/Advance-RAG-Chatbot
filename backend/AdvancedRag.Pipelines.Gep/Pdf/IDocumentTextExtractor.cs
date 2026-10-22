namespace AdvancedRag.Pipelines.Gep.Pdf;

/// <summary>
/// Turns a PDF file into pages of positioned text lines.
/// Implementations know about PDF geometry only — not about this particular report's styles.
/// </summary>
public interface IDocumentTextExtractor
{
    Task<ExtractedDocument> ExtractAsync(string pdfPath, CancellationToken cancellationToken);
}
