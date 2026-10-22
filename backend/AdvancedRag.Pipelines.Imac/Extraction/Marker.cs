using UglyToad.PdfPig.Content;

namespace AdvancedRag.Pipelines.Imac.Extraction;

/// <summary>A footnote marker (superscript number) and the word it follows (null for a lone marker, e.g. beside a logo).</summary>
public record Marker(Word Word, int Number, Word? Prev);
