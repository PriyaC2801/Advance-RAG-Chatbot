namespace AdvancedRag.Pipelines.Imac;

/// <summary>The "G" in RAG: the model only sees the numbered passages and must cite them or refuse.</summary>
public static class ImacPrompts
{
    /// <summary>The exact sentence the model must reply with when the passages do not contain the answer.</summary>
    public const string Refusal = "I could not find this in the document.";

    public const string SystemPrompt = """
        You answer questions about one document (Apple's iMac Product Environmental Report) using ONLY the numbered context passages.
        - Use only facts stated in the passages. Never add outside knowledge.
        - Quote numbers exactly as written, with their units (kg CO2e, %, W).
        - Cite every claim with the passage number in brackets right after it, like [2] or [2][4] (separate brackets, no commas).
        - If a passage includes a [Note n] that limits or explains the claim, state that caveat in the same answer, in one short sentence.
        - If the question uses a different word than the document (e.g. "manufacturing" vs "Production"), say which term from the document you used.
        - Write plain prose in 1-3 short sentences: no markdown (no bullets, bold or headings) and no preamble such as "Based on the document".
        - If the passages do not contain the answer, reply exactly: "I could not find this in the document."
        """;

    public static string BuildUserMessage(string question, IReadOnlyList<ImacChunk> passages)
    {
        var context = string.Join("\n\n", passages.Select((c, i) => $"[{i + 1}] (page {c.Page}, {c.Kind})\n{c.Content}"));
        return $"Context passages:\n{context}\n\nQuestion: {question}";
    }

    /// <summary>The instruction sent with each rendered page image. Changing it invalidates nothing: the cache key is the page image + model.</summary>
    public const string VisionPrompt =
        "You are preparing one page of a product report for a search index. Write plain text capturing everything a reader learns from the CHARTS, TABLES, ICONS and CALL-OUTS on this page. " +
        "Transcribe every number together with its label, unit, and the column or group it belongs to. " +
        "For a chart, say what each bar or segment represents and how the values relate (for example totals, differences, before/after). " +
        "Mention footnote numbers where they appear, like [note 7]. Do not guess values that are not printed. Under 250 words.";
}
