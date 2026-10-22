using AdvancedRag.Core.Ai;
using AdvancedRag.Core.Rag;

namespace AdvancedRag.Api.Infrastructure;

/// <summary>
/// Makes every document ready without any user action: shortly after startup, each document that is not indexed
/// is indexed in the background (cached embeddings and vision results are reused, so an already-built index costs nothing).
/// Tried once per document per start; a failure is logged and shown as the document's status message.
/// </summary>
public sealed class AutoIndexService(
    PipelineRegistry registry, IngestionCoordinator ingestion, IGeminiClient gemini, ILogger<AutoIndexService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
        if (!gemini.IsConfigured)
        {
            logger.LogWarning("Gemini API key is not configured; documents will not be indexed automatically.");
            return;
        }

        foreach (var pipeline in registry.All)
        {
            try
            {
                var status = await pipeline.GetStatusAsync(stoppingToken);
                if (status.State == IndexState.Ready || !status.PdfFound) continue;

                logger.LogInformation("Indexing '{Document}' in the background", pipeline.Info.Id);
                ingestion.Start(pipeline.Info.Id, force: false, mode: null);
                // One at a time: the free tier's per-minute embedding quota is shared.
                while (ingestion.IsRunning(pipeline.Info.Id)) await Task.Delay(2000, stoppingToken);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Automatic indexing of '{Document}' could not start", pipeline.Info.Id);
            }
        }
    }
}
