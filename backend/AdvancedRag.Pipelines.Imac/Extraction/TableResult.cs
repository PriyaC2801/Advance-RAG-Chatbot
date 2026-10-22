using UglyToad.PdfPig.Content;

namespace AdvancedRag.Pipelines.Imac.Extraction;

public class TableResult
{
    public int Page { get; set; }
    public string Title { get; set; } = "";
    public List<string> Columns { get; set; } = [];
    public List<string> Rows { get; set; } = [];

    /// <summary>Every word the table consumed, so prose extraction skips them.</summary>
    public HashSet<Word> Words { get; set; } = [];

    public string Text => $"Table: {Title} (page {Page})\nColumns: {string.Join(" | ", Columns)}\n{string.Join("\n", Rows)}";
}
