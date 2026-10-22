using AdvancedRag.Pipelines.Gep.Models;
using AdvancedRag.Pipelines.Gep.Pdf;
using Microsoft.Extensions.Options;

namespace AdvancedRag.Pipelines.Gep.Chunking;

public interface IDocumentChunker
{
    IReadOnlyList<DocumentChunk> Chunk(IReadOnlyList<ExtractedPage> pages, string documentId);
}

/// <summary>
/// Pipeline: classified pages → segments (structure walker) → text pieces (splitter / table
/// formatter) → <see cref="DocumentChunk"/>s with full heading metadata and page ranges.
/// </summary>
public sealed class StructureAwareChunker(IOptions<GepOptions> options) : IDocumentChunker
{
    /// <summary>A chunk before it gets its id and index.</summary>
    private sealed record Draft(Segment Segment, string Text, IReadOnlyList<SourceLine> Lines, string? Region);

    public IReadOnlyList<DocumentChunk> Chunk(IReadOnlyList<ExtractedPage> pages, string documentId)
    {
        var settings = options.Value.Chunking;
        var segments = new DocumentStructureWalker(settings).Walk(pages);
        var dehyphenator = Dehyphenator.Build(
            segments.Where(s => s.Stream != SegmentStream.Table).SelectMany(s => s.Lines).Select(l => l.Text));
        var splitter = new TextSplitter(settings);
        var tables = new TableFormatter(settings.RegionNames);

        var drafts = new List<Draft>();
        foreach (var segment in segments)
        {
            drafts.AddRange(segment.Stream switch
            {
                SegmentStream.Table => TableDrafts(segment, tables, splitter, dehyphenator, settings),
                SegmentStream.Figure => [FigureDraft(segment, dehyphenator)],
                _ => ProseDrafts(segment, segment.Lines, splitter, dehyphenator, prefix: null, segment.Context.Region),
            });
        }

        return drafts
            .Where(d => HasRealContent(d.Text))
            .Select((d, index) => ToChunk(d, index, documentId))
            .ToList();
    }

    /// <summary>
    /// Drops noise such as formula subscripts ("jt | j | jt-1") that survive extraction.
    /// Rule: at least four words made of three or more letters.
    /// </summary>
    public static bool HasRealContent(string text) =>
        text.Split((char[])[' ', '\n', '|'], StringSplitOptions.RemoveEmptyEntries)
            .Count(w => w.Count(char.IsLetter) >= 3) >= 4;

    /// <summary>Running text: join lines, repair hyphenation, cut at sentence boundaries with overlap.</summary>
    private static IEnumerable<Draft> ProseDrafts(Segment segment, IReadOnlyList<SourceLine> lines,
        TextSplitter splitter, Dehyphenator dehyphenator, string? prefix, string? region)
    {
        if (lines.Count == 0) yield break;
        var joined = dehyphenator.Join(lines.Select(l => l.Text).ToList());

        foreach (var piece in splitter.Split(joined.Text))
        {
            var first = LineIndexAt(joined.LineStarts, piece.Start);
            var last = LineIndexAt(joined.LineStarts, Math.Max(piece.Start, piece.End - 1));
            var text = prefix is null ? piece.Text : $"{prefix}\n{piece.Text}";
            yield return new Draft(segment, text, lines.Skip(first).Take(last - first + 1).ToList(), region);
        }
    }

    /// <summary>Figure title plus its one-sentence description — the chart data itself is not text.</summary>
    private static Draft FigureDraft(Segment segment, Dehyphenator dehyphenator)
    {
        var description = segment.Lines.Count > 0 ? dehyphenator.Join(segment.Lines.Select(l => l.Text).ToList()).Text : null;
        var text = description is null ? segment.Title! : $"{segment.Title}\n{description}";
        var lines = segment.Lines.Count > 0 ? segment.Lines : [segment.StartLocation];
        return new Draft(segment, text, lines, segment.Context.Region);
    }

    /// <summary>
    /// One or more chunks per region group. Every chunk repeats the table title and unit, so a chunk
    /// read on its own still says what the numbers are.
    /// </summary>
    private static IEnumerable<Draft> TableDrafts(Segment segment, TableFormatter formatter, TextSplitter splitter,
        Dehyphenator dehyphenator, ChunkingOptions settings)
    {
        var title = segment.Title ?? segment.Context.SectionTitle;
        var table = formatter.Format(title, segment.Lines, segment.Context.Region);
        var heading = table.Unit is null ? title : $"{title} {table.Unit}";

        if (table.Groups.Count == 0 && table.Notes.Count == 0)
        {
            yield return new Draft(segment, heading, [segment.StartLocation], segment.Context.Region);
            yield break;
        }

        foreach (var group in table.Groups)
        {
            var prefix = group.Region is null ? heading : $"{heading}\nRegion: {group.Region}";
            var budget = Math.Max(200, settings.MaxChunkCharacters - prefix.Length);
            foreach (var rows in splitter.Pack(group.Rows, r => r.Text.Length, budget))
            {
                var text = $"{prefix}\n{string.Join('\n', rows.Select(r => r.Text))}";
                yield return new Draft(segment, text, rows, group.Region);
            }
        }

        // Table notes matter: e.g. "Fiscal year runs from April 1 to March 31 in India".
        foreach (var draft in ProseDrafts(segment, table.Notes, splitter, dehyphenator, $"{title} — notes:", segment.Context.Region))
        {
            yield return draft;
        }
    }

    private static DocumentChunk ToChunk(Draft draft, int index, string documentId)
    {
        var context = draft.Segment.Context;
        var firstLine = draft.Lines.MinBy(l => l.PdfPage)!;
        var lastLine = draft.Lines.MaxBy(l => l.PdfPage)!;

        return new DocumentChunk
        {
            Id = $"{documentId}-c{index:D4}",
            DocumentId = documentId,
            ChunkIndex = index,
            Text = draft.Text,
            ChapterNumber = context.ChapterNumber,
            ChapterTitle = context.ChapterTitle,
            SectionTitle = context.SectionTitle,
            SubsectionTitle = context.SubsectionTitle,
            Region = draft.Region,
            ContentType = draft.Segment.ContentType,
            PdfPageNumberStart = firstLine.PdfPage,
            PdfPageNumberEnd = lastLine.PdfPage,
            PrintedPageStart = firstLine.PrintedPage,
            PrintedPageEnd = lastLine.PrintedPage,
            HeadingPath = BuildHeadingPath(context),
        };
    }

    public static string BuildHeadingPath(SectionContext context)
    {
        var parts = new List<string>
        {
            context.ChapterNumber is int n ? $"Chapter {n}: {context.ChapterTitle}" : context.ChapterTitle,
        };
        if (context.SectionTitle != context.ChapterTitle) parts.Add(context.SectionTitle);
        if (context.SubsectionTitle is not null) parts.Add(context.SubsectionTitle);
        return string.Join(" > ", parts);
    }

    /// <summary>Index of the source line that contains character offset <paramref name="offset"/>.</summary>
    private static int LineIndexAt(IReadOnlyList<int> lineStarts, int offset)
    {
        var low = 0;
        var high = lineStarts.Count - 1;
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            if (lineStarts[mid] <= offset) low = mid; else high = mid - 1;
        }
        return low;
    }
}
