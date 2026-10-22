using AdvancedRag.Pipelines.Gep.Models;
using AdvancedRag.Pipelines.Gep.Retrieval;

namespace AdvancedRag.Pipelines.Gep.Answering;

/// <summary>
/// Finds the notes printed under a table ("TABLE 2.10 ... — notes: ... fiscal-year basis ...").
/// The chunker stores notes as their own chunks, so retrieval can return a table's rows without the
/// note that explains them. Without the note, "India 2026f: 6.6" looks like calendar year 2026
/// when the report means fiscal year 2026/27. The note chunk shares the table's heading path.
/// </summary>
public sealed class TableNotesLookup(GepVectorStore store)
{
    public const string NotesMarker = " — notes:";

    private IReadOnlyDictionary<string, IReadOnlyList<DocumentChunk>>? _byHeadingPath;
    private object? _source;

    public static bool IsTable(DocumentChunk chunk) =>
        chunk.ContentType is ContentType.Table or ContentType.StatisticalAppendix;

    public static bool IsNotes(DocumentChunk chunk)
    {
        var start = chunk.Text.Length > 300 ? chunk.Text[..300] : chunk.Text;
        return start.Contains(NotesMarker, StringComparison.Ordinal);
    }

    /// <summary>The table's notes chunks in document order (empty for non-tables and for notes themselves).</summary>
    public IReadOnlyList<DocumentChunk> NotesFor(DocumentChunk chunk)
    {
        if (!IsTable(chunk) || IsNotes(chunk)) return [];
        return Index().GetValueOrDefault(chunk.HeadingPath) ?? [];
    }

    /// <summary>
    /// Attaches notes to table sources. A table's notes are attached once (to its first source);
    /// later sources from the same table point back to it. Notes already sent as a source are skipped.
    /// </summary>
    public IReadOnlyList<EvidenceSource> Attach(IReadOnlyList<EvidenceSource> evidence, int maxCharactersPerTable)
    {
        var sentIds = evidence.Select(e => e.Result.Chunk.Id).ToHashSet();
        var firstSourceOfTable = new Dictionary<string, string>();
        var result = new List<EvidenceSource>(evidence.Count);

        foreach (var source in evidence)
        {
            var chunk = source.Result.Chunk;
            var notes = NotesFor(chunk).Where(n => !sentIds.Contains(n.Id)).ToList();
            if (notes.Count == 0) { result.Add(source); continue; }

            if (firstSourceOfTable.TryGetValue(chunk.HeadingPath, out var firstId))
            {
                result.Add(source with { TableNotes = $"Same table as {firstId}; its notes apply here too." });
                continue;
            }

            firstSourceOfTable[chunk.HeadingPath] = source.SourceId;
            var text = string.Join("\n", notes.Select(n => n.Text));
            if (text.Length > maxCharactersPerTable) text = text[..maxCharactersPerTable].TrimEnd() + " …";
            result.Add(source with { TableNotes = text });
        }
        return result;
    }

    private IReadOnlyDictionary<string, IReadOnlyList<DocumentChunk>> Index()
    {
        var items = store.Chunks;
        if (_byHeadingPath is not null && ReferenceEquals(_source, items)) return _byHeadingPath;

        var index = items
            .Where(c => IsTable(c) && IsNotes(c))
            .GroupBy(c => c.HeadingPath)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<DocumentChunk>)g.OrderBy(c => c.ChunkIndex).ToList());

        _source = items;
        return _byHeadingPath = index;
    }
}
