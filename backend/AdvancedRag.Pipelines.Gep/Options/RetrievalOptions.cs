namespace AdvancedRag.Pipelines.Gep;

/// <summary>
/// Retrieval and ranking settings (section "Retrieval"). The boosts are deliberately small compared
/// with typical similarity differences: metadata nudges the ranking, it does not override meaning.
/// </summary>
public sealed class RetrievalOptions
{
    public int DefaultTopK { get; set; } = 5;
    public int MaxTopK { get; set; } = 20;

    /// <summary>
    /// Chunks whose raw cosine similarity is below this are treated as "not relevant enough".
    /// The right value depends on the embedding model. Calibrated for gemini-embedding-2 / 768 dims
    /// on 10 real questions (Oct 2026): answerable questions scored 0.74–0.84 for their top chunks;
    /// off-topic ones scored 0.53–0.68 (Atlantis 0.68, FIFA 0.57, cookies 0.53).
    /// </summary>
    public double MinRelevanceScore { get; set; } = 0.70;

    /// <summary>Added when a chunk's Region is a region the question asks about.</summary>
    public double RegionMatchBoost { get; set; } = 0.05;

    /// <summary>Subtracted when a chunk belongs to a different region than the one(s) asked about.</summary>
    public double RegionMismatchPenalty { get; set; } = 0.05;

    /// <summary>Added when a word from the question appears in the chunk's section or subsection heading.</summary>
    public double HeadingMatchBoost { get; set; } = 0.02;

    /// <summary>Subtracted for bibliographies, front matter and figure captions unless the question asks for them.</summary>
    public double LowValueContentPenalty { get; set; } = 0.03;

    /// <summary>Keyword (BM25) search on top of the semantic ranking: exact words, figure/table numbers, acronyms.</summary>
    public bool KeywordSearchEnabled { get; set; } = true;

    /// <summary>
    /// Largest score a perfect keyword match adds, scaled by how much of the question the chunk covers.
    /// Small on purpose, like the other adjustments: keywords nudge the ranking, meaning still leads.
    /// </summary>
    public double KeywordMatchBoost { get; set; } = 0.06;

    /// <summary>
    /// A chunk just below <see cref="MinRelevanceScore"/> still counts as relevant when it contains at least
    /// this share of the question's term weight (and two or more terms). Words that match many chunks weigh little,
    /// so an off-topic question that only shares common words does not qualify.
    /// </summary>
    public double KeywordRescueCoverage { get; set; } = 0.6;

    /// <summary>How far below <see cref="MinRelevanceScore"/> the similarity may be for a keyword rescue.</summary>
    public double KeywordRescueMargin { get; set; } = 0.08;

    /// <summary>Extra names that refer to a region, e.g. "SAR" → "South Asia". Keys are region names.</summary>
    public Dictionary<string, List<string>> RegionAliases { get; set; } = [];
}
