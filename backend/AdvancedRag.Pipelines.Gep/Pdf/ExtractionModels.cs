namespace AdvancedRag.Pipelines.Gep.Pdf;

/// <summary>Which column of a two-column page a line belongs to.</summary>
public enum ColumnPosition
{
    Left,
    Right,
    FullWidth,
}

/// <summary>
/// What a line *is*, decided by <see cref="GepLayoutClassifier"/> from its font and position.
/// Headings get their own roles so the chunker can rebuild the chapter/section hierarchy.
/// </summary>
public enum LineRole
{
    Unclassified,

    // Page furniture: repeated on every page, never content.
    RunningHeader,
    PageFooter,

    // Content text.
    Body,            // 11pt main narrative
    BoxBody,         // 10.6pt text inside a shaded box article
    Note,            // 8pt Garamond: footnotes, "Note: this chapter was prepared by..."
    FigureText,      // small Helvetica: chart labels, figure descriptions, sources
    TableText,       // rows of the statistical tables

    // Headings, largest to smallest.
    CoverTitle,      // report cover pages
    FrontMatterTitle,// "Contents", "Foreword", "Executive Summary", ...
    ChapterLabel,    // "CHAPTER 3"
    ChapterTitle,    // "A RISING CHALLENGE Sovereign Debt Levels and Interest Rates in EMDEs"
    SectionHeading,  // 16pt bold: "Global context", "South Asia", "ANNEX 3.1 ..."
    MinorSectionHeading, // 14pt bold: Chapter 2 intro "Recent developments", "Outlook", "Risks"
    SubsectionHeading,   // 11pt Helvetica: "Global trade", region "Outlook"
    BoxTitle,        // "BOX 1.1 How much will AI affect global growth?"
    FigureTitle,     // "FIGURE 1.1 Global economic prospects"
    TableTitle,      // "TABLE 1.1 Real GDP"
}

/// <summary>One visual line of text (or one column segment of a line) with layout facts.</summary>
public sealed record TextLine
{
    public required string Text { get; init; }
    public required double FontSize { get; init; }
    /// <summary>Font name without the PDF subset prefix, e.g. "HelveticaLTStd-Bold".</summary>
    public required string FontName { get; init; }
    /// <summary>Left edge in PDF points (origin bottom-left, 72 points = 1 inch).</summary>
    public required double X { get; init; }
    /// <summary>Baseline height in PDF points; larger = higher on the page.</summary>
    public required double Y { get; init; }
    public required double Width { get; init; }
    public required ColumnPosition Column { get; init; }
    /// <summary>Position in the reconstructed reading order of the page (0-based).</summary>
    public required int ReadingOrder { get; init; }
    public LineRole Role { get; init; } = LineRole.Unclassified;

    public bool IsBold => FontName.Contains("Bold", StringComparison.OrdinalIgnoreCase);
    public bool IsHelvetica => FontName.Contains("Helvetica", StringComparison.OrdinalIgnoreCase);
}

public sealed record ExtractedPage
{
    /// <summary>1-based physical page number inside the PDF file.</summary>
    public required int PdfPageNumber { get; init; }
    /// <summary>The number printed on the page ("74", "xvii"), when found in the header/footer.</summary>
    public string? PrintedPageLabel { get; init; }
    /// <summary>
    /// Part of the report named in the running header: "CHAPTER 2", "STATISTICAL APPENDIX",
    /// "SELECTED TOPICS". Null for front matter and cover pages (no running header).
    /// </summary>
    public string? RunningHeaderLabel { get; init; }
    public required bool HasExtractableText { get; init; }
    public required bool HasImages { get; init; }
    public required IReadOnlyList<TextLine> Lines { get; init; }
}

public sealed record ExtractedDocument
{
    public required string FileName { get; init; }
    public required int PageCount { get; init; }
    public required IReadOnlyList<ExtractedPage> Pages { get; init; }
    public required TimeSpan Duration { get; init; }
}
