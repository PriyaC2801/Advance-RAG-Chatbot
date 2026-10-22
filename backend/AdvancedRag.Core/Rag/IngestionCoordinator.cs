using System.Collections.Concurrent;

namespace AdvancedRag.Core.Rag;

/// <summary>
/// Runs ingestion in the background (it can take minutes: vision calls and rate-limited embeddings), one run per document.
/// The UI starts a run, then polls the status; <see cref="Apply"/> overlays "Indexing" / "Failed" on the pipeline's own status.
/// </summary>
public sealed class IngestionCoordinator(PipelineRegistry registry, ILogger<IngestionCoordinator> logger)
{
    private sealed class Run
    {
        public volatile string Progress = "Starting…";
        public volatile bool Done;
        public Exception? Error;
        public IngestResult? Result;
    }

    private readonly ConcurrentDictionary<string, Run> _runs = new(StringComparer.OrdinalIgnoreCase);

    public bool IsRunning(string documentId) => _runs.TryGetValue(documentId, out var run) && !run.Done;

    /// <summary>Starts a background ingestion. Throws <see cref="ConflictException"/> if one is already running for this document.</summary>
    public void Start(string documentId, bool force, string? mode)
    {
        var pipeline = registry.Get(documentId);
        var run = new Run();
        lock (_runs)
        {
            if (_runs.TryGetValue(pipeline.Info.Id, out var existing) && !existing.Done)
            {
                throw new ConflictException($"An ingestion of '{pipeline.Info.Id}' is already running.");
            }
            _runs[pipeline.Info.Id] = run;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var progress = new Progress<string>(message => run.Progress = message);
                run.Result = await pipeline.IngestAsync(force, mode, progress, CancellationToken.None);
                logger.LogInformation("Ingestion of {Document} finished: {Status}, {Chunks} chunks in {Seconds:0.0}s",
                    pipeline.Info.Id, run.Result.Status, run.Result.ChunkCount, run.Result.Duration.TotalSeconds);
            }
            catch (Exception ex)
            {
                run.Error = ex;
                logger.LogError(ex, "Ingestion of {Document} failed", pipeline.Info.Id);
            }
            finally
            {
                run.Done = true;
            }
        });
    }

    /// <summary>The result of the latest finished run, if any (null while running or when never run).</summary>
    public IngestResult? LastResult(string documentId) =>
        _runs.TryGetValue(documentId, out var run) && run.Done ? run.Result : null;

    /// <summary>Overlays the live run state on the status a pipeline reported.</summary>
    public PipelineStatus Apply(PipelineStatus status)
    {
        if (!_runs.TryGetValue(status.DocumentId, out var run)) return status;
        if (!run.Done) return status with { State = IndexState.Indexing, Message = run.Progress };
        if (run.Error is not null && status.State != IndexState.Ready)
        {
            return status with { State = IndexState.Failed, Message = run.Error.Message };
        }
        return status;
    }
}
