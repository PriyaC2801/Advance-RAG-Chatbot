using System.Text.RegularExpressions;
using AdvancedRag.Pipelines.Gep.Models;

namespace AdvancedRag.Pipelines.Gep.Retrieval;

/// <summary>
/// Learns "country → region" from the report itself: every row of a region-tagged table chunk
/// ("India: 2023/24: 7.2; ...") names a country of that region. Nothing is hard-coded, so
/// "India and Bangladesh" can be linked to South Asia without a hand-made country list.
/// </summary>
public static partial class CountryRegionIndex
{
    private static readonly string[] NotCountries =
        ["EMDE", "GDP", "excluding", "Excluding", "consumption", "investment", "Exports", "Imports", "Net exports",
         "Memo", "countries", "exporters", "importers", "economies", "Pacific Island", "Central Europe", "Western Balkans",
         "Eastern Europe", "South Caucasus", "Central Asia", "Caribbean", "Central America", "South America", "FCS",
         "GCC", "Non-", "Hydrocarbon", "Industrial", "SSA", "SAR", "EAP", "ECA", "LAC", "MNA", "Low-income", "per capita"];

    public static IReadOnlyDictionary<string, string> Build(IEnumerable<DocumentChunk> chunks)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var tableChunks = chunks.Where(c => c.Region is not null
                                            && c.ContentType is ContentType.Table or ContentType.StatisticalAppendix).ToList();
        var regions = tableChunks.Select(c => c.Region!).Distinct().ToList();

        foreach (var chunk in tableChunks)
        {
            foreach (Match match in CountryRow().Matches(chunk.Text))
            {
                var name = match.Groups["name"].Value.Trim();
                if (name == chunk.Region || NotCountries.Any(n => name.Contains(n, StringComparison.Ordinal))) continue;

                // Fragments of a wrapped label are not countries: "Africa, Afghanistan, and" ends with a
                // connector, and "Africa" alone is part of another region's name ("Sub-Saharan Africa").
                if (WrappedFragment().IsMatch(name)) continue;
                if (regions.Any(r => r != chunk.Region && r.Contains(name, StringComparison.OrdinalIgnoreCase))) continue;

                // "Egypt, Arab Rep." → also findable as "Egypt"
                var shortName = name.Split(',')[0].Trim();
                map.TryAdd(name, chunk.Region!);
                if (shortName.Length >= 4) map.TryAdd(shortName, chunk.Region!);
            }
        }
        return map;
    }

    /// <summary>A table line produced by TableFormatter: "Name: 2023: 7.2; ..." or "Name: 2023/24: ...".</summary>
    [GeneratedRegex(@"^(?<name>[A-Z][^:\n]{1,40}):\s(\d{4}|\d{2}Q\d)", RegexOptions.Multiline)]
    private static partial Regex CountryRow();

    [GeneratedRegex(@"(\band|\bthe|\bof|,)$", RegexOptions.IgnoreCase)]
    private static partial Regex WrappedFragment();
}
