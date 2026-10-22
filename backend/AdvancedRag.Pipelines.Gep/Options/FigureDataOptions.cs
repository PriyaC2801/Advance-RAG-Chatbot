namespace AdvancedRag.Pipelines.Gep;

/// <summary>
/// Chart-data extraction. Chart values are pixels in the PDF, not text, so each figure's page is
/// sent to a Gemini vision model and the numbers it reads are appended to the figure chunk.
/// Results are cached in data/figure-data.json, so a rebuild only calls Gemini for new figures.
/// </summary>
public sealed class FigureDataOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Vision model used to read the charts. Empty = the pipeline's generation model.</summary>
    public string Model { get; set; } = "gemini-3.5-flash";

    /// <summary>Cap on the extracted data text added to one figure chunk.</summary>
    public int MaxDataCharacters { get; set; } = 3500;

    public int MaxOutputTokens { get; set; } = 8192;

    /// <summary>Pause between page calls to stay within free-tier per-minute quotas.</summary>
    public int DelayBetweenCallsMilliseconds { get; set; } = 1500;
}
