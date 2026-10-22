using System.Globalization;
using System.Text.RegularExpressions;
using AdvancedRag.Pipelines.Gep.Models;

namespace AdvancedRag.Pipelines.Gep.Retrieval;

/// <summary>How well one chunk matches the question's words.</summary>
/// <param name="Bm25">BM25 score (higher = better; only comparable within one question).</param>
/// <param name="Coverage">Share of the question's term weight (IDF) found in the chunk, 0–1.</param>
/// <param name="MatchedTerms">The question terms found in the chunk.</param>
public sealed record KeywordMatch(double Bm25, double Coverage, IReadOnlyList<string> MatchedTerms);

/// <summary>
/// Exact-word search (BM25) over each chunk's heading path plus text. Embeddings blur exact tokens such as
/// "Figure 3.4", "SAR", "COVID-19" or a country name; this index finds them. Built once per loaded index.
/// </summary>
public sealed partial class KeywordIndex
{
    private const double K1 = 1.2;
    private const double B = 0.75;

    private static readonly HashSet<string> StopWords =
    [
        "a", "an", "the", "and", "or", "of", "in", "on", "to", "for", "by", "with", "at", "from", "as", "is", "are",
        "was", "were", "be", "been", "it", "its", "this", "that", "these", "those", "what", "which", "who", "how",
        "do", "does", "did", "can", "could", "will", "would", "should", "about", "than", "then", "there", "their",
        "report", "say", "says", "according", "tell", "me", "give", "show", "describe",
    ];

    private readonly Dictionary<string, Dictionary<string, int>> _termFrequencies = new(); // chunk id → term → count
    private readonly Dictionary<string, int> _lengths = new();
    private readonly Dictionary<string, double> _idf = new();
    private readonly double _averageLength;

    private KeywordIndex(IReadOnlyList<DocumentChunk> chunks)
    {
        var documentFrequency = new Dictionary<string, int>();
        foreach (var chunk in chunks)
        {
            var terms = Tokenize($"{chunk.HeadingPath} {chunk.Text}");
            var counts = new Dictionary<string, int>();
            foreach (var term in terms)
            {
                counts[term] = counts.GetValueOrDefault(term) + 1;
            }
            _termFrequencies[chunk.Id] = counts;
            _lengths[chunk.Id] = terms.Count;
            foreach (var term in counts.Keys)
            {
                documentFrequency[term] = documentFrequency.GetValueOrDefault(term) + 1;
            }
        }

        var n = chunks.Count;
        foreach (var (term, df) in documentFrequency)
        {
            _idf[term] = Math.Log(1 + (n - df + 0.5) / (df + 0.5));
        }
        _averageLength = n == 0 ? 1 : Math.Max(1, _lengths.Values.Average());
    }

    public static KeywordIndex Build(IEnumerable<DocumentChunk> chunks) => new(chunks.ToList());

    /// <summary>Scores the given chunks for the question. Chunks sharing no term with it are left out.</summary>
    public Dictionary<string, KeywordMatch> Match(string question, IEnumerable<DocumentChunk> candidates)
    {
        // Terms the report never uses carry no signal, so they neither match nor count towards coverage.
        var queryTerms = Tokenize(question).Distinct().Where(_idf.ContainsKey).ToList();
        var matches = new Dictionary<string, KeywordMatch>();
        if (queryTerms.Count == 0) return matches;

        var totalWeight = queryTerms.Sum(t => _idf[t]);
        foreach (var chunk in candidates)
        {
            var counts = _termFrequencies[chunk.Id];
            var length = _lengths[chunk.Id];
            double score = 0, matchedWeight = 0;
            var matched = new List<string>();

            foreach (var term in queryTerms)
            {
                if (!counts.TryGetValue(term, out var tf)) continue;
                var idf = _idf[term];
                score += idf * tf * (K1 + 1) / (tf + K1 * (1 - B + B * length / _averageLength));
                matchedWeight += idf;
                matched.Add(term);
            }

            if (matched.Count > 0)
            {
                matches[chunk.Id] = new KeywordMatch(score, matchedWeight / totalWeight, matched);
            }
        }
        return matches;
    }

    /// <summary>
    /// Lower-cased words and numbers; "1.1" and "b1.1.1" stay whole (figure and table numbers), hyphenated words
    /// also yield their parts, and a trailing plural "s" is dropped.
    /// </summary>
    public static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        foreach (Match m in Word().Matches(text.ToLower(CultureInfo.InvariantCulture)))
        {
            var word = m.Value;
            Add(tokens, word);
            if (word.Contains('-'))
            {
                foreach (var part in word.Split('-', StringSplitOptions.RemoveEmptyEntries)) Add(tokens, part);
            }
        }
        return tokens;
    }

    private static void Add(List<string> tokens, string word)
    {
        word = word.Replace("’", "'");
        if (word.EndsWith("'s", StringComparison.Ordinal)) word = word[..^2];
        if (word.Length == 0 || StopWords.Contains(word)) return;
        if (word.Length > 3 && word.EndsWith('s') && !word.EndsWith("ss", StringComparison.Ordinal)) word = word[..^1];
        tokens.Add(word);
    }

    [GeneratedRegex(@"[\p{L}\p{N}]+(?:[.\-'’][\p{L}\p{N}]+)*")]
    private static partial Regex Word();
}
