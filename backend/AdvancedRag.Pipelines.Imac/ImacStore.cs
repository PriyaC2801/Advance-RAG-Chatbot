using AdvancedRag.Core.Rag;
using AdvancedRag.Core.Storage;
using Npgsql;
using NpgsqlTypes;
using Pgvector;

namespace AdvancedRag.Pipelines.Imac;

/// <summary>The imac_chunks table: schema, writes, listing and the two retrieval queries. Plain Npgsql, no ORM.</summary>
public sealed class ImacStore(NpgsqlDataSource dataSource)
{
    public const string Table = "imac_chunks";

    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private bool _schemaReady;

    // Same columns as the original db/init.sql (table "chunks"), plus IF NOT EXISTS because there is no init script here.
    private static readonly string SchemaSql = $$"""
        CREATE TABLE IF NOT EXISTS {{Table}} (
          id            serial PRIMARY KEY,
          collection    text NOT NULL,          -- 'advanced'
          kind          text NOT NULL,          -- 'text' | 'table' | 'vision'
          page          int  NOT NULL,
          section       text,                   -- e.g. 'Use', 'Package and Ship'
          footnotes     int[] DEFAULT '{}',
          content       text NOT NULL,          -- what the LLM reads
          embed_text    text NOT NULL,          -- what we embed/search (content + small header)
          embedding     vector(1536) NOT NULL,
          tsv           tsvector GENERATED ALWAYS AS (to_tsvector('english', embed_text)) STORED
        );
        CREATE INDEX IF NOT EXISTS {{Table}}_embedding_hnsw ON {{Table}} USING hnsw (embedding vector_cosine_ops);
        CREATE INDEX IF NOT EXISTS {{Table}}_tsv_gin ON {{Table}} USING gin (tsv);
        CREATE INDEX IF NOT EXISTS {{Table}}_collection_idx ON {{Table}} (collection);
        """;

    /// <summary>Opens a connection and creates the table on first use (so queries on a fresh database find an empty table, not an error).</summary>
    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var conn = await VectorDb.OpenAsync(dataSource, ct);
        if (!_schemaReady)
        {
            await _schemaLock.WaitAsync(ct);
            try
            {
                if (!_schemaReady)
                {
                    await using var cmd = new NpgsqlCommand(SchemaSql, conn);
                    await cmd.ExecuteNonQueryAsync(ct);
                    _schemaReady = true;
                }
            }
            catch (Exception ex) when (VectorDb.IsConnectivityFailure(ex))
            {
                await conn.DisposeAsync();
                throw VectorDb.Unavailable(ex);
            }
            finally
            {
                _schemaLock.Release();
            }
        }
        return conn;
    }

    /// <summary>Deletes the whole collection, then stores the chunks with their vectors, in one transaction (re-running ingest never duplicates).</summary>
    public async Task ReplaceCollectionAsync(string collection, IReadOnlyList<ChunkDraft> chunks, IReadOnlyList<float[]> vectors, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await using (var delete = new NpgsqlCommand($"DELETE FROM {Table} WHERE collection = @collection", conn, tx))
        {
            delete.Parameters.AddWithValue("collection", collection);
            await delete.ExecuteNonQueryAsync(ct);
        }

        for (var i = 0; i < chunks.Count; i++)
        {
            var c = chunks[i];
            await using var insert = new NpgsqlCommand(
                $@"INSERT INTO {Table} (collection, kind, page, section, footnotes, content, embed_text, embedding)
                   VALUES (@collection, @kind, @page, @section, @footnotes, @content, @embed_text, @embedding)", conn, tx);
            insert.Parameters.AddWithValue("collection", collection);
            insert.Parameters.AddWithValue("kind", c.Kind);
            insert.Parameters.AddWithValue("page", c.Page);
            insert.Parameters.Add(new NpgsqlParameter("section", NpgsqlDbType.Text) { Value = (object?)c.Section ?? DBNull.Value });
            insert.Parameters.Add(new NpgsqlParameter("footnotes", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = c.Footnotes });
            insert.Parameters.AddWithValue("content", c.Content);
            insert.Parameters.AddWithValue("embed_text", c.EmbedText);
            insert.Parameters.Add(new NpgsqlParameter("embedding", new Vector(vectors[i])));
            await insert.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    /// <summary>How many chunks of each kind a collection holds (for example table = 3, text = 100, vision = 10).</summary>
    public async Task<IReadOnlyDictionary<string, int>> CountByKindAsync(string collection, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"SELECT kind, count(*)::int FROM {Table} WHERE collection = @collection GROUP BY kind ORDER BY kind", conn);
        cmd.Parameters.AddWithValue("collection", collection);
        var result = new Dictionary<string, int>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result[reader.GetString(0)] = reader.GetInt32(1);
        return result;
    }

    public async Task<(int Total, IReadOnlyList<ImacChunk> Items)> ListAsync(
        string collection, string? kind, int? page, string? contains, int skip, int take, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        const string where = @"WHERE collection = @collection
              AND (@kind::text IS NULL OR kind = @kind)
              AND (@page::int IS NULL OR page = @page)
              AND (@contains::text IS NULL OR content ILIKE '%' || @contains || '%')";

        void Bind(NpgsqlCommand cmd)
        {
            cmd.Parameters.AddWithValue("collection", collection);
            cmd.Parameters.Add(new NpgsqlParameter("kind", NpgsqlDbType.Text) { Value = (object?)kind ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("page", NpgsqlDbType.Integer) { Value = (object?)page ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("contains", NpgsqlDbType.Text) { Value = (object?)contains ?? DBNull.Value });
        }

        int total;
        await using (var count = new NpgsqlCommand($"SELECT count(*)::int FROM {Table} {where}", conn))
        {
            Bind(count);
            total = (int)(await count.ExecuteScalarAsync(ct))!;
        }

        await using var list = new NpgsqlCommand(
            $@"SELECT id, kind, page, section, footnotes, content FROM {Table} {where}
               ORDER BY page, id OFFSET @skip LIMIT @take", conn);
        Bind(list);
        list.Parameters.AddWithValue("skip", skip);
        list.Parameters.AddWithValue("take", take);
        var items = new List<ImacChunk>();
        await using var reader = await list.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) items.Add(Read(reader, ranked: false));
        return (total, items);
    }

    /// <summary>Dense-only search: the K nearest chunk vectors (cosine distance). <c>kinds</c> lets the ablation leave out 'vision'.</summary>
    public async Task<IReadOnlyList<ImacChunk>> DenseSearchAsync(string collection, float[] vector, string[] kinds, int topK, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $@"SELECT id, kind, page, section, footnotes, content,
                     ROW_NUMBER() OVER (ORDER BY embedding <=> @q)::int AS denserank,
                     NULL::int AS keywordrank,
                     (1 - (embedding <=> @q))::float8 AS score
              FROM {Table} WHERE collection = @collection AND kind = ANY(@kinds)
              ORDER BY embedding <=> @q LIMIT @k", conn);
        cmd.Parameters.Add(new NpgsqlParameter("q", new Vector(vector)));
        cmd.Parameters.AddWithValue("collection", collection);
        cmd.Parameters.AddWithValue("k", topK);
        cmd.Parameters.AddWithValue("kinds", kinds);
        return await ReadAllAsync(cmd, ct);
    }

    // Hybrid search = meaning (dense vectors) + exact words (Postgres full text), merged with Reciprocal Rank Fusion.
    // Dense is good at paraphrase ("manufacturing" ~ "Production"); keyword is good at exact tokens ("20.9W", "230V").
    private static readonly string HybridSql = $@"
WITH qry AS (
  -- plainto_tsquery ANDs every word; a question never matches that, so swap '&' for '|' (any word may match).
  SELECT @q::vector AS vec,
         replace(plainto_tsquery('english', @question)::text, '&', '|')::tsquery AS tsq
),
dense AS (
  -- the N nearest chunks by cosine distance; rnk 1 = closest
  SELECT c.id, ROW_NUMBER() OVER (ORDER BY c.embedding <=> qry.vec) AS rnk
  FROM {Table} c, qry
  WHERE c.collection = @collection AND c.kind = ANY(@kinds)
  ORDER BY c.embedding <=> qry.vec
  LIMIT @n
),
keyword AS (
  -- the N chunks with the best full-text score (ts_rank_cd rewards matching words that sit close together)
  SELECT c.id, ROW_NUMBER() OVER (ORDER BY ts_rank_cd(c.tsv, qry.tsq) DESC, c.id) AS rnk
  FROM {Table} c, qry
  WHERE c.collection = @collection AND c.kind = ANY(@kinds) AND c.tsv @@ qry.tsq
  ORDER BY ts_rank_cd(c.tsv, qry.tsq) DESC, c.id
  LIMIT @n
),
fused AS (
  -- Reciprocal Rank Fusion: score = 1/(k+rank) from each list; a chunk missing from a list adds 0.
  -- Ranks (not raw scores) are fused because cosine similarity and ts_rank live on different scales.
  SELECT COALESCE(d.id, k.id) AS id, d.rnk AS dense_rank, k.rnk AS keyword_rank,
         COALESCE(1.0 / (@rrfk + d.rnk), 0) + COALESCE(1.0 / (@rrfk + k.rnk), 0) AS score
  FROM dense d FULL OUTER JOIN keyword k ON d.id = k.id
  ORDER BY score DESC, COALESCE(d.id, k.id)
  LIMIT @topk
)
SELECT c.id, c.kind, c.page, c.section, c.footnotes, c.content,
       f.dense_rank::int AS denserank, f.keyword_rank::int AS keywordrank, f.score::float8 AS score
FROM fused f JOIN {Table} c ON c.id = f.id
ORDER BY f.score DESC, c.id";

    public async Task<IReadOnlyList<ImacChunk>> HybridSearchAsync(
        string collection, float[] vector, string question, string[] kinds, int candidates, int rrfK, int topK, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(HybridSql, conn);
        cmd.Parameters.Add(new NpgsqlParameter("q", new Vector(vector)));
        cmd.Parameters.AddWithValue("question", question);
        cmd.Parameters.AddWithValue("collection", collection);
        cmd.Parameters.AddWithValue("kinds", kinds);
        cmd.Parameters.AddWithValue("n", candidates);
        cmd.Parameters.AddWithValue("rrfk", rrfK);
        cmd.Parameters.AddWithValue("topk", topK);
        return await ReadAllAsync(cmd, ct);
    }

    private static async Task<IReadOnlyList<ImacChunk>> ReadAllAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        var rows = new List<ImacChunk>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) rows.Add(Read(reader, ranked: true));
        return rows;
    }

    private static ImacChunk Read(NpgsqlDataReader r, bool ranked)
    {
        var chunk = new ImacChunk
        {
            Id = r.GetInt32(0),
            Kind = r.GetString(1),
            Page = r.GetInt32(2),
            Section = r.IsDBNull(3) ? null : r.GetString(3),
            Footnotes = r.IsDBNull(4) ? [] : r.GetFieldValue<int[]>(4),
            Content = r.GetString(5),
        };
        if (ranked)
        {
            chunk.DenseRank = r.IsDBNull(6) ? null : r.GetInt32(6);
            chunk.KeywordRank = r.IsDBNull(7) ? null : r.GetInt32(7);
            chunk.Score = r.GetDouble(8);
        }
        return chunk;
    }
}
