using System.Text.RegularExpressions;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;

namespace AdvancedRag.Pipelines.Imac.Extraction;

// Footnotes are the document's fine print: "58% less energy" is only half a fact without note 18's definition.
// We keep the caveat next to the claim by appending "[Note n] ..." to every chunk that has a marker.
public static class FootnoteLinker
{
    static readonly Regex MarkerRx = new(@"\[\^(\d+)\]");
    const int MaxNoteChars = 700;

    // raw text containing "[^n]" -> (readable text + attached notes, footnote numbers)
    public static (string Content, int[] Footnotes) Link(string raw, IReadOnlyDictionary<int, string> notes)
    {
        var nums = MarkerRx.Matches(raw).Select(m => int.Parse(m.Groups[1].Value)).Distinct().ToArray();
        var clean = Regex.Replace(MarkerRx.Replace(raw, ""), @"[ \t]+", " ").Trim();
        foreach (var n in nums)
            if (notes.TryGetValue(n, out var note))
                clean += $"\n[Note {n}] {(note.Length > MaxNoteChars ? note[..MaxNoteChars] + "…" : note)}";
        return (clean, nums);
    }

    // Endnote pages: each note number is its own tiny text block just left of the note text (Phase 1 evidence),
    // so numbers and text are never glued together. The text belongs to the label whose y-range it falls in.
    public static Dictionary<int, string> ParseEndnotes(IReadOnlyList<Word> words)
    {
        var usable = words.Where(w => !ProseExtractor.IsFooterWord(w) && WordUtil.Size(w) < ProseExtractor.TitleMinSize).ToList();
        var blocks = DocstrumBoundingBoxes.Instance.GetBlocks(usable).Select(b => (Box: b.BoundingBox, Text: b.Text.Replace("\n", " ").Trim())).ToList();
        var none = new Dictionary<Word, List<int>>();

        var labels = blocks.Where(b => Regex.IsMatch(b.Text, @"^\d{1,2}$") && b.Box.Right < 220).OrderByDescending(b => b.Box.Top).ToList();
        var notes = new Dictionary<int, string>();
        for (int i = 0; i < labels.Count; i++)
        {
            double top = labels[i].Box.Top + 3;
            double bottom = i + 1 < labels.Count ? labels[i + 1].Box.Top + 3 : double.NegativeInfinity;
            var parts = blocks.Where(b => b.Box.Left >= 214 && b.Box.Top <= top && b.Box.Top > bottom && !b.Text.StartsWith("©")
                                          && !Regex.IsMatch(b.Text, @"^\d{1,2}$"))
                              .OrderByDescending(b => b.Box.Top).Select(b => b.Text);
            notes[int.Parse(labels[i].Text)] = string.Join(" ", parts);
        }
        return notes;
    }
}
