using System.Text.Json;
using System.Text.Json.Serialization;
using AdvancedRag.Core.Rag;
using AdvancedRag.Core.Storage;
using AdvancedRag.Pipelines.Gep.Models;
using Npgsql;
using NpgsqlTypes;
using Pgvector;

namespace AdvancedRag.Pipelines.Gep.Retrieval;

/// <summary>A chunk together with its (normalized) embedding vector and the hash of the text that was embedded.</summary>
public sealed record IndexedChunk(DocumentChunk Chunk, float[] Vector, string TextHash);

/// <summary>
/// PostgreSQL + pgvector vector store. The table holds one row per chunk: its metadata as JSON, the hash of the embedded
/// text and its embedding as a <c>vector(N)</c>. Similarity is computed by pgvector (<c>1 - (embedding &lt;=&gt; query)</c>,
/// cosine distance). data/gep/chunks.json and embeddings.json remain the build artifacts; ingestion copies them into the
/// table. Chunk metadata is also kept in memory for filters, keyword search and table notes.
/// </summary>
public sealed class GepVectorStore(NpgsqlDataSource dataSource, IOptions<GepOptions> options, ILogger<GepVectorStore> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private volatile Snapshot? _snapshot;

    private sealed record Snapshot(IReadOnlyList<DocumentChunk> Chunks, string ModelId);

    public bool IsLoaded => _snapshot is not null;
    public string? ModelId => _snapshot?.ModelId;
    public IReadOnlyList<DocumentChunk> Chunks => _snapshot?.Chunks ?? [];

    private string Table => VectorDb.SafeTableName(options.Value.TableName);

    /// <summary>A dropped connection or refused socket (a SQL error from a reachable server is a different problem).</summary>
    private static bool IsUnreachable(Exception ex) =>
        ex is NpgsqlException and not PostgresException or System.Net.Sockets.SocketException;

    /// <summary>Replaces the whole index (table and in-memory metadata).</summary>
    public async Task ReplaceAsync(IReadOnlyList<IndexedChunk> items, string modelId, CancellationToken cancellationToken)
    {
        var dimensions = items.Count > 0 ? items[0].Vector.Length : 0;
        try
        {
            await using var connection = await VectorDb.OpenAsync(dataSource, cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            // Rebuilt from scratch: ~600 rows load in about a second, and a changed embedding size just works.
            await using (var create = new NpgsqlCommand($"""
                DROP TABLE IF EXISTS {Table};
                CREATE TABLE {Table} (
                    chunk_id    text PRIMARY KEY,
                    chunk_index integer NOT NULL,
                    model_id    text NOT NULL,
                    text_hash   text NOT NULL,
                    chunk       jsonb NOT NULL,
                    embedding   vector({dimensions}) NOT NULL
                );
                """, connection, transaction))
            {
                await create.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var import = await connection.BeginBinaryImportAsync(
                $"COPY {Table} (chunk_id, chunk_index, model_id, text_hash, chunk, embedding) FROM STDIN (FORMAT BINARY)", cancellationToken))
            {
                foreach (var item in items)
                {
                    await import.StartRowAsync(cancellationToken);
                    await import.WriteAsync(item.Chunk.Id, NpgsqlDbType.Text, cancellationToken);
                    await import.WriteAsync(item.Chunk.ChunkIndex, NpgsqlDbType.Integer, cancellationToken);
                    await import.WriteAsync(modelId, NpgsqlDbType.Text, cancellationToken);
                    await import.WriteAsync(item.TextHash, NpgsqlDbType.Text, cancellationToken);
                    await import.WriteAsync(JsonSerializer.Serialize(item.Chunk with { Embedding = null }, Json), NpgsqlDbType.Jsonb, cancellationToken);
                    await import.WriteAsync(new Vector(item.Vector), cancellationToken);
                }
                await import.CompleteAsync(cancellationToken);
            }

            // Approximate-nearest-neighbour index for top-N searches once the report outgrows a full scan.
            await using (var index = new NpgsqlCommand(
                $"CREATE INDEX ON {Table} USING hnsw (embedding vector_cosine_ops)", connection, transaction))
            {
                await index.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception ex) when (IsUnreachable(ex))
        {
            throw VectorDb.Unavailable(ex);
        }

        _snapshot = new Snapshot(items.Select(i => i.Chunk).ToList(), modelId);
        logger.LogInformation("Wrote {Count} chunks ({Dimensions} dimensions) to PostgreSQL table {Table}", items.Count, dimensions, Table);
    }

    /// <summary>
    /// Adopts a table that already holds exactly these chunks (same ids, same embedded-text hashes, same model),
    /// so a restart does not have to rewrite it. Returns false when the table is missing or differs.
    /// </summary>
    public async Task<bool> TryAdoptExistingAsync(IReadOnlyList<DocumentChunk> chunks,
        IReadOnlyDictionary<string, string> expectedHashes, string modelId, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await VectorDb.OpenAsync(dataSource, cancellationToken);
            await using var command = new NpgsqlCommand($"SELECT chunk_id, text_hash, model_id FROM {Table}", connection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            var found = 0;
            while (await reader.ReadAsync(cancellationToken))
            {
                if (reader.GetString(2) != modelId
                    || !expectedHashes.TryGetValue(reader.GetString(0), out var hash) || hash != reader.GetString(1))
                {
                    return false;
                }
                found++;
            }
            if (found != chunks.Count) return false;
        }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.UndefinedColumn)
        {
            return false; // no table yet (or one from an older layout): the caller rebuilds it
        }
        catch (Exception ex) when (IsUnreachable(ex))
        {
            throw VectorDb.Unavailable(ex);
        }

        _snapshot = new Snapshot(chunks, modelId);
        return true;
    }

    /// <summary>Cosine similarity between the query vector and the stored vector of each given chunk id.</summary>
    public async Task<IReadOnlyDictionary<string, double>> SimilaritiesAsync(float[] queryVector,
        IReadOnlyCollection<string> chunkIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, double>(chunkIds.Count);
        try
        {
            await using var connection = await VectorDb.OpenAsync(dataSource, cancellationToken);
            await using var command = new NpgsqlCommand(
                $"SELECT chunk_id, 1 - (embedding <=> @query) FROM {Table} WHERE chunk_id = ANY(@ids)", connection);
            command.Parameters.AddWithValue("query", new Vector(queryVector));
            command.Parameters.AddWithValue("ids", chunkIds.ToArray());

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                result[reader.GetString(0)] = reader.GetDouble(1);
            }
        }
        catch (Exception ex) when (IsUnreachable(ex))
        {
            throw VectorDb.Unavailable(ex);
        }
        return result;
    }
}
