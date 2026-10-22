namespace AdvancedRag.Pipelines.Gpo;

/// <summary>
/// Merges several ranked result lists into one using Reciprocal Rank Fusion: each chunk scores
/// the sum of 1 / (k + rank) over the lists it appears in. It uses only ranks, so the unrelated
/// score scales of vector similarity and keyword rank never have to be reconciled.
/// Lists are passed as (dense, keyword) so the result can report each retriever's rank.
/// </summary>
internal static class ReciprocalRankFusion
{
    public static IReadOnlyList<FusedChunk> Fuse(int take, int rankConstant, IReadOnlyList<ScoredChunk> dense, IReadOnlyList<ScoredChunk> keyword)
    {
        var scores = new Dictionary<string, (DocumentChunk Chunk, double Score, int? Dense, int? Keyword)>();

        void Add(IReadOnlyList<ScoredChunk> list, bool isDense)
        {
            for (var rank = 0; rank < list.Count; rank++)
            {
                var chunk = list[rank].Chunk;
                var contribution = 1.0 / (rankConstant + rank + 1);
                scores.TryGetValue(chunk.Key, out var existing);
                scores[chunk.Key] = (chunk, existing.Score + contribution,
                    isDense ? rank + 1 : existing.Dense, isDense ? existing.Keyword : rank + 1);
            }
        }

        Add(dense, true);
        Add(keyword, false);

        return scores.Values
            .OrderByDescending(s => s.Score)
            .Take(take)
            .Select(s => new FusedChunk(s.Chunk, s.Score, s.Dense, s.Keyword))
            .ToList();
    }
}
