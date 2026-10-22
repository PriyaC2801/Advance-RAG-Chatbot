using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using AdvancedRag.Pipelines.Gep.Models;

namespace AdvancedRag.Pipelines.Gep.Chunking;

/// <summary>
/// Contents of data/chunks.json. The hashes let us skip re-chunking when neither the PDF nor the
/// chunking settings changed.
/// </summary>
public sealed record ChunkIndexFile(
    string DocumentId,
    string SourceFileName,
    string SourceSha256,
    string SettingsFingerprint,
    DateTimeOffset CreatedUtc,
    int PageCount,
    IReadOnlyList<DocumentChunk> Chunks);

/// <summary>Reads and writes chunks.json.</summary>
public sealed class ChunkStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true, // the file is meant to be read by a learner
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // Keep "’", "–" and "Türkiye" readable instead of \u escapes.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public const string FileName = "chunks.json";

    public async Task SaveAsync(string dataDirectory, ChunkIndexFile index, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(dataDirectory);
        var path = Path.Combine(dataDirectory, FileName);
        var temp = path + ".tmp";

        // Write to a temp file and then move it, so a crash never leaves a half-written chunks.json.
        await using (var stream = File.Create(temp))
        {
            await JsonSerializer.SerializeAsync(stream, index, JsonOptions, cancellationToken);
        }
        File.Move(temp, path, overwrite: true);
    }

    public async Task<ChunkIndexFile?> LoadAsync(string dataDirectory, CancellationToken cancellationToken)
    {
        var path = Path.Combine(dataDirectory, FileName);
        if (!File.Exists(path)) return null;

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<ChunkIndexFile>(stream, JsonOptions, cancellationToken);
    }
}
