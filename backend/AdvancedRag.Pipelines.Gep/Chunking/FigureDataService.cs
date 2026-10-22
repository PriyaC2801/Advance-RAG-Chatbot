using System.Text.Encodings.Web;
using System.Text.Json;
using AdvancedRag.Core.Ai;
using AdvancedRag.Pipelines.Gep.Models;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Writer;

namespace AdvancedRag.Pipelines.Gep.Chunking;

public interface IFigureDataService
{
    /// <summary>Returns the chunks with chart data appended to every figure chunk that could be read.</summary>
    Task<IReadOnlyList<DocumentChunk>> EnrichAsync(IReadOnlyList<DocumentChunk> chunks, string pdfPath,
        string pdfSha256, string dataDirectory, IProgress<string>? progress, CancellationToken cancellationToken);
}

/// <summary>What the model returns for one page: one entry per requested figure.</summary>
public sealed class FigurePageResult
{
    public List<FigureEntry> Figures { get; set; } = [];
}

public sealed class FigureEntry
{
    public string Title { get; set; } = string.Empty;
    /// <summary>Plain-text data: unit, period, then one line per series or category.</summary>
    public string Data { get; set; } = string.Empty;
}

/// <summary>Contents of data/figure-data.json: figure key ("page|title") → extracted data text.</summary>
public sealed record FigureDataCache(string PdfSha256, string Version, string Model, Dictionary<string, string> Entries);

public sealed class FigureDataService(
    IOptions<GepOptions> options,
    IGeminiClient gemini,
    ILogger<FigureDataService> logger) : IFigureDataService
{
    /// <summary>Bump when the prompt changes so old cached readings are discarded.</summary>
    public const string PromptVersion = "1";
    public const string CacheFileName = "figure-data.json";
    public const string DataHeader = "Chart data (read from the figure by a vision model; values read off graphics are approximate):";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private const string Instruction =
        "You read charts from World Bank reports and transcribe their data. Use only what is drawn on the page. " +
        "Never invent numbers. If a value is printed as a label, copy it exactly. If you read it off the axis, " +
        "prefix it with '~'. If a figure has no readable data (a map, a photo or a diagram), say so in one short sentence. " +
        "Ignore the body text of the page, except for notes printed directly under the figure (units, sources, definitions).";

    public async Task<IReadOnlyList<DocumentChunk>> EnrichAsync(IReadOnlyList<DocumentChunk> chunks, string pdfPath,
        string pdfSha256, string dataDirectory, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var settings = options.Value.FigureData;
        var figures = chunks.Where(c => c.ContentType == ContentType.FigureCaption).ToList();
        if (!settings.Enabled || figures.Count == 0) return chunks;

        var model = string.IsNullOrWhiteSpace(settings.Model) ? options.Value.GenerationModel : settings.Model;
        var cache = await LoadCacheAsync(dataDirectory, pdfSha256, model, cancellationToken);

        var missingByPage = figures
            .Where(c => !cache.Entries.ContainsKey(Key(c)))
            .GroupBy(c => c.PdfPageNumberStart)
            .OrderBy(g => g.Key)
            .ToList();

        if (missingByPage.Count > 0 && !gemini.IsConfigured)
        {
            logger.LogWarning("Gemini API key is not configured; {Count} figures keep caption-only chunks", missingByPage.Sum(g => g.Count()));
        }
        else if (missingByPage.Count > 0)
        {
            logger.LogInformation("Reading chart data for {Figures} figures on {Pages} pages with {Model}",
                missingByPage.Sum(g => g.Count()), missingByPage.Count, model);
            var failedPages = 0;
            var done = 0;

            foreach (var page in missingByPage)
            {
                try
                {
                    progress?.Report($"Reading chart on page {page.Key} ({++done}/{missingByPage.Count})");
                    var result = await ReadPageAsync(pdfPath, page.Key, page.ToList(), model, cancellationToken);
                    foreach (var figure in page)
                    {
                        var entry = FindEntry(result, figure, expectedCount: page.Count());
                        if (entry is not null && !string.IsNullOrWhiteSpace(entry.Data))
                        {
                            cache.Entries[Key(figure)] = entry.Data.Trim();
                        }
                        else
                        {
                            logger.LogWarning("No chart data matched '{Title}' on PDF page {Page}; model returned {Count} entries: {Titles}",
                                figure.SubsectionTitle, page.Key, result.Figures.Count, string.Join(" | ", result.Figures.Select(f => f.Title)));
                        }
                    }
                    await SaveCacheAsync(dataDirectory, cache, cancellationToken); // resume-friendly: save per page
                    await Task.Delay(settings.DelayBetweenCallsMilliseconds, cancellationToken);
                }
                catch (GeminiApiException ex) when (ex.Kind != GeminiErrorKind.MissingApiKey)
                {
                    failedPages++;
                    logger.LogWarning("Chart data for PDF page {Page} failed ({Kind}); those figures keep caption-only chunks", page.Key, ex.Kind);
                }
            }

            if (failedPages > 0)
            {
                logger.LogWarning("{Failed} of {Total} figure pages failed; rebuild with force=true to retry only those", failedPages, missingByPage.Count);
            }
        }

        return chunks.Select(c => c.ContentType == ContentType.FigureCaption && cache.Entries.TryGetValue(Key(c), out var data)
            ? c with { Text = $"{c.Text}\n{DataHeader}\n{Truncate(data, settings.MaxDataCharacters)}" }
            : c).ToList();
    }

    private async Task<FigurePageResult> ReadPageAsync(string pdfPath, int pageNumber,
        List<DocumentChunk> figures, string model, CancellationToken cancellationToken)
    {
        var pagePdf = ExtractSinglePage(pdfPath, pageNumber);
        var titles = string.Join("\n", figures.Select(f => $"- {f.SubsectionTitle}"));

        var prompt =
            "This PDF page contains the figures listed below. For each one, transcribe the chart data as plain text: " +
            "the unit and period, then one line per series or category with its values " +
            "(e.g. \"Advanced economies: 2024 1.8; 2025 1.6; 2026 1.5\"). For multi-panel figures, label each panel " +
            "(\"Panel A. ...\"). Keep it compact: no commentary.\n\n" +
            "Answer in plain text, with one block per figure that starts with a line of the form\n" +
            "=== <figure title exactly as listed> ===\n" +
            "followed by the data lines.\n\nFigures:\n" + titles;

        // Plain text, not a JSON schema: the small models filled the title field with the data and looped.
        var reply = await gemini.GenerateAsync(new ChatRequest(
            model, Instruction, prompt, MaxOutputTokens: options.Value.FigureData.MaxOutputTokens,
            Images: [new InlineImage(pagePdf, "application/pdf")]), cancellationToken);

        return ParseBlocks(reply.Text);
    }

    /// <summary>Splits "=== title ===" blocks into entries.</summary>
    public static FigurePageResult ParseBlocks(string reply)
    {
        var result = new FigurePageResult();
        foreach (var block in reply.Split("===", StringSplitOptions.None).Skip(1).Chunk(2))
        {
            if (block.Length < 2) continue;
            result.Figures.Add(new FigureEntry { Title = block[0].Trim(), Data = block[1].Trim() });
        }
        return result;
    }

    /// <summary>A one-page PDF copy of the page, so the model sees the real chart and not the whole report.</summary>
    private static byte[] ExtractSinglePage(string pdfPath, int pageNumber)
    {
        using var document = PdfDocument.Open(pdfPath);
        var builder = new PdfDocumentBuilder();
        builder.AddPage(document, pageNumber);
        return builder.Build();
    }

    private static string Key(DocumentChunk figure) => $"{figure.PdfPageNumberStart}|{figure.SubsectionTitle}";

    /// <summary>
    /// Matches a model entry to a figure: same title, one title contains the other, or the same figure
    /// number ("1.1", "B1.1.1"). A lone entry for a lone figure on the page is accepted as is.
    /// </summary>
    private static FigureEntry? FindEntry(FigurePageResult result, DocumentChunk figure, int expectedCount)
    {
        var title = Normalize(figure.SubsectionTitle);
        var number = FigureNumber(title);
        var match = result.Figures.FirstOrDefault(f =>
        {
            var t = Normalize(f.Title);
            return t.Length > 0 && (t.Equals(title, StringComparison.OrdinalIgnoreCase)
                || t.Contains(title, StringComparison.OrdinalIgnoreCase)
                || title.Contains(t, StringComparison.OrdinalIgnoreCase)
                || (number is not null && string.Equals(FigureNumber(t), number, StringComparison.OrdinalIgnoreCase)));
        });
        return match ?? (expectedCount == 1 && result.Figures.Count == 1 ? result.Figures[0] : null);
    }

    /// <summary>"FIGURE B1.1.1 AI, productivity" → "B1.1.1".</summary>
    private static string? FigureNumber(string title)
    {
        var m = System.Text.RegularExpressions.Regex.Match(title, @"^FIGURE\s+([A-Z]?\d+(?:\.\d+)*)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string Normalize(string? s) => string.Join(' ', (s ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max].TrimEnd() + " …";

    private static async Task<FigureDataCache> LoadCacheAsync(string dataDirectory, string pdfSha256, string model, CancellationToken ct)
    {
        var path = Path.Combine(dataDirectory, CacheFileName);
        if (File.Exists(path))
        {
            try
            {
                await using var stream = File.OpenRead(path);
                var cache = await JsonSerializer.DeserializeAsync<FigureDataCache>(stream, JsonOptions, ct);
                if (cache is not null && cache.PdfSha256 == pdfSha256 && cache.Version == PromptVersion && cache.Model == model)
                {
                    return cache;
                }
            }
            catch (JsonException)
            {
                // Corrupt cache: start over.
            }
        }
        return new FigureDataCache(pdfSha256, PromptVersion, model, []);
    }

    private static async Task SaveCacheAsync(string dataDirectory, FigureDataCache cache, CancellationToken ct)
    {
        Directory.CreateDirectory(dataDirectory);
        var path = Path.Combine(dataDirectory, CacheFileName);
        var temp = path + ".tmp";
        await using (var stream = File.Create(temp))
        {
            await JsonSerializer.SerializeAsync(stream, cache, JsonOptions, ct);
        }
        File.Move(temp, path, overwrite: true);
    }
}
