using UglyToad.PdfPig;
using AdvancedRag.Pipelines.Imac;

namespace AdvancedRag.Pipelines.Imac.Extraction;

// One extracted piece of the document. Raw keeps "[^n]" markers; Content is what the LLM will read.
public static class ElementBuilder
{
    public record Result(List<ExtractedElement> Elements, Dictionary<int, string> Notes);

    public static Result Build(string pdfPath)
    {
        var raw = new List<(string Kind, int Page, string? Section, string Text)>();
        var notes = new Dictionary<int, string>();

        using var doc = PdfDocument.Open(pdfPath);
        foreach (var page in doc.GetPages())
        {
            var words = page.GetWords().ToList();
            var section = ProseExtractor.SectionTitle(words);

            if (section == "Endnotes")
            {
                // The notes themselves are not chunks; they get attached to the chunks that cite them.
                // (Tables on these pages - the power table on p12 - are still extracted below.)
                var tables0 = TableExtractor.Extract(page, words, []);
                var rest = words.Where(w => !tables0.Any(t => t.Words.Contains(w))).ToList();
                foreach (var kv in FootnoteLinker.ParseEndnotes(rest)) notes[kv.Key] = kv.Value;
                foreach (var t in tables0) raw.Add(("table", page.Number, section, t.Text));
                continue;
            }

            var markers = MarkerDetector.Detect(words);
            var tables = TableExtractor.Extract(page, words, markers);
            var consumed = tables.SelectMany(t => t.Words).ToHashSet();
            foreach (var t in tables) raw.Add(("table", page.Number, section, t.Text));
            // Markers that belong to a table must not be re-attached to prose blocks.
            var proseMarkers = markers.Where(m => !consumed.Contains(m.Word)).ToList();
            foreach (var b in ProseExtractor.Extract(page.Number, words, proseMarkers, consumed))
                raw.Add(("text", page.Number, section, b.Raw));
        }

        // Link after the loop: endnotes sit at the END of the document but are cited from page 1 onwards.
        var elements = raw.Select(r =>
        {
            var (content, fn) = FootnoteLinker.Link(r.Text, notes);
            return new ExtractedElement(r.Kind, r.Page, r.Section, r.Text, content, fn);
        }).ToList();
        return new Result(elements, notes);
    }
}
