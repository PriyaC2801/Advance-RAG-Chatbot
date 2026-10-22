namespace AdvancedRag.Core.Rag;

/// <summary>Finds the pipeline for a document id. Pipelines register themselves as <see cref="IRagPipeline"/> singletons.</summary>
public sealed class PipelineRegistry(IEnumerable<IRagPipeline> pipelines)
{
    private readonly Dictionary<string, IRagPipeline> _byId =
        pipelines.ToDictionary(p => p.Info.Id, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<IRagPipeline> All => _byId.Values.OrderBy(p => p.Info.Id).ToList();

    public IRagPipeline Get(string documentId) =>
        _byId.TryGetValue(documentId, out var pipeline)
            ? pipeline
            : throw new KeyNotFoundException($"Unknown document '{documentId}'. Known documents: {string.Join(", ", _byId.Keys)}.");
}
