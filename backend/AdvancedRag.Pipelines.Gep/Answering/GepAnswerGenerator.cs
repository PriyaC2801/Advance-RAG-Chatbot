using System.Text.Json.Nodes;
using AdvancedRag.Core.Ai;

namespace AdvancedRag.Pipelines.Gep.Answering;

/// <summary>
/// Answer generation with Gemini through the shared client. The system instruction carries the grounding rules;
/// JSON mode + a response schema make the reply machine-checkable. Deliberately thin: prompt building and output
/// validation live elsewhere.
/// </summary>
public sealed class GepAnswerGenerator(IGeminiClient gemini, IOptions<GepOptions> options)
{
    /// <summary>The shape of <see cref="ModelAnswer"/> as a Gemini response schema.</summary>
    private static readonly JsonNode Schema = JsonNode.Parse("""
        {
          "type": "object",
          "properties": {
            "status": {
              "type": "string",
              "description": "\"answered\" if the excerpts support an answer (possibly partial); \"insufficient_evidence\" if they do not."
            },
            "answer": {
              "type": "string",
              "description": "The answer, with an inline citation such as [S1] after each factual claim."
            },
            "citedSourceIds": {
              "type": "array",
              "items": { "type": "string" },
              "description": "Every source id cited in the answer, e.g. [\"S1\", \"S3\"]."
            }
          },
          "required": ["status", "answer", "citedSourceIds"]
        }
        """)!;

    /// <summary>Model name, shown in the debug panel.</summary>
    public string ModelId => options.Value.GenerationModel;

    public async Task<ModelOutput> GenerateAsync(AnswerPrompt prompt, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var reply = await gemini.GenerateAsync(new ChatRequest(
            settings.GenerationModel, prompt.SystemInstruction, prompt.UserMessage,
            Temperature: settings.GenerationTemperature,
            MaxOutputTokens: settings.GenerationMaxOutputTokens,
            ResponseSchema: Schema), cancellationToken);

        return new ModelOutput(reply.Text, reply.FinishReason, reply.Usage);
    }
}
