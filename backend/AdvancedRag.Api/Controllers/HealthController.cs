using AdvancedRag.Core.Ai;
using AdvancedRag.Core.Rag;
using Microsoft.AspNetCore.Mvc;

namespace AdvancedRag.Api.Controllers;

public sealed record HealthResponse(string Status, bool GeminiApiKeyConfigured, IReadOnlyList<string> Documents);

[ApiController]
[Route("api/health")]
public sealed class HealthController(IGeminiClient gemini, PipelineRegistry registry) : ControllerBase
{
    [HttpGet]
    public HealthResponse Get() => new("ok", gemini.IsConfigured, registry.All.Select(p => p.Info.Id).ToList());
}
