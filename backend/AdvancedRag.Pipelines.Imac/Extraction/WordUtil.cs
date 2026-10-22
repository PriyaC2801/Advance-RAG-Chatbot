using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig.Content;

namespace AdvancedRag.Pipelines.Imac.Extraction;

// Small helpers shared by the table, prose and footnote code.
public static class WordUtil
{
    public static double Base(Word w) => w.Letters[0].StartBaseLine.Y;   // text baseline
    public static double Size(Word w) => w.Letters[0].PointSize;
    public static string Font(Word w) => w.Letters[0].FontName ?? "";

    // Joins words of ONE line left-to-right. Footnote markers become "[^n]" glued to the word they follow.
    // A gap under 1pt means "no space" - this rebuilds "CO2e" from the pieces "CO", "2", "e".
    public static string JoinLine(IEnumerable<Word> words, IReadOnlyDictionary<Word, List<int>> markerAfter)
    {
        var sb = new StringBuilder();
        Word? prev = null;
        foreach (var w in words.OrderBy(w => w.BoundingBox.Left))
        {
            if (prev != null && w.BoundingBox.Left - prev.BoundingBox.Right >= 1.0) sb.Append(' ');
            sb.Append(w.Text);
            if (markerAfter.TryGetValue(w, out var nums)) foreach (var n in nums) sb.Append($"[^{n}]");
            prev = w;
        }
        return sb.ToString();
    }

    // Groups words into lines (baselines within `tol` points), top line first.
    public static List<List<Word>> Lines(IEnumerable<Word> words, double tol = 2.0)
    {
        var lines = new List<List<Word>>();
        foreach (var w in words.OrderByDescending(Base))
        {
            var line = lines.FirstOrDefault(l => Math.Abs(Base(l[0]) - Base(w)) <= tol);
            if (line == null) lines.Add([w]); else line.Add(w);
        }
        return lines;
    }

    // Lines joined top-to-bottom; a line ending in "-" is glued to the next ("low-" + "carbon").
    public static string JoinLines(IEnumerable<IEnumerable<Word>> lines, IReadOnlyDictionary<Word, List<int>> markerAfter)
    {
        var sb = new StringBuilder();
        foreach (var l in lines)
        {
            var t = JoinLine(l, markerAfter);
            if (sb.Length > 0 && sb[^1] != '-') sb.Append(' ');
            sb.Append(t);
        }
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }
}
