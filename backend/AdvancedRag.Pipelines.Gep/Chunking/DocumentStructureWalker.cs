using System.Globalization;
using System.Text.RegularExpressions;
using AdvancedRag.Pipelines.Gep.Models;
using AdvancedRag.Pipelines.Gep.Pdf;

namespace AdvancedRag.Pipelines.Gep.Chunking;

/// <summary>
/// Walks the classified pages in reading order, like a reader turning pages, and keeps track of
/// "where am I": part, chapter, section, subsection, region and current box. Every content line is
/// appended to the segment for its current location; any heading change closes that segment.
///
/// This is what makes chunking structure-aware: South Asia text and Sub-Saharan Africa text can
/// never land in the same segment, because the "Sub-Saharan Africa" heading closes the South Asia one.
/// </summary>
public sealed partial class DocumentStructureWalker(ChunkingOptions options)
{
    public IReadOnlyList<Segment> Walk(IReadOnlyList<ExtractedPage> pages)
    {
        var state = new WalkState(options);
        for (var p = 0; p < pages.Count; p++)
        {
            var lines = GepLayoutClassifier.MergeWrappedHeadings(pages[p].Lines);
            var nextPageLines = p + 1 < pages.Count ? pages[p + 1].Lines : [];
            state.BeginPage(pages[p]);
            for (var i = 0; i < lines.Count; i++)
            {
                state.Handle(lines, i, nextPageLines);
            }
            state.EndPage();
        }
        state.CloseAll();
        // Segments are stored when closed; sort back into the order they were opened.
        return state.Segments.OrderBy(s => s.Sequence).ToList();
    }

    /// <summary>
    /// "A RISING CHALLENGE Sovereign Debt Levels and Interest Rates in EMDEs"
    ///   → "A Rising Challenge: Sovereign Debt Levels and Interest Rates in EMDEs".
    /// Chapter covers print the main title in capitals and the subtitle in normal case.
    /// </summary>
    public static string FormatTitle(string raw)
    {
        var words = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var capsCount = words.TakeWhile(w => w.Any(char.IsLetter) && !w.Any(char.IsLower)).Count();
        var head = string.Join(' ', words.Take(capsCount).Select((w, i) => TitleCaseWord(w, i == 0)));
        var tail = string.Join(' ', words.Skip(capsCount));
        if (head.Length == 0) return tail;
        return tail.Length == 0 ? head : $"{head}: {tail}";
    }

    private static readonly HashSet<string> SmallWords = ["a", "an", "and", "of", "in", "the", "for", "on", "to", "or"];

    private static string TitleCaseWord(string word, bool isFirst)
    {
        var lower = word.ToLower(CultureInfo.InvariantCulture);
        if (!isFirst && SmallWords.Contains(lower)) return lower;
        return char.ToUpper(lower[0], CultureInfo.InvariantCulture) + lower[1..];
    }

    [GeneratedRegex(@"^CHAPTER\s+(\d+)$")]
    private static partial Regex ChapterNumberPattern();

    [GeneratedRegex(@"^Note:\s.*prepared", RegexOptions.IgnoreCase)]
    private static partial Regex AuthorCreditPattern();

    [GeneratedRegex(@"^(\d+\s|Note:)")]
    private static partial Regex FootnoteStartPattern();

    /// <summary>The mutable "you are here" state. Kept private: callers only see the segments.</summary>
    private sealed class WalkState(ChunkingOptions options)
    {
        public List<Segment> Segments { get; } = [];

        private ReportPart _part = ReportPart.BeforeContent;
        private int? _chapterNumber;
        private string _chapterTitle = "Front matter";
        private int _chapterLabelPage = -1;
        private string? _section;
        private string? _subsection;
        private string? _region;
        private bool _skipping = true; // nothing before the first front-matter title is indexed

        private string? _box;
        private string? _boxSubsection;
        private bool _skipNote;
        private int _sequence;

        private ExtractedPage _page = null!;
        private Segment? _prose, _boxSegment, _note, _figure, _table;

        public void BeginPage(ExtractedPage page)
        {
            _page = page;
            _skipNote = false;

            // The running header is a second, independent source of truth. If a chapter cover was
            // missed (or the PDF is imperfect), the header still tells us which chapter we're in.
            var label = page.RunningHeaderLabel;
            if (label is null) return;

            var chapter = ChapterNumberPattern().Match(label);
            if (chapter.Success && (_part != ReportPart.Chapter || _chapterNumber != int.Parse(chapter.Groups[1].Value)))
            {
                StartChapter(int.Parse(chapter.Groups[1].Value));
            }
            else if (label == "STATISTICAL APPENDIX" && _part != ReportPart.StatisticalAppendix)
            {
                StartPart(ReportPart.StatisticalAppendix, "Statistical Appendix");
            }
            else if (label == "SELECTED TOPICS" && _part != ReportPart.SelectedTopics)
            {
                StartPart(ReportPart.SelectedTopics, "Selected Topics");
            }
        }

        public void EndPage() => Close(ref _figure); // figures never continue onto the next page

        public void Handle(IReadOnlyList<TextLine> lines, int index, IReadOnlyList<TextLine> nextPageLines)
        {
            var line = lines[index];
            switch (line.Role)
            {
                case LineRole.FrontMatterTitle:
                    CloseAll();
                    var isSummary = line.Text.Equals("Executive Summary", StringComparison.OrdinalIgnoreCase);
                    ResetLocation(isSummary ? ReportPart.ExecutiveSummary : ReportPart.FrontMatter, null, line.Text);
                    _skipping = options.SkippedFrontMatterTitles.Contains(line.Text, StringComparer.OrdinalIgnoreCase);
                    break;

                case LineRole.ChapterLabel:
                    StartChapter(int.Parse(ChapterNumberPattern().Match(line.Text).Groups[1].Value));
                    _chapterLabelPage = _page.PdfPageNumber;
                    break;

                case LineRole.ChapterTitle:
                    if (_part == ReportPart.Chapter && _chapterLabelPage == _page.PdfPageNumber)
                    {
                        _chapterTitle = FormatTitle(line.Text);
                    }
                    else if (line.Text.Contains("APPENDIX", StringComparison.OrdinalIgnoreCase))
                    {
                        StartPart(ReportPart.StatisticalAppendix, FormatTitle(line.Text));
                    }
                    break;

                case LineRole.SectionHeading:
                case LineRole.MinorSectionHeading:
                    StartSection(GepLayoutClassifier.StripContinued(line.Text));
                    break;

                case LineRole.SubsectionHeading:
                    if (_box is not null && NextContentIsBox(lines, index, nextPageLines))
                    {
                        Close(ref _boxSegment);
                        _boxSubsection = line.Text;
                    }
                    else
                    {
                        CloseProse();
                        _subsection = line.Text;
                    }
                    break;

                case LineRole.BoxTitle:
                    var boxTitle = GepLayoutClassifier.StripContinued(line.Text);
                    if (boxTitle != _box)
                    {
                        Close(ref _boxSegment);
                        _box = boxTitle;
                        _boxSubsection = null;
                    }
                    break;

                case LineRole.FigureTitle:
                    Close(ref _figure);
                    if (!_skipping) _figure = Open(SegmentStream.Figure, line.Text);
                    break;

                case LineRole.TableTitle:
                    Close(ref _table);
                    if (!_skipping) _table = Open(SegmentStream.Table, line.Text);
                    break;

                case LineRole.Body:
                    Append(ref _prose, SegmentStream.Prose, line);
                    break;

                case LineRole.BoxBody:
                    if (_box is null) Append(ref _prose, SegmentStream.Prose, line);
                    else Append(ref _boxSegment, SegmentStream.Box, line);
                    break;

                case LineRole.Note:
                    HandleNote(line);
                    break;

                case LineRole.FigureText:
                    // Keep only the figure's descriptive sentence (oblique font), not axis labels or sources.
                    if (_figure is not null && line.FontName.Contains("Obl", StringComparison.OrdinalIgnoreCase))
                    {
                        _figure.Lines.Add(ToSource(line));
                    }
                    break;

                case LineRole.TableText:
                    // Appendix tables have no "TABLE x.y" title; the 16pt section heading is their title.
                    if (_table is null && !_skipping && _part == ReportPart.StatisticalAppendix && _section is not null)
                    {
                        _table = Open(SegmentStream.Table, _section);
                    }
                    if (_table is not null) _table.Lines.Add(ToSource(line));
                    break;

                // Running headers, footers, cover text, bullets and formula fragments are not content.
            }
        }

        private void HandleNote(TextLine line)
        {
            if (line.Text.Length <= 3) return; // a lone footnote marker such as "1"

            // "Note: This chapter was prepared by <names>" is an author credit, not report content.
            if (AuthorCreditPattern().IsMatch(line.Text))
            {
                _skipNote = true;
                return;
            }
            if (_skipNote)
            {
                if (!FootnoteStartPattern().IsMatch(line.Text)) return;
                _skipNote = false;
            }
            Append(ref _note, SegmentStream.Note, line);
        }

        /// <summary>
        /// An 11pt subheading belongs to a box when the next text after it is box body text
        /// (boxes use 10.6pt text). Looks at the rest of this page, then the next page.
        /// </summary>
        private static bool NextContentIsBox(IReadOnlyList<TextLine> lines, int index, IReadOnlyList<TextLine> nextPageLines)
        {
            var next = lines.Skip(index + 1).Concat(nextPageLines)
                .FirstOrDefault(l => l.Role is LineRole.Body or LineRole.BoxBody);
            return next?.Role == LineRole.BoxBody;
        }

        private void StartChapter(int number)
        {
            CloseAll();
            ResetLocation(ReportPart.Chapter, number, $"Chapter {number}");
            _skipping = false;
        }

        private void StartPart(ReportPart part, string title)
        {
            CloseAll();
            ResetLocation(part, null, title);
            _skipping = false;
        }

        private void StartSection(string title)
        {
            if (title == _section) return; // "Real GDP growth (continued)" on the next page

            CloseProse();
            Close(ref _table);
            _section = title;
            _subsection = null;
            _region = _part == ReportPart.Chapter
                      && _chapterNumber == options.RegionalChapterNumber
                      && options.RegionNames.Contains(title, StringComparer.Ordinal)
                ? title
                : null;
        }

        private void ResetLocation(ReportPart part, int? chapterNumber, string chapterTitle)
        {
            _part = part;
            _chapterNumber = chapterNumber;
            _chapterTitle = chapterTitle;
            _section = _subsection = _region = _box = _boxSubsection = null;
        }

        private void Append(ref Segment? segment, SegmentStream stream, TextLine line)
        {
            if (_skipping) return;
            segment ??= Open(stream, title: null);
            segment.Lines.Add(ToSource(line));
        }

        private Segment Open(SegmentStream stream, string? title)
        {
            var isBoxFigure = stream == SegmentStream.Figure && _box is not null
                              && title?.StartsWith("FIGURE B", StringComparison.Ordinal) == true;
            var inBox = stream == SegmentStream.Box || isBoxFigure;

            var context = new SectionContext(
                _part,
                _chapterNumber,
                _chapterTitle,
                SectionTitle: (inBox ? _box : _section) ?? _chapterTitle,
                SubsectionTitle: stream is SegmentStream.Figure or SegmentStream.Table
                    ? title
                    : inBox ? _boxSubsection : _subsection,
                Region: _region);

            return new Segment
            {
                Sequence = _sequence++,
                Context = context,
                Stream = stream,
                ContentType = DecideContentType(context, stream),
                Title = title,
                StartLocation = new SourceLine(title ?? string.Empty, _page.PdfPageNumber, _page.PrintedPageLabel),
            };
        }

        private static ContentType DecideContentType(SectionContext context, SegmentStream stream)
        {
            switch (stream)
            {
                case SegmentStream.Box:
                    return ContentType.BoxArticle;
                case SegmentStream.Figure:
                    return ContentType.FigureCaption;
                case SegmentStream.Table:
                    return context.Part == ReportPart.StatisticalAppendix ? ContentType.StatisticalAppendix : ContentType.Table;
            }

            return context.Part switch
            {
                ReportPart.ExecutiveSummary => ContentType.ExecutiveSummary,
                ReportPart.FrontMatter => ContentType.FrontMatter,
                ReportPart.StatisticalAppendix => ContentType.StatisticalAppendix,
                ReportPart.SelectedTopics => ContentType.References,
                _ when context.SectionTitle == "References" => ContentType.References,
                _ when context.SectionTitle.StartsWith("ANNEX", StringComparison.Ordinal) => ContentType.Annex,
                _ when context.Region is not null => ContentType.RegionalOutlook,
                _ => ContentType.Narrative,
            };
        }

        private SourceLine ToSource(TextLine line) => new(line.Text, _page.PdfPageNumber, _page.PrintedPageLabel);

        private void CloseProse()
        {
            Close(ref _prose);
            Close(ref _note);
        }

        public void CloseAll()
        {
            CloseProse();
            Close(ref _boxSegment);
            Close(ref _figure);
            Close(ref _table);
        }

        private void Close(ref Segment? segment)
        {
            if (segment is not null && (segment.Lines.Count > 0 || segment.Title is not null))
            {
                Segments.Add(segment);
            }
            segment = null;
        }
    }
}
