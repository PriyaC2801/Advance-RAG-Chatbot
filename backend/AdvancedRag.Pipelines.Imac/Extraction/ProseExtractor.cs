using UglyToad.PdfPig.Content;
using AdvancedRag.Pipelines.Imac;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;

namespace AdvancedRag.Pipelines.Imac.Extraction;

public static class ProseExtractor
{
    public const double TitleMinSize = 20;   // every page has exactly one 24pt title (Phase 1)

    // The page's section title: the 24pt words, top line first.
    public static string? SectionTitle(IReadOnlyList<Word> words)
    {
        var t = words.Where(w => WordUtil.Size(w) >= TitleMinSize).ToList();
        return t.Count == 0 ? null : WordUtil.JoinLines(WordUtil.Lines(t), new Dictionary<Word, List<int>>());
    }

    public static bool IsFooterWord(Word w) => w.BoundingBox.Bottom < 42;   // "iMac | Product Environmental Report N" at y~37.7

    // Prose = everything that is not title, footer or table. Docstrum groups words into blocks, which keeps
    // side-by-side call-outs and columns apart instead of merging them line by line.
    public static List<ProseBlock> Extract(int pageNumber, IReadOnlyList<Word> words, IReadOnlyList<Marker> markers, ISet<Word> consumedByTables)
    {
        var markerWords = markers.Select(m => m.Word).ToHashSet();
        var markerAfter = MarkerDetector.AfterMap(markers);
        var usable = words.Where(w => !markerWords.Contains(w) && !consumedByTables.Contains(w)
                                      && !IsFooterWord(w) && WordUtil.Size(w) < TitleMinSize).ToList();
        if (usable.Count == 0) return [];

        var blocks = new List<ProseBlock>();
        foreach (var b in DocstrumBoundingBoxes.Instance.GetBlocks(usable))
        {
            var bw = b.TextLines.SelectMany(l => l.Words).ToList();
            string raw = WordUtil.JoinLines(b.TextLines.Select(l => (IEnumerable<Word>)l.Words), markerAfter);
            if (raw.Length == 0 || raw.StartsWith("©") || raw.StartsWith("iMac | Product Environmental Report")) continue;
            blocks.Add(new ProseBlock(pageNumber, raw, b.BoundingBox, bw));
        }

        // Reading order: top to bottom, then left to right (blocks within ~10pt of each other count as one row).
        blocks = blocks.OrderByDescending(b => Math.Round(b.Box.Top / 10)).ThenBy(b => b.Box.Left).ToList();

        // Markers with no word to attach to (the lone "6" by the EPEAT logo) go to the nearest block.
        foreach (var m in markers.Where(m => m.Prev == null && blocks.Count > 0))
        {
            var c = m.Word.BoundingBox.Centroid;
            var best = blocks.Select((b, i) => (b, i, d: Dist(b.Box, c.X, c.Y))).MinBy(x => x.d);
            blocks[best.i] = best.b with { Raw = best.b.Raw + $"[^{m.Number}]" };
        }
        return MergeStats(blocks);
    }

    // Icon call-outs are drawn as a big number with its label underneath ("76%" / "recycled content in fiber packaging").
    // Docstrum makes them two blocks; a lone "76%" is a useless chunk, so glue it to the block directly below it.
    static readonly System.Text.RegularExpressions.Regex Stat = new(@"^[–-]?[\d.,]+\s?(%|kg|W|GB)?$");
    static List<ProseBlock> MergeStats(List<ProseBlock> blocks)
    {
        var result = new List<ProseBlock>(blocks);
        foreach (var stat in blocks.Where(b => b.Raw.Length <= 10 && Stat.IsMatch(b.Raw)))
        {
            var below = result.Where(o => o != stat && o.Box.Top <= stat.Box.Bottom + 3 && stat.Box.Bottom - o.Box.Top < 40
                                          && o.Box.Left < stat.Box.Right + 10 && o.Box.Right > stat.Box.Left - 10)
                              .OrderByDescending(o => o.Box.Top).FirstOrDefault();
            if (below == null) continue;
            int i = result.IndexOf(below);
            result[i] = below with { Raw = stat.Raw + " " + below.Raw };
            result.Remove(stat);
        }
        return result;
    }

    static double Dist(PdfRectangle r, double x, double y)
    {
        double dx = Math.Max(Math.Max(r.Left - x, 0), x - r.Right);
        double dy = Math.Max(Math.Max(r.Bottom - y, 0), y - r.Top);
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
