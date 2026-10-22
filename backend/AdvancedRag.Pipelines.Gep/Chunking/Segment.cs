using AdvancedRag.Pipelines.Gep.Models;

namespace AdvancedRag.Pipelines.Gep.Chunking;

/// <summary>Which major part of the report the walker is in.</summary>
public enum ReportPart
{
    BeforeContent,   // covers and the copyright page — not indexed
    FrontMatter,
    ExecutiveSummary,
    Chapter,
    StatisticalAppendix,
    SelectedTopics,
}

/// <summary>The kind of text stream a segment holds; boxes, notes, figures and tables are kept apart from prose.</summary>
public enum SegmentStream
{
    Prose,
    Box,
    Note,
    Figure,
    Table,
}

/// <summary>"You are here" in the report at the moment a segment started.</summary>
public sealed record SectionContext(
    ReportPart Part,
    int? ChapterNumber,
    string ChapterTitle,
    string SectionTitle,
    string? SubsectionTitle,
    string? Region);

/// <summary>One extracted line with the page it came from.</summary>
public sealed record SourceLine(string Text, int PdfPage, string? PrintedPage);

/// <summary>
/// A run of consecutive lines that share the same location in the report (same chapter, section,
/// subsection, region and stream). Chunks are cut from segments and never cross a segment boundary.
/// </summary>
public sealed class Segment
{
    /// <summary>Order in which segments were opened = document order.</summary>
    public required int Sequence { get; init; }
    public required SectionContext Context { get; init; }
    public required SegmentStream Stream { get; init; }
    public required ContentType ContentType { get; init; }
    /// <summary>Figure or table title, for those streams.</summary>
    public string? Title { get; init; }
    /// <summary>Page where the segment started (used when a figure has a title but no description).</summary>
    public required SourceLine StartLocation { get; init; }
    public List<SourceLine> Lines { get; } = [];
}
