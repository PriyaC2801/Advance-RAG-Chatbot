namespace AdvancedRag.Pipelines.Gep;

/// <summary>Limits and rules for grounded answer generation.</summary>
public sealed class AnsweringOptions
{
    /// <summary>
    /// Upper bound on report text sent to Gemini per question (about 3,000 tokens).
    /// Only the best-ranked relevant chunks are sent — never the whole 200-page PDF.
    /// </summary>
    public int MaxContextCharacters { get; set; } = 12_000;

    /// <summary>
    /// Evidence is "Sufficient" when at least this many chunks pass the relevance threshold
    /// (and, for comparisons, every compared region has at least one). Fewer → "Limited".
    /// </summary>
    public int MinRelevantChunksForSufficient { get; set; } = 2;

    /// <summary>
    /// Notes printed under a table (e.g. "the column for 2023 refers to FY2022/23") are sent with the
    /// table's rows, up to this many characters per table.
    /// </summary>
    public int MaxTableNotesCharacters { get; set; } = 3_500;

    /// <summary>Characters of each cited chunk returned as the source excerpt.</summary>
    public int SourceExcerptLength { get; set; } = 400;

    /// <summary>The fixed reply when the report does not support an answer.</summary>
    public string InsufficientEvidenceMessage { get; set; } =
        "I couldn't find sufficient evidence in this report to answer this question.";
}
