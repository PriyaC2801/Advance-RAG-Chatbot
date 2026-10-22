namespace AdvancedRag.Pipelines.Imac;

/// <summary>
/// One extracted piece of the document (a table or a prose block). Raw keeps "[^n]" footnote markers;
/// Content is what the LLM reads, with the markers removed and the notes appended.
/// </summary>
public record ExtractedElement(string Kind, int Page, string? Section, string Raw, string Content, int[] Footnotes);

/// <summary>A chunk that has been built but not yet embedded and stored. Content is what the LLM reads, EmbedText what we search.</summary>
public record ChunkDraft(string Kind, int Page, string? Section, int[] Footnotes, string Content, string EmbedText);

/// <summary>A vision model's plain-text description of one visual page (chart, icons, call-outs).</summary>
public record VisionResult(int Page, string? Section, string Description);

/// <summary>A chunk as stored in the "imac_chunks" table (embedding left out). Rank fields are filled by search.</summary>
public sealed class ImacChunk
{
    public int Id { get; set; }
    public string Kind { get; set; } = "";
    public int Page { get; set; }
    public string? Section { get; set; }
    public int[] Footnotes { get; set; } = [];
    public string Content { get; set; } = "";
    public int? DenseRank { get; set; }
    public int? KeywordRank { get; set; }
    public double Score { get; set; }
}

/// <summary>One hand-checked question from eval/imac-questions.json.</summary>
public sealed class ImacEvalQuestion
{
    public string Id { get; set; } = "";
    public string Question { get; set; } = "";

    /// <summary>chart / table / callout / footnote / prose / refusal</summary>
    public string Type { get; set; } = "";

    public int[] ExpectedPages { get; set; } = [];

    /// <summary>Strings that must appear in the answer and in a relevant chunk.</summary>
    public string[] MustContain { get; set; } = [];
}

/// <summary>
/// The retrieval modes, in ablation order: each one adds a single technique to the previous one.
/// elements-dense = element chunks, dense | elements-hybrid = + keyword search with RRF | advanced = + vision chunks.
/// </summary>
public static class RetrievalModes
{
    public const string ElementsDense = "elements-dense";
    public const string ElementsHybrid = "elements-hybrid";
    public const string Advanced = "advanced";

    public static readonly IReadOnlyList<string> All = [ElementsDense, ElementsHybrid, Advanced];

    public static bool IsValid(string? mode) => mode != null && All.Contains(mode);

    /// <summary>A plain-language description of what the mode searches and how, shown to developers in the UI.</summary>
    public static string Describe(string mode) => mode switch
    {
        ElementsDense => "Dense search only (meaning) over table and text chunks",
        ElementsHybrid => "Hybrid: dense (meaning) + keyword (exact words), fused with Reciprocal Rank Fusion, over table and text chunks",
        Advanced => "Hybrid: dense (meaning) + keyword (exact words), fused with Reciprocal Rank Fusion, over table, text and vision (chart description) chunks",
        _ => mode,
    };

    /// <summary>Which chunk kinds a mode searches.</summary>
    public static string[] KindsFor(string mode) => mode == Advanced ? ["text", "table", "vision"] : ["text", "table"];

    public static bool IsHybrid(string mode) => mode != ElementsDense;

    /// <summary>Every mode searches the same stored collection; the modes only differ in which kinds and which rankings they use.</summary>
    public const string Collection = "advanced";
}
