using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace AdvancedRag.Pipelines.Gep.Retrieval;

/// <summary>Structure the question states explicitly. Used for filters and small ranking boosts.</summary>
public sealed record QueryAnalysis(
    string Question,
    int? ExplicitChapter,
    IReadOnlyList<string> Regions,
    IReadOnlyList<string> RegionsImpliedByCountries,
    IReadOnlyList<string> CountriesMentioned,
    bool AsksForExecutiveSummary,
    bool IsComparison,
    bool AsksForReferenceMaterial,
    IReadOnlyList<string> KeyTerms)
{
    /// <summary>Regions named directly plus regions of named countries.</summary>
    public IReadOnlyList<string> AllRegions => Regions.Concat(RegionsImpliedByCountries).Distinct().ToList();
}

/// <summary>
/// "Query understanding": reads what the question says about the report's structure.
/// It only looks for things stated explicitly ("Chapter 3", "South Asia", "Executive Summary");
/// it does not try to guess intent. Semantic matching is still done by the embeddings.
/// </summary>
public sealed partial class QueryAnalyzer(IOptions<GepOptions> options)
{
    private static readonly HashSet<string> StopWords =
    [
        "what", "does", "the", "report", "about", "say", "says", "according", "this", "that", "with", "from", "for",
        "and", "are", "how", "which", "between", "compare", "difference", "chapter", "section", "outlook", "describe",
        "described", "discussed", "there", "their", "into", "much", "will", "would", "were", "been", "have", "has",
        "main", "tell", "give", "show", "summary", "executive", "region", "regions",
    ];

    private static readonly Dictionary<string, int> NumberWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4,
    };

    /// <param name="countryRegions">Country → region, learned from the report's regional tables (may be empty).</param>
    public QueryAnalysis Analyze(string question, IReadOnlyDictionary<string, string> countryRegions)
    {
        var regions = FindRegions(question);
        var countries = countryRegions.Keys
            .Where(country => Regex.IsMatch(question, $@"\b{Regex.Escape(country)}\b", RegexOptions.IgnoreCase))
            .OrderBy(country => question.IndexOf(country, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var implied = countries.Select(c => countryRegions[c]).Distinct().Where(r => !regions.Contains(r)).ToList();

        return new QueryAnalysis(
            question,
            FindChapter(question),
            regions,
            implied,
            countries,
            AsksForExecutiveSummary: question.Contains("executive summary", StringComparison.OrdinalIgnoreCase),
            IsComparison: regions.Count >= 2,
            AsksForReferenceMaterial: ReferenceWords().IsMatch(question),
            KeyTerms: Words().Matches(question.ToLower(CultureInfo.InvariantCulture))
                .Select(m => m.Value)
                .Where(w => w.Length >= 4 && !StopWords.Contains(w))
                .Distinct()
                .ToList());
    }

    private static int? FindChapter(string question)
    {
        var match = ChapterPattern().Match(question);
        if (!match.Success) return null;
        var value = match.Groups[1].Value;
        return int.TryParse(value, out var n) ? n : NumberWords.GetValueOrDefault(value);
    }

    /// <summary>Region names and configured aliases, in the order they appear in the question.</summary>
    private List<string> FindRegions(string question)
    {
        var found = new List<(string Region, int Position)>();
        foreach (var region in options.Value.Chunking.RegionNames)
        {
            var names = new List<string> { region };
            if (options.Value.Retrieval.RegionAliases.TryGetValue(region, out var aliases)) names.AddRange(aliases);

            foreach (var name in names.OrderByDescending(n => n.Length))
            {
                // All-caps abbreviations (SAR, SSA) must match exactly; names match case-insensitively.
                var caseOptions = name.All(c => !char.IsLetter(c) || char.IsUpper(c)) ? RegexOptions.None : RegexOptions.IgnoreCase;
                var match = Regex.Match(question, $@"\b{Regex.Escape(name)}\b", caseOptions);
                if (!match.Success) continue;
                found.Add((region, match.Index));
                break;
            }
        }
        return found.OrderBy(f => f.Position).Select(f => f.Region).ToList();
    }

    [GeneratedRegex(@"\bchapter\s+(\d|one|two|three|four)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ChapterPattern();

    [GeneratedRegex(@"\b(references?|bibliography|sources cited|abbreviations?|foreword|figures?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ReferenceWords();

    [GeneratedRegex(@"[a-z][a-z\-]+")]
    private static partial Regex Words();
}
