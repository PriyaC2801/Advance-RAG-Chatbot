using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;

namespace AdvancedRag.Pipelines.Imac.Extraction;

/// <summary>One paragraph / call-out block found by page segmentation, text still containing "[^n]" markers.</summary>
public record ProseBlock(int Page, string Raw, PdfRectangle Box, List<Word> Words);
