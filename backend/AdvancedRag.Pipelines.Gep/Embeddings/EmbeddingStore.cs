using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AdvancedRag.Pipelines.Gep.Embeddings;

/// <summary>One stored vector. TextHash lets us re-embed only chunks whose text actually changed.</summary>
public sealed record StoredEmbedding(string ChunkId, string TextHash, float[] Vector);

/// <summary>Contents of data/embeddings.json.</summary>
public sealed record EmbeddingIndexFile(
    string ModelId,
    int Dimensions,
    DateTimeOffset CreatedUtc,
    IReadOnlyList<StoredEmbedding> Items);

/// <summary>
/// Reads and writes data/embeddings.json. Vectors are kept separate from chunks.json so that the
/// readable chunk file stays readable, and so re-chunking does not force paying for all embeddings again.
/// </summary>
public sealed class EmbeddingStore
{
    public const string FileName = "embeddings.json";

    // Not indented: 600 chunks × 768 numbers is ~5 MB already.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Hash of exactly the text that was sent to the model (title + text).</summary>
    public static string HashInput(EmbeddingInput input) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{input.Title}\n{input.Text}")))[..16];

    public async Task SaveAsync(string dataDirectory, EmbeddingIndexFile index, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(dataDirectory);
        var path = Path.Combine(dataDirectory, FileName);
        var temp = path + ".tmp";
        await using (var stream = File.Create(temp))
        {
            await JsonSerializer.SerializeAsync(stream, index, Json, cancellationToken);
        }
        File.Move(temp, path, overwrite: true);
    }

    public async Task<EmbeddingIndexFile?> LoadAsync(string dataDirectory, CancellationToken cancellationToken)
    {
        var path = Path.Combine(dataDirectory, FileName);
        if (!File.Exists(path)) return null;
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<EmbeddingIndexFile>(stream, Json, cancellationToken);
    }
}
