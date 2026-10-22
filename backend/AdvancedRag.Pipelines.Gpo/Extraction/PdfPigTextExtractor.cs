using System.Text;
using System.Text.RegularExpressions;

using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.ReadingOrderDetector;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace AdvancedRag.Pipelines.Gpo;

/// <summary>
/// Extracts text page by page in layout order: words are grouped into blocks (so a two-column page
/// yields whole columns rather than interleaved lines), blocks are put in reading order, ligatures
/// and line-end hyphenation are repaired, and blocks repeated on many pages (running headers,
/// footers, page numbers) are dropped.
/// </summary>
internal sealed partial class PdfPigTextExtractor
{
    // A short block that recurs on at least this share of pages is boilerplate, not content.
    private const double RepeatedBlockPageShare = 0.3;
    private const int RepeatedBlockMinPages = 3;
    private const int RepeatedBlockMaxWords = 12;

    [GeneratedRegex(@"^\W*\d{1,4}\W*$")]
    private static partial Regex BarePageNumber();

    [GeneratedRegex(@"\d+")]
    private static partial Regex Digits();

    public IReadOnlyList<ExtractedPage> ExtractPages(string filePath, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        using var document = PdfDocument.Open(filePath);

        var pageBlocks = new List<(int PageNumber, List<string> Blocks)>(document.NumberOfPages);
        foreach (var page in document.GetPages())
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"Reading page {page.Number} of {document.NumberOfPages}");
            pageBlocks.Add((page.Number, ExtractBlocks(page)));
        }

        var boilerplate = FindRepeatedBlocks(pageBlocks);

        return pageBlocks
            .Select(p => new ExtractedPage(
                p.PageNumber,
                string.Join("\n\n", p.Blocks.Where(b => !IsBoilerplate(b, boilerplate)))))
            .ToList();
    }

    private static List<string> ExtractBlocks(Page page)
    {
        try
        {
            var words = page.GetWords().ToList();
            if (words.Count > 0)
            {
                var blocks = DocstrumBoundingBoxes.Instance.GetBlocks(words);
                var ordered = UnsupervisedReadingOrderDetector.Instance.Get(blocks)
                    .OrderBy(b => b.ReadingOrder);

                var texts = ordered
                    .Select(b => Repair(JoinLines(b.TextLines.Select(l => l.Text))))
                    .Where(t => t.Length > 0)
                    .ToList();

                if (texts.Count > 0)
                {
                    return texts;
                }
            }
        }
        catch (Exception)
        {
            // Layout analysis can choke on unusual pages; fall back to plain content order below.
        }

        var fallback = Repair(ContentOrderTextExtractor.GetText(page));
        return fallback.Length > 0 ? [fallback] : [];
    }

    /// <summary>Joins lines with spaces, merging a word split by a line-end hyphen ("govern-" + "ment").</summary>
    private static string JoinLines(IEnumerable<string> lines)
    {
        var result = new StringBuilder();

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (result.Length == 0)
            {
                result.Append(line);
            }
            else if (result[^1] == '-' && result.Length > 1 && char.IsLetter(result[^2]) && char.IsLower(line[0]))
            {
                result.Length--; // drop the hyphen and glue the word halves together
                result.Append(line);
            }
            else
            {
                result.Append(' ').Append(line);
            }
        }

        return result.ToString();
    }

    /// <summary>Folds ligatures (ﬁ, ﬂ ...) and other compatibility characters to plain text; removes soft hyphens.</summary>
    private static string Repair(string text) =>
        text.Normalize(NormalizationForm.FormKC).Replace("­", "").Trim();

    private static HashSet<string> FindRepeatedBlocks(List<(int PageNumber, List<string> Blocks)> pageBlocks)
    {
        var threshold = Math.Max(RepeatedBlockMinPages, (int)Math.Ceiling(pageBlocks.Count * RepeatedBlockPageShare));

        return pageBlocks
            .SelectMany(p => p.Blocks
                .Where(b => b.Split(' ').Length <= RepeatedBlockMaxWords)
                .Select(Fingerprint)
                .Distinct())
            .GroupBy(f => f)
            .Where(g => g.Count() >= threshold)
            .Select(g => g.Key)
            .ToHashSet();
    }

    private static bool IsBoilerplate(string block, HashSet<string> repeated) =>
        BarePageNumber().IsMatch(block) || repeated.Contains(Fingerprint(block));

    // Digits are masked so "Annual Report 2018 | 21" and "... | 22" count as the same running header.
    private static string Fingerprint(string block) => Digits().Replace(block.ToLowerInvariant(), "#");
}
