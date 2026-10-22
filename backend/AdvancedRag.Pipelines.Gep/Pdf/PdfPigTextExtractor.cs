using System.Diagnostics;
using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace AdvancedRag.Pipelines.Gep.Pdf;

/// <summary>
/// Extracts text lines from a PDF using PdfPig.
///
/// A PDF does not store "paragraphs" or "lines" — it stores individual glyphs (letters) at x/y
/// positions. This class rebuilds lines from those letters:
///   1. group letters that share a baseline (same y),
///   2. split a baseline into segments where there is a wide horizontal gap (the gutter between
///      the two text columns),
///   3. decide whether each segment is in the left column, right column, or spans the page,
///   4. order segments the way a person reads: left column top-to-bottom, then right column,
///      with full-width elements acting as separators.
/// </summary>
public sealed class PdfPigTextExtractor(ILogger<PdfPigTextExtractor> logger) : IDocumentTextExtractor
{
    /// <summary>Letters whose baselines differ by less than this (points) are on the same line.</summary>
    private const double BaselineTolerance = 1.0;

    /// <summary>A horizontal gap wider than this (points) starts a new segment (column gutter, table cell).</summary>
    private const double SegmentGapThreshold = 12.0;

    /// <summary>A gap wider than this fraction of the font size is treated as a word space.</summary>
    private const double WordGapFraction = 0.2;

    /// <summary>Segments wider than this fraction of the page are treated as full width.</summary>
    private const double FullWidthFraction = 0.55;

    /// <summary>A baseline with this many small-font segments is treated as one table/chart row.</summary>
    private const int TableRowMinSegments = 4;
    private const double TableRowMaxFontSize = 9.5;

    public Task<ExtractedDocument> ExtractAsync(string pdfPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(pdfPath))
        {
            throw new FileNotFoundException("The configured PDF was not found.", Path.GetFileName(pdfPath));
        }

        // PdfPig is synchronous and CPU-bound; run it off the request thread.
        return Task.Run(() => Extract(pdfPath, cancellationToken), cancellationToken);
    }

    private ExtractedDocument Extract(string pdfPath, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        using var document = PdfDocument.Open(pdfPath);

        var pages = new List<ExtractedPage>(document.NumberOfPages);
        for (var pageNumber = 1; pageNumber <= document.NumberOfPages; pageNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            pages.Add(ExtractPage(document.GetPage(pageNumber)));
        }

        stopwatch.Stop();
        logger.LogInformation(
            "Extracted {PageCount} pages from {FileName} in {ElapsedMs} ms",
            pages.Count, Path.GetFileName(pdfPath), stopwatch.ElapsedMilliseconds);

        return new ExtractedDocument
        {
            FileName = Path.GetFileName(pdfPath),
            PageCount = pages.Count,
            Pages = pages,
            Duration = stopwatch.Elapsed,
        };
    }

    private static ExtractedPage ExtractPage(Page page)
    {
        // Ignore rotated glyphs (vertical chart axis labels) — they would produce garbage lines.
        var letters = page.Letters
            .Where(l => !string.IsNullOrEmpty(l.Value) && l.TextOrientation == TextOrientation.Horizontal)
            .ToList();

        var segments = BuildSegments(letters, page.Width);
        var ordered = OrderForReading(segments);

        return new ExtractedPage
        {
            PdfPageNumber = page.Number,
            HasExtractableText = letters.Any(l => !string.IsNullOrWhiteSpace(l.Value)),
            HasImages = page.GetImages().Any(),
            Lines = ordered,
        };
    }

    private static List<TextLine> BuildSegments(List<Letter> letters, double pageWidth)
    {
        var result = new List<TextLine>();

        foreach (var baseline in GroupByBaseline(letters))
        {
            var segments = SplitIntoSegments(baseline);

            // Many small segments on one baseline = a table row or chart axis: keep them together,
            // otherwise each cell would become its own "line" scattered across columns.
            if (segments.Count >= TableRowMinSegments && segments.All(s => DominantSize(s) <= TableRowMaxFontSize))
            {
                result.Add(ToTextLine(segments.SelectMany(s => s).ToList(), pageWidth, " | ", segments));
                continue;
            }

            result.AddRange(segments.Select(s => ToTextLine(s, pageWidth, separator: null, cells: null)));
        }

        return result.Where(l => l.Text.Length > 0).ToList();
    }

    /// <summary>Groups letters into baselines, top of the page first.</summary>
    private static IEnumerable<List<Letter>> GroupByBaseline(List<Letter> letters)
    {
        var sorted = letters.OrderByDescending(l => l.StartBaseLine.Y).ThenBy(l => l.StartBaseLine.X);
        List<Letter>? current = null;
        double currentY = double.NaN;

        foreach (var letter in sorted)
        {
            if (current is null || Math.Abs(letter.StartBaseLine.Y - currentY) > BaselineTolerance)
            {
                if (current is not null) yield return current;
                current = [];
                currentY = letter.StartBaseLine.Y;
            }
            current.Add(letter);
        }

        if (current is not null) yield return current;
    }

    /// <summary>Splits one baseline left-to-right wherever there is a wide gap between visible letters.</summary>
    private static List<List<Letter>> SplitIntoSegments(List<Letter> baseline)
    {
        var segments = new List<List<Letter>>();
        List<Letter>? current = null;
        Letter? previousVisible = null;

        foreach (var letter in baseline.OrderBy(l => l.StartBaseLine.X))
        {
            var isVisible = !string.IsNullOrWhiteSpace(letter.Value);
            if (isVisible && previousVisible is not null &&
                letter.StartBaseLine.X - previousVisible.EndBaseLine.X > SegmentGapThreshold)
            {
                current = null; // gap too wide: start a new segment
            }

            if (current is null)
            {
                if (!isVisible) continue; // never start a segment with whitespace
                current = [];
                segments.Add(current);
            }

            current.Add(letter);
            if (isVisible) previousVisible = letter;
        }

        return segments;
    }

    private static TextLine ToTextLine(List<Letter> letters, double pageWidth, string? separator, List<List<Letter>>? cells)
    {
        var text = cells is not null && separator is not null
            ? string.Join(separator, cells.Select(BuildText))
            : BuildText(letters);

        var visible = letters.Where(l => !string.IsNullOrWhiteSpace(l.Value)).ToList();
        var left = visible.Min(l => l.StartBaseLine.X);
        var right = visible.Max(l => l.EndBaseLine.X);
        var width = right - left;
        var dominant = visible
            .GroupBy(l => (l.FontName, Size: Math.Round(l.PointSize, 1)))
            .OrderByDescending(g => g.Count())
            .First().Key;

        // Decide the column from the real extent only. (A chart legend row inside one column is
        // not full width; treating it as such would split that column's reading order in two.)
        var column = width > pageWidth * FullWidthFraction
            ? ColumnPosition.FullWidth
            : (left + right) / 2 < pageWidth / 2 ? ColumnPosition.Left : ColumnPosition.Right;

        return new TextLine
        {
            Text = text,
            FontSize = dominant.Size,
            FontName = StripSubsetPrefix(dominant.FontName),
            X = Math.Round(left, 1),
            Y = Math.Round(visible[0].StartBaseLine.Y, 1),
            Width = Math.Round(width, 1),
            Column = column,
            ReadingOrder = -1, // assigned in OrderForReading
        };
    }

    /// <summary>
    /// Joins letters into a string. We decide spaces ourselves from the geometry, because PDFs
    /// sometimes omit space glyphs or position letters with kerning that confuses word detection
    /// (PdfPig's default word splitter turned "government" into "g overnment" in this report).
    /// </summary>
    private static string BuildText(List<Letter> letters)
    {
        var builder = new StringBuilder();
        Letter? previousVisible = null;
        var pendingSpace = false;

        foreach (var letter in letters.OrderBy(l => l.StartBaseLine.X))
        {
            if (string.IsNullOrWhiteSpace(letter.Value))
            {
                pendingSpace = previousVisible is not null;
                continue;
            }

            if (previousVisible is not null)
            {
                var gap = letter.StartBaseLine.X - previousVisible.EndBaseLine.X;
                if (pendingSpace || gap > letter.PointSize * WordGapFraction)
                {
                    builder.Append(' ');
                }
            }

            builder.Append(letter.Value);
            previousVisible = letter;
            pendingSpace = false;
        }

        return TextNormalizer.Normalize(builder.ToString());
    }

    /// <summary>
    /// Reading order for a two-column layout: between full-width separators, read the whole left
    /// column top-to-bottom, then the right column.
    /// </summary>
    private static List<TextLine> OrderForReading(List<TextLine> segments)
    {
        var ordered = new List<TextLine>(segments.Count);
        var left = new List<TextLine>();
        var right = new List<TextLine>();

        void Flush()
        {
            ordered.AddRange(left);
            ordered.AddRange(right);
            left.Clear();
            right.Clear();
        }

        foreach (var segment in segments.OrderByDescending(s => s.Y).ThenBy(s => s.X))
        {
            switch (segment.Column)
            {
                case ColumnPosition.FullWidth:
                    Flush();
                    ordered.Add(segment);
                    break;
                case ColumnPosition.Left:
                    left.Add(segment);
                    break;
                default:
                    right.Add(segment);
                    break;
            }
        }

        Flush();
        return ordered.Select((line, index) => line with { ReadingOrder = index }).ToList();
    }

    private static double DominantSize(List<Letter> letters) =>
        letters.Where(l => !string.IsNullOrWhiteSpace(l.Value))
            .GroupBy(l => Math.Round(l.PointSize, 1))
            .OrderByDescending(g => g.Count())
            .First().Key;

    /// <summary>"WRTXTL+HelveticaLTStd-Bold" → "HelveticaLTStd-Bold" (the prefix marks a font subset).</summary>
    private static string StripSubsetPrefix(string? fontName)
    {
        if (string.IsNullOrEmpty(fontName)) return "Unknown";
        var plus = fontName.IndexOf('+');
        return plus == 6 ? fontName[(plus + 1)..] : fontName;
    }
}
