using System.Security.Cryptography;
using System.Text.Json;
using AdvancedRag.Core.Ai;
using AdvancedRag.Core.Storage;
using AdvancedRag.Pipelines.Imac.Extraction;
using PDFtoImage;
using SkiaSharp;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace AdvancedRag.Pipelines.Imac;

// Charts, icons and call-outs are pictures: text extraction sees only scattered labels. So we render the page,
// let a vision model read it, and index its plain-text description as one more chunk (kind = 'vision').
public sealed class ImacVisionDescriber(IGeminiClient gemini, ImacOptions options, StoragePaths paths, ILogger<ImacVisionDescriber> logger)
{
    private const int Dpi = 130;             // readable chart labels without huge uploads
    private const int MinArtworkShapes = 5;  // filled shapes, not counting the white page background

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>Cached vision descriptions, one JSON per page. Re-ingesting never calls the model again for an unchanged page.</summary>
    public string CacheDir => Path.Combine(paths.Data("imac"), "vision-cache");

    // A page is "visual" if it has images or enough filled artwork. Plain-text pages (9, 11) have only the
    // white background and thin rules, so they are skipped.
    public static bool IsVisualPage(Page page)
    {
        if (page.GetImages().Any())
        {
            return true;
        }

        var shapes = page.Paths.Count(p => p.IsFilled && p.GetBoundingRectangle() is { } r && r.Width < page.Width * 0.95);
        return shapes >= MinArtworkShapes;
    }

    public async Task<IReadOnlyList<VisionResult>> DescribeAsync(string pdfPath, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(CacheDir);
        var pdfBytes = await File.ReadAllBytesAsync(pdfPath, cancellationToken);
        var results = new List<VisionResult>();

        using var document = PdfDocument.Open(pdfBytes);
        var pageCount = document.NumberOfPages;
        foreach (var page in document.GetPages())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsVisualPage(page))
            {
                logger.LogInformation("Vision: page {Page} skipped (plain text)", page.Number);
                continue;
            }

            progress?.Report($"Rendering page {page.Number} of {pageCount} for the vision model");
            using var stream = new MemoryStream(pdfBytes);
            using var bitmap = Conversion.ToImage(stream, page: page.Number - 1, options: new RenderOptions(Dpi: Dpi));
            using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            var png = data.ToArray();
            var hash = Convert.ToHexString(SHA256.HashData(png))[..16];

            // Cache key = page + image hash + model: re-ingesting never calls the model unless the page really changed.
            var cachePath = Path.Combine(CacheDir, $"page-{page.Number:00}.json");
            string? description = null;
            if (File.Exists(cachePath))
            {
                var cached = JsonSerializer.Deserialize<CacheEntry>(await File.ReadAllTextAsync(cachePath, cancellationToken));
                if (cached?.PngHash == hash && cached.Model == options.VisionModel)
                {
                    description = cached.Description;
                }
            }

            if (description == null)
            {
                logger.LogInformation("Vision: page {Page} asking {Model}...", page.Number, options.VisionModel);
                progress?.Report($"Describing page {page.Number} of {pageCount} with {options.VisionModel}");
                description = await AskAsync(png, cancellationToken);
                var entry = new CacheEntry(page.Number, hash, options.VisionModel, description);
                await File.WriteAllTextAsync(cachePath, JsonSerializer.Serialize(entry, WriteOptions), cancellationToken);
            }
            else
            {
                logger.LogInformation("Vision: page {Page} cached", page.Number);
            }

            var section = ProseExtractor.SectionTitle(page.GetWords().ToList());
            results.Add(new VisionResult(page.Number, section, description));
        }

        return results;
    }

    private async Task<string> AskAsync(byte[] png, CancellationToken cancellationToken)
    {
        // Timeouts, retries with backoff and 429 handling live in IGeminiClient.
        var result = await gemini.GenerateAsync(new ChatRequest(
            options.VisionModel, SystemInstruction: null, UserMessage: ImacPrompts.VisionPrompt, Temperature: 0,
            Images: [new AdvancedRag.Core.Ai.InlineImage(png, "image/png")]), cancellationToken);
        return result.Text.Trim();
    }

    private record CacheEntry(int Page, string PngHash, string Model, string Description);
}
