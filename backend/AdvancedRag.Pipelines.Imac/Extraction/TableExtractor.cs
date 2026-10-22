using System.Text;
using AdvancedRag.Pipelines.Imac;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;

namespace AdvancedRag.Pipelines.Imac.Extraction;

// Rebuilds tables from geometry, because generic table detection returns broken fragments on this PDF.
// Evidence (Phase 1): every table has a header made of dark-grey (0.235) bars; a header is SEVERAL touching
// bars, 57-59% of page width together; the bottom bars give the columns; rows are ~17pt apart.
public static class TableExtractor
{
    const double HeaderMinWidthFraction = 0.35;
    const double MaxRowGap = 16.5;    // gap between one row bottom and the next row top: rows <=15.2pt, next paragraph or note >=17
    const double RowTolerance = 2.0;  // baselines within 2pt = same row

    public static List<TableResult> Extract(Page page, IReadOnlyList<Word> words, IReadOnlyList<Marker> markers)
    {
        var markerWords = markers.Select(m => m.Word).ToHashSet();
        var markerAfter = MarkerDetector.AfterMap(markers);
        var plain = words.Where(w => !markerWords.Contains(w)).ToList();

        var bars = page.Paths.Where(p => p.IsFilled && IsDark(p))
            .Select(p => p.GetBoundingRectangle()).Where(r => r != null).Select(r => r!.Value)
            .Where(r => r.Height < 45 && r.Width > 8 && r.Width < page.Width * 0.9).ToList();

        // Merge touching bars into one header group (union-find via repeated merging).
        var groups = new List<List<PdfRectangle>>();
        foreach (var bar in bars)
        {
            var touching = groups.Where(g => g.Any(o => Touches(o, bar))).ToList();
            var merged = new List<PdfRectangle> { bar };
            foreach (var g in touching) { merged.AddRange(g); groups.Remove(g); }
            groups.Add(merged);
        }

        var headers = groups
            .Select(g => (Bars: g, Left: g.Min(b => b.Left), Right: g.Max(b => b.Right), Bottom: g.Min(b => b.Bottom), Top: g.Max(b => b.Top)))
            .Where(g => g.Right - g.Left > page.Width * HeaderMinWidthFraction)
            .OrderByDescending(g => g.Top).ToList();

        var tables = new List<TableResult>();
        for (int hi = 0; hi < headers.Count; hi++)
        {
            var h = headers[hi];
            var t = new TableResult { Page = page.Number };

            // Columns = the bars along the bottom edge of the header (the title bar, if any, sits above them).
            var colBars = h.Bars.Where(b => b.Bottom <= h.Bottom + 1.5).OrderBy(b => b.Left).ToList();
            bool InCol(Word w, PdfRectangle b) => b.Left <= w.BoundingBox.Centroid.X && w.BoundingBox.Centroid.X <= b.Right
                                               && b.Bottom <= w.BoundingBox.Centroid.Y && w.BoundingBox.Centroid.Y <= b.Top;
            foreach (var b in colBars)
            {
                var hw = plain.Where(w => InCol(w, b)).ToList();
                t.Columns.Add(WordUtil.JoinLines(WordUtil.Lines(hw), markerAfter));
                foreach (var w in hw) t.Words.Add(w);
            }
            // Words inside the header block but outside every column bar = the table title.
            var titleWords = plain.Where(w => w.BoundingBox.Centroid.X >= h.Left && w.BoundingBox.Centroid.X <= h.Right
                                           && w.BoundingBox.Centroid.Y >= h.Bottom && w.BoundingBox.Centroid.Y <= h.Top
                                           && !colBars.Any(b => InCol(w, b))).ToList();
            foreach (var w in titleWords) t.Words.Add(w);
            t.Title = titleWords.Count > 0 ? WordUtil.JoinLines(WordUtil.Lines(titleWords), markerAfter) : t.Columns[0];

            // Body = lines under the header, inside its x-range, until a big vertical gap or the next header.
            double stopY = hi + 1 < headers.Count ? headers[hi + 1].Top : double.NegativeInfinity;
            var body = plain.Where(w => w.BoundingBox.Centroid.X >= h.Left - 8 && w.BoundingBox.Centroid.X <= h.Right + 8
                                     && w.BoundingBox.Top <= h.Bottom + 0.5 && w.BoundingBox.Bottom >= stopY).ToList();
            double prevBottom = h.Bottom;
            foreach (var line in WordUtil.Lines(body, RowTolerance))
            {
                double lineTop = line.Max(w => w.BoundingBox.Top);
                if (prevBottom - lineTop > MaxRowGap) break;
                prevBottom = line.Min(w => w.BoundingBox.Bottom);

                // Each word goes to the column whose x-range contains its centre (else the nearest).
                var cells = colBars.Select(_ => new List<Word>()).ToList();
                foreach (var w in line)
                {
                    double cx = w.BoundingBox.Centroid.X;
                    int ci = colBars.FindIndex(b => b.Left <= cx && cx <= b.Right);
                    if (ci < 0) ci = colBars.Select((b, i) => (i, d: Math.Min(Math.Abs(cx - b.Left), Math.Abs(cx - b.Right)))).MinBy(x => x.d).i;
                    cells[ci].Add(w); t.Words.Add(w);
                }
                var texts = cells.Select(c => WordUtil.JoinLines([c], markerAfter)).ToList();
                var sb = new StringBuilder(texts[0].TrimStart('•', ' '));
                var values = texts.Skip(1).Select((v, i) => (v, i)).Where(x => x.v.Length > 0)
                                  .Select(x => $"{t.Columns[x.i + 1]}: {x.v}");
                if (values.Any()) sb.Append(" — ").Append(string.Join("; ", values));
                t.Rows.Add(sb.ToString());
            }
            // Markers attached to any word the table consumed belong to the table too.
            foreach (var m in markers.Where(m => m.Prev != null && t.Words.Contains(m.Prev))) t.Words.Add(m.Word);
            tables.Add(t);
        }
        return tables;
    }

    static bool IsDark(UglyToad.PdfPig.Graphics.PdfPath p)
    {
        var (r, g, b) = p.FillColor!.ToRGBValues();
        return r < 0.3 && g < 0.3 && b < 0.3;
    }

    static bool Touches(PdfRectangle a, PdfRectangle b, double tol = 1.5) =>
        a.Left <= b.Right + tol && b.Left <= a.Right + tol && a.Bottom <= b.Top + tol && b.Bottom <= a.Top + tol;
}
