using AdvancedRag.Core.Rag;
using Microsoft.AspNetCore.Mvc;

namespace AdvancedRag.Api.Controllers;

/// <summary>A document's description together with its live index status.</summary>
public sealed record DocumentSummary(DocumentInfo Info, PipelineStatus Status);

[ApiController]
[Route("api/documents")]
public sealed class DocumentsController(PipelineRegistry registry, IngestionCoordinator ingestion) : ControllerBase
{
    /// <summary>The documents the user can pick from. Indexing happens by itself (see AutoIndexService); status shows when one is ready.</summary>
    [HttpGet]
    public async Task<IReadOnlyList<DocumentSummary>> List(CancellationToken cancellationToken)
    {
        var result = new List<DocumentSummary>();
        foreach (var pipeline in registry.All)
        {
            result.Add(new DocumentSummary(pipeline.Info, ingestion.Apply(await pipeline.GetStatusAsync(cancellationToken))));
        }
        return result;
    }
}
