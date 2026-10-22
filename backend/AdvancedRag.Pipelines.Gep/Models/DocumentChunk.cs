using System.Text.Json.Serialization;

namespace AdvancedRag.Pipelines.Gep.Models;

/// <summary>What kind of content a chunk holds. Retrieval can filter or weight by this.</summary>
public enum ContentType
{
    ExecutiveSummary,
    FrontMatter,        // Foreword, Acknowledgments, Abbreviations
    Narrative,          // main chapter text
    RegionalOutlook,    // Chapter 2 text inside one region's section
    BoxArticle,         // "BOX 3.1 The costs of hidden debt"
    Table,              // rows of a numbered table, e.g. TABLE 2.10
    FigureCaption,      // figure title + one-sentence description + chart data read by a vision model (approximate)
    Annex,              // "ANNEX 3.1 Methodology and data"
    StatisticalAppendix,
    References,         // bibliographies and the "Selected Topics" publication lists
}

/// <summary>
/// One retrievable piece of the report plus everything we know about where it came from.
/// The metadata comes from headings detected in the PDF — never from keywords in the text.
/// </summary>
public sealed record DocumentChunk
{
    /// <summary>Stable id, e.g. "gep-2026-06-c0412".</summary>
    public required string Id { get; init; }
    public required string DocumentId { get; init; }
    /// <summary>Position in document order (0-based).</summary>
    public required int ChunkIndex { get; init; }

    /// <summary>The original report text of this chunk (what gets cited).</summary>
    public required string Text { get; init; }

    /// <summary>1–4 for chapters; null for front matter and the statistical appendix.</summary>
    public int? ChapterNumber { get; init; }
    /// <summary>"Regional Outlooks", or "Executive Summary" for front matter.</summary>
    public required string ChapterTitle { get; init; }
    /// <summary>
    /// Section heading ("South Asia"), or the box title for box articles. Text that appears before
    /// a chapter's first section heading uses the chapter title.
    /// </summary>
    public required string SectionTitle { get; init; }
    /// <summary>Subsection heading ("Outlook"), or the table/figure title for those chunks.</summary>
    public string? SubsectionTitle { get; init; }
    /// <summary>EMDE region, set only from a Chapter 2 region heading or a table's region row.</summary>
    public string? Region { get; init; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public required ContentType ContentType { get; init; }

    /// <summary>Physical pages in the PDF file (1-based).</summary>
    public required int PdfPageNumberStart { get; init; }
    public required int PdfPageNumberEnd { get; init; }
    /// <summary>Page numbers printed in the report ("74"), for citations. Null on unnumbered pages.</summary>
    public string? PrintedPageStart { get; init; }
    public string? PrintedPageEnd { get; init; }

    /// <summary>Readable location, e.g. "Chapter 2: Regional Outlooks > South Asia > Outlook".</summary>
    public required string HeadingPath { get; init; }

    /// <summary>Vector from the embedding model. Stored in embeddings.json, not chunks.json.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float[]? Embedding { get; init; }
}
