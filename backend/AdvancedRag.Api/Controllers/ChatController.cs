using AdvancedRag.Core.Rag;
using Microsoft.AspNetCore.Mvc;

namespace AdvancedRag.Api.Controllers;

[ApiController]
[Route("api/chat")]
public sealed class ChatController(PipelineRegistry registry, IngestionCoordinator ingestion) : ControllerBase
{
    /// <summary>Ask a question about one document. Body: <c>{ "documentId": "gep", "question": "...", "mode": null }</c>.</summary>
    [HttpPost("ask")]
    public async Task<AskResult> Ask([FromBody] AskRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.DocumentId)) throw new BadRequestException("documentId is required.");
        if (string.IsNullOrWhiteSpace(request.Question)) throw new BadRequestException("question is required.");
        if (request.Question.Length > 1000) throw new BadRequestException("question must be 1000 characters or fewer.");

        var pipeline = registry.Get(request.DocumentId);
        if (ingestion.IsRunning(pipeline.Info.Id))
        {
            throw new IndexNotReadyException($"'{pipeline.Info.Title}' is still being indexed. Try again when the status is Ready.");
        }
        return await pipeline.AskAsync(request.Question.Trim(), request.Mode, cancellationToken);
    }
}
