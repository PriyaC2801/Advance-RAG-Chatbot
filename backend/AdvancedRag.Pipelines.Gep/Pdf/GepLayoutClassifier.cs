using System.Text.RegularExpressions;

namespace AdvancedRag.Pipelines.Gep.Pdf;

/// <summary>A heading found in the PDF, possibly merged from several visual lines.</summary>
public sealed record HeadingCandidate(
    int PdfPageNumber,
    string? PrintedPageLabel,
    LineRole Role,
    string Text,
    double FontSize,
    ColumnPosition Column,
    int ReadingOrder,
    bool IsContinuation);

/// <summary>
/// Assigns a <see cref="LineRole"/> to every extracted line using the typography of the
/// Global Economic Prospects report (observed by inspecting the PDF's fonts):
///
///   26pt Helvetica Condensed  → front-matter title  ("Executive Summary", "Foreword")
///   26pt Garamond "CHAPTER n" → chapter label; 26–30pt Garamond/Minion text → chapter title
///   16pt Helvetica Bold       → section heading     ("Global context", "South Asia", "ANNEX 3.1 …")
///   14pt Helvetica Bold       → minor section       (Chapter 2 intro "Recent developments")
///   11pt Helvetica Roman      → subsection heading  ("Global trade", "Outlook")
///   10pt Helvetica Bold       → BOX / FIGURE / TABLE titles
///   11pt Garamond             → body text;  10.6pt Garamond → box body;  ≤9pt Garamond → notes
///   small Helvetica           → chart labels, figure notes, table cells
///   ≤8pt text at the very top/bottom of the page → running header / page footer
///
/// These rules are specific to this publication's design. That is the honest trade-off of
/// layout-based structure detection: very accurate for one document family, but it must be
/// re-checked for a different publisher.
/// </summary>
public sealed partial class GepLayoutClassifier
{
    private const double PageHeight = 792; // US Letter, points
    private const double HeaderZone = 42;  // top/bottom band where running headers and folios live

    public ExtractedPage Classify(ExtractedPage page)
    {
        var classified = page.Lines.Select(line => line with { Role = ClassifyLine(line) }).ToList();
        classified = ResolveTitleContinuations(classified);
        classified = ResolveTableText(classified, page);

        var headerLines = classified.Where(l => l.Role is LineRole.RunningHeader or LineRole.PageFooter).ToList();

        return page with
        {
            Lines = classified,
            RunningHeaderLabel = FindRunningHeaderLabel(headerLines),
            PrintedPageLabel = FindPrintedPageLabel(headerLines),
        };
    }

    /// <summary>Rules for a single line, without context from neighbouring lines.</summary>
    public static LineRole ClassifyLine(TextLine line)
    {
        var size = line.FontSize;
        var font = line.FontName;
        var isSerif = font.Contains("Garamond", StringComparison.OrdinalIgnoreCase)
                      || font.Contains("Minion", StringComparison.OrdinalIgnoreCase);

        if (size <= 9 && line.Y >= PageHeight - HeaderZone - 10) return LineRole.RunningHeader;
        if (size <= 9 && line.Y <= HeaderZone + 10) return LineRole.PageFooter;

        if (size >= 40 || font.StartsWith("Andes", StringComparison.OrdinalIgnoreCase)) return LineRole.CoverTitle;

        if (size >= 24)
        {
            if (font.Contains("Cond", StringComparison.OrdinalIgnoreCase)) return LineRole.FrontMatterTitle;
            return ChapterLabelPattern().IsMatch(line.Text) ? LineRole.ChapterLabel : LineRole.ChapterTitle;
        }

        if (line.IsHelvetica && line.IsBold)
        {
            if (size is >= 15.5 and <= 16.5) return LineRole.SectionHeading;
            if (size is >= 13.5 and <= 14.5) return LineRole.MinorSectionHeading;
            if (size is >= 9.5 and <= 10.5)
            {
                if (line.Text.StartsWith("BOX ", StringComparison.Ordinal)) return LineRole.BoxTitle;
                if (line.Text.StartsWith("FIGURE ", StringComparison.Ordinal)) return LineRole.FigureTitle;
                if (line.Text.StartsWith("TABLE ", StringComparison.Ordinal)) return LineRole.TableTitle;
                return LineRole.Unclassified; // maybe the 2nd line of a title — resolved with context
            }
        }

        if (line.IsHelvetica && !line.IsBold && size is >= 10.5 and <= 11.5
            && !font.Contains("Obl", StringComparison.OrdinalIgnoreCase))
        {
            return LineRole.SubsectionHeading;
        }

        if (isSerif)
        {
            if (size is >= 10.8 and <= 11.5) return LineRole.Body;
            if (size is >= 10.3 and < 10.8) return LineRole.BoxBody;
            if (size <= 9) return LineRole.Note;
            return LineRole.Body;
        }

        if (line.IsHelvetica && size < 9.5) return LineRole.FigureText;

        return LineRole.Unclassified;
    }

    /// <summary>
    /// A 10pt bold line without "BOX/FIGURE/TABLE" right below such a title is its second line
    /// (e.g. "BOX 2.1 Low-income countries: Recent developments" / "and outlook").
    /// </summary>
    private static List<TextLine> ResolveTitleContinuations(List<TextLine> lines)
    {
        var result = new List<TextLine>(lines.Count);
        foreach (var line in lines)
        {
            var updated = line;
            if (line.Role == LineRole.Unclassified && line.IsHelvetica && line.IsBold && line.FontSize is >= 9.5 and <= 10.5)
            {
                var title = result.LastOrDefault(l => l.Role is LineRole.BoxTitle or LineRole.FigureTitle or LineRole.TableTitle
                                                      && Math.Abs(l.Y - line.Y) <= 2.5 * line.FontSize);
                updated = line with { Role = title?.Role ?? LineRole.FigureText };
            }
            result.Add(updated);
        }
        return result;
    }

    /// <summary>Small Helvetica text following a TABLE title (or anywhere in the appendix) is table content.</summary>
    private static List<TextLine> ResolveTableText(List<TextLine> lines, ExtractedPage page)
    {
        var inAppendix = lines.Any(l => l.Role == LineRole.RunningHeader
                                        && l.Text.Contains("STATISTICAL APPENDIX", StringComparison.Ordinal));
        var lastTitle = LineRole.Unclassified;
        var result = new List<TextLine>(lines.Count);

        foreach (var line in lines)
        {
            if (line.Role is LineRole.FigureTitle or LineRole.TableTitle) lastTitle = line.Role;
            var isTable = line.Role == LineRole.FigureText && (inAppendix || lastTitle == LineRole.TableTitle);
            result.Add(isTable ? line with { Role = LineRole.TableText } : line);
        }
        return result;
    }

    private static string? FindRunningHeaderLabel(List<TextLine> headerLines)
    {
        foreach (var line in headerLines)
        {
            var chapter = ChapterInHeaderPattern().Match(line.Text);
            if (chapter.Success) return $"CHAPTER {chapter.Groups[1].Value}";
            if (line.Text.Contains("STATISTICAL APPENDIX", StringComparison.Ordinal)) return "STATISTICAL APPENDIX";
            if (line.Text.Contains("SELECTED TOPICS", StringComparison.Ordinal)) return "SELECTED TOPICS";
        }
        return null;
    }

    /// <summary>The printed folio, e.g. "74" or "xvii" — what a reader would cite.</summary>
    private static string? FindPrintedPageLabel(List<TextLine> headerLines)
    {
        foreach (var line in headerLines)
        {
            var withoutChapter = ChapterInHeaderPattern().Replace(line.Text, " ");
            var token = withoutChapter.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .LastOrDefault(t => PageLabelPattern().IsMatch(t));
            if (token is not null) return token;
        }
        return null;
    }

    /// <summary>
    /// Builds the heading list: consecutive heading lines of the same kind (a title wrapped onto
    /// two or three lines) are merged into one heading.
    /// </summary>
    public static IReadOnlyList<HeadingCandidate> ExtractHeadings(IEnumerable<ExtractedPage> pages)
    {
        var headings = new List<HeadingCandidate>();

        foreach (var page in pages)
        {
            foreach (var line in MergeWrappedHeadings(page.Lines).Where(l => IsHeadingRole(l.Role)))
            {
                // Boxes repeat their title on every page with "(continued)"; flag those repeats.
                headings.Add(new HeadingCandidate(
                    page.PdfPageNumber, page.PrintedPageLabel, line.Role, StripContinued(line.Text),
                    line.FontSize, line.Column, line.ReadingOrder,
                    IsContinuation: IsContinued(line.Text)));
            }
        }

        return headings;
    }

    /// <summary>
    /// Returns the page's lines with a heading that wraps onto several visual lines combined into
    /// one line ("Middle East, North Africa," + "Afghanistan, and Pakistan").
    /// </summary>
    public static IReadOnlyList<TextLine> MergeWrappedHeadings(IReadOnlyList<TextLine> lines)
    {
        var result = new List<TextLine>(lines.Count);
        TextLine? previousHeading = null; // last source line of the heading being built

        foreach (var line in lines)
        {
            if (!IsHeadingRole(line.Role))
            {
                result.Add(line);
                previousHeading = null;
                continue;
            }

            // Same kind, directly below, same column. The gap is compared to the font size
            // because chapter titles mix 30pt and 26pt lines.
            var continuesPrevious = previousHeading is not null && previousHeading.Role == line.Role
                && (previousHeading.Y - line.Y) > 0
                && (previousHeading.Y - line.Y) <= Math.Max(previousHeading.FontSize, line.FontSize) * 1.7
                && (previousHeading.Column == line.Column
                    || previousHeading.Column == ColumnPosition.FullWidth
                    || line.Column == ColumnPosition.FullWidth
                    || line.Role is LineRole.ChapterTitle);

            if (continuesPrevious)
            {
                result[^1] = result[^1] with { Text = $"{result[^1].Text} {line.Text}" };
            }
            else
            {
                result.Add(line);
            }
            previousHeading = line;
        }

        return result;
    }

    public static bool IsContinued(string text) => text.EndsWith("(continued)", StringComparison.Ordinal);

    public static string StripContinued(string text) =>
        text.Replace("(continued)", string.Empty, StringComparison.Ordinal).Trim();

    public static bool IsHeadingRole(LineRole role) => role is
        LineRole.FrontMatterTitle or LineRole.ChapterLabel or LineRole.ChapterTitle or
        LineRole.SectionHeading or LineRole.MinorSectionHeading or LineRole.SubsectionHeading or
        LineRole.BoxTitle or LineRole.FigureTitle or LineRole.TableTitle;

    [GeneratedRegex(@"^CHAPTER\s+\d+$")]
    private static partial Regex ChapterLabelPattern();

    [GeneratedRegex(@"CHAPTER\s+(\d+)")]
    private static partial Regex ChapterInHeaderPattern();

    [GeneratedRegex(@"^(\d{1,3}|[ivxlc]{1,6})$")]
    private static partial Regex PageLabelPattern();
}
