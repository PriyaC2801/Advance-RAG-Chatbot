using System.Text.RegularExpressions;
using AdvancedRag.Core.Storage;
using Npgsql;
using Pgvector;

namespace AdvancedRag.Pipelines.Gpo;

/// <summary>
/// The gpo_chunks table, accessed with plain Npgsql SQL. The vector column holds the dense embedding; the generated
/// <c>tsv</c> column backs the keyword search, so both retrievers read the same rows.
/// </summary>
internal sealed partial class GpoChunkStore(NpgsqlDataSource dataSource, GpoOptions options)
{
    private readonly string _table = VectorDb.SafeTableName(options.TableName);

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex KeywordToken();

    /// <summary>Number of stored chunks, 0 when the table does not exist.</summary>
    public async Task<int> CountAsync(CancellationToken ct)
    {
        await using var connection = await VectorDb.OpenAsync(dataSource, ct);
        if (!await TableExistsAsync(connection, ct)) return 0;
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM {_table}", connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct));
    }

    /// <summary>Count that never throws: reports the error text when the database is down.</summary>
    public async Task<(int Count, string? Error)> TryCountAsync(CancellationToken ct)
    {
        try { return (await CountAsync(ct), null); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return (0, ex.Message); }
    }

    private async Task<bool> TableExistsAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("SELECT to_regclass(@t) IS NOT NULL", connection);
        command.Parameters.AddWithValue("t", _table);
        return (bool)(await command.ExecuteScalarAsync(ct))!;
    }

    /// <summary>Drops and recreates the table (and its HNSW + GIN indexes), then inserts every chunk.</summary>
    public async Task ReplaceAllAsync(IReadOnlyList<ChunkDraft> chunks, IReadOnlyList<float[]> embeddings, CancellationToken ct)
    {
        await using var connection = await VectorDb.OpenAsync(dataSource, ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        var ddl = $"""
            DROP TABLE IF EXISTS {_table};
            CREATE TABLE {_table} (
                chunk_id    text PRIMARY KEY,
                page        int  NOT NULL,
                chunk_index int  NOT NULL,
                content     text NOT NULL,
                embedding   vector({options.EmbeddingDimensions}) NOT NULL,
                tsv         tsvector GENERATED ALWAYS AS (to_tsvector('english', content)) STORED
            );
            CREATE INDEX {_table}_embedding_hnsw ON {_table} USING hnsw (embedding vector_cosine_ops);
            CREATE INDEX {_table}_tsv_gin ON {_table} USING gin (tsv);
            """;
        await using (var create = new NpgsqlCommand(ddl, connection, tx))
            await create.ExecuteNonQueryAsync(ct);

        for (var i = 0; i < chunks.Count; i++)
        {
            await using var insert = new NpgsqlCommand(
                $"INSERT INTO {_table} (chunk_id, page, chunk_index, content, embedding) VALUES (@id, @page, @idx, @content, @emb)",
                connection, tx);
            insert.Parameters.AddWithValue("id", ChunkId(chunks[i].ChunkIndex));
            insert.Parameters.AddWithValue("page", chunks[i].PageNumber);
            insert.Parameters.AddWithValue("idx", chunks[i].ChunkIndex);
            insert.Parameters.AddWithValue("content", chunks[i].Text);
            insert.Parameters.AddWithValue("emb", new Vector(VectorMath.Normalize(embeddings[i])));
            await insert.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    public static string ChunkId(int index) => $"gpo-{index:D4}";

    /// <summary>Dense retrieval: cosine distance (&lt;=&gt;); the score is the cosine similarity.</summary>
    public async Task<IReadOnlyList<ScoredChunk>> SearchDenseAsync(float[] queryEmbedding, int topK, CancellationToken ct)
    {
        await using var connection = await VectorDb.OpenAsync(dataSource, ct);
        await using var command = new NpgsqlCommand($"""
            SELECT chunk_id, chunk_index, page, content, 1 - (embedding <=> @q) AS score
            FROM {_table} ORDER BY embedding <=> @q LIMIT @limit
            """, connection);
        command.Parameters.AddWithValue("q", new Vector(VectorMath.Normalize(queryEmbedding)));
        command.Parameters.AddWithValue("limit", topK);
        return await ReadAsync(command, ct);
    }

    public async Task<IReadOnlyList<ScoredChunk>> SearchKeywordsAsync(string question, int topK, CancellationToken ct)
    {
        // Questions are natural language, so match ANY meaningful term and let ts_rank_cd
        // put chunks matching more (and closer-together) terms first. Words are reduced to
        // letters/digits, which also makes the websearch syntax safe against user input.
        var terms = KeywordToken().Matches(question)
            .Select(m => m.Value.ToLowerInvariant())
            .Where(t => t is not "or" and not "and")
            .Distinct()
            .ToList();
        if (terms.Count == 0) return [];

        await using var connection = await VectorDb.OpenAsync(dataSource, ct);
        await using var command = new NpgsqlCommand($"""
            SELECT chunk_id, chunk_index, page, content, ts_rank_cd(tsv, q.query) AS rank
            FROM {_table}, (SELECT websearch_to_tsquery('english', @query) AS query) AS q
            WHERE tsv @@ q.query
            ORDER BY rank DESC
            LIMIT @limit
            """, connection);
        command.Parameters.AddWithValue("query", string.Join(" or ", terms));
        command.Parameters.AddWithValue("limit", topK);
        return await ReadAsync(command, ct);
    }

    private static async Task<IReadOnlyList<ScoredChunk>> ReadAsync(NpgsqlCommand command, CancellationToken ct)
    {
        var results = new List<ScoredChunk>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new ScoredChunk(
                new DocumentChunk(reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetString(3)),
                Convert.ToDouble(reader.GetValue(4))));
        }
        return results;
    }

    public async Task<(int Total, IReadOnlyList<DocumentChunk> Items)> ListAsync(int? page, string? contains, int skip, int take, CancellationToken ct)
    {
        await using var connection = await VectorDb.OpenAsync(dataSource, ct);
        if (!await TableExistsAsync(connection, ct)) return (0, []);

        const string where = "WHERE (@page::int IS NULL OR page = @page) AND (@contains::text IS NULL OR content ILIKE '%' || @contains || '%')";
        void Bind(NpgsqlCommand c)
        {
            c.Parameters.AddWithValue("page", (object?)page ?? DBNull.Value);
            // Escape LIKE wildcards so the filter is a literal substring match.
            c.Parameters.AddWithValue("contains", string.IsNullOrEmpty(contains)
                ? DBNull.Value
                : contains.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_"));
        }

        await using var count = new NpgsqlCommand($"SELECT count(*) FROM {_table} {where}", connection);
        Bind(count);
        var total = Convert.ToInt32(await count.ExecuteScalarAsync(ct));

        await using var list = new NpgsqlCommand($"""
            SELECT chunk_id, chunk_index, page, content FROM {_table} {where}
            ORDER BY chunk_index OFFSET @skip LIMIT @take
            """, connection);
        Bind(list);
        list.Parameters.AddWithValue("skip", Math.Max(0, skip));
        list.Parameters.AddWithValue("take", Math.Clamp(take, 1, 500));
        var items = new List<DocumentChunk>();
        await using var reader = await list.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            items.Add(new DocumentChunk(reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetString(3)));
        return (total, items);
    }
}
