namespace AdvancedRag.Pipelines.Gep;

/// <summary>
/// Chunking settings (section "Chunking" in appsettings.json). These are reasonable starting
/// values, not universally optimal ones.
/// Changing any value changes the settings fingerprint, so chunks.json is rebuilt.
/// </summary>
public sealed class ChunkingOptions
{
    /// <summary>Upper limit for a chunk's text (~1,500 chars ≈ 300–350 English tokens).</summary>
    public int MaxChunkCharacters { get; set; } = 1500;

    /// <summary>
    /// Whole sentences repeated from the end of the previous chunk (prose only, not tables).
    /// 300 ≈ one typical sentence of this report; 200 was often too small to carry any sentence.
    /// </summary>
    public int OverlapCharacters { get; set; } = 300;

    /// <summary>A final piece shorter than this is merged into the previous chunk of the same section.</summary>
    public int MinChunkCharacters { get; set; } = 250;

    // The two lists start empty on purpose: the configuration binder APPENDS to a list that
    // already has items, so defaults here would be duplicated. Values live in appsettings.json.

    /// <summary>Front-matter parts that are not content (tables of contents would pollute retrieval).</summary>
    public List<string> SkippedFrontMatterTitles { get; set; } = [];

    /// <summary>The chapter whose 16pt section headings are regions.</summary>
    public int RegionalChapterNumber { get; set; } = 2;

    /// <summary>
    /// World Bank EMDE region names. A heading or table row must match one of these exactly to
    /// set a chunk's Region — body text mentioning a region never does.
    /// </summary>
    public List<string> RegionNames { get; set; } = [];
}
