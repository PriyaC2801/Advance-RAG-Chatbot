using System.Text;
using System.Text.RegularExpressions;

namespace AdvancedRag.Pipelines.Gep.Pdf;

/// <summary>Cleans raw PDF text so it is consistent for chunking, embedding and display.</summary>
public static partial class TextNormalizer
{
    public static string Normalize(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        // NFKC turns typographic ligatures into plain letters: "conﬂict" (U+FB02) → "conflict".
        // Without this, a search for "conflict" would not match the stored text exactly.
        var normalized = text.Normalize(NormalizationForm.FormKC);

        // Soft hyphens and non-breaking spaces from page layout carry no meaning.
        normalized = normalized.Replace("­", string.Empty).Replace(' ', ' ');

        return MultipleSpaces().Replace(normalized, " ").Trim();
    }

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex MultipleSpaces();
}
