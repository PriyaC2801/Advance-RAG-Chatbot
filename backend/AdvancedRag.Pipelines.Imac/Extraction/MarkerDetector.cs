using System.Text.RegularExpressions;
using AdvancedRag.Pipelines.Imac;
using UglyToad.PdfPig.Content;

namespace AdvancedRag.Pipelines.Imac.Extraction;

// Finds footnote markers (superscript numbers). Evidence from Phase 1 (see LEARNING.md):
//  - markers are drawn in the "Regular" font while body text is Light/Semibold;
//  - they are 1-19, much smaller than the word they follow (about 0.67x) and sit RAISED just after it;
//  - CO2 subscripts sit LOWER than the word before them, so "raised" separates the two;
//  - table numbers (6.5pt) are small too, so size alone is not enough.
public static class MarkerDetector
{
    static readonly Regex Digits = new(@"^\d{1,2}$");

    public static List<Marker> Detect(IReadOnlyList<Word> words)
    {
        var result = new List<Marker>();
        foreach (var w in words)
        {
            if (!Digits.IsMatch(w.Text)) continue;
            int n = int.Parse(w.Text);
            if (n < 1 || n > 19) continue;
            if (w.BoundingBox.Bottom < 42) continue;                       // footer page numbers

            bool regularFont = WordUtil.Font(w).Contains("Regular", StringComparison.OrdinalIgnoreCase);
            double size = WordUtil.Size(w);
            var prev = FindPrev(w, words);

            if (prev == null)
            {
                // Lone marker (e.g. the 6 next to the EPEAT logo): no word to attach to, so require the marker look strictly.
                if (regularFont && size <= 5.8) result.Add(new Marker(w, n, null));
                continue;
            }
            bool smaller = size <= WordUtil.Size(prev) * 0.75 || size <= 5.8;
            bool raised = WordUtil.Base(w) > WordUtil.Base(prev) + 0.3;
            if ((regularFont || size <= 5.8) && smaller && raised) result.Add(new Marker(w, n, prev));
        }
        return result;
    }

    // The word whose right edge touches the marker's left edge, on (roughly) the same line.
    static Word? FindPrev(Word m, IReadOnlyList<Word> words)
    {
        return words.Where(p => p != m
                && p.BoundingBox.Right <= m.BoundingBox.Left + 1.0
                && m.BoundingBox.Left - p.BoundingBox.Right <= 4.0
                && WordUtil.Base(m) >= WordUtil.Base(p) - 2
                && WordUtil.Base(m) <= WordUtil.Base(p) + WordUtil.Size(p))
            .OrderByDescending(p => p.BoundingBox.Right).FirstOrDefault();
    }

    public static Dictionary<Word, List<int>> AfterMap(IEnumerable<Marker> markers)
    {
        var map = new Dictionary<Word, List<int>>();
        foreach (var m in markers.Where(m => m.Prev != null))
        {
            if (!map.TryGetValue(m.Prev!, out var l)) map[m.Prev!] = l = [];
            l.Add(m.Number);
        }
        return map;
    }
}
