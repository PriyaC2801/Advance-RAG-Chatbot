using System.Text.Json.Serialization;
using AdvancedRag.Core.Ai;
using AdvancedRag.Pipelines.Gep.Retrieval;

namespace AdvancedRag.Pipelines.Gep.Answering;

/// <summary>
/// How well the RETRIEVED evidence supports an answer. Decided by retrieval scores and coverage,
/// never by how confident the model sounds. The model can only lower it (by declining), never raise it.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EvidenceStatus
{
    /// <summary>Enough relevant excerpts, and every compared region is covered.</summary>
    Sufficient,
    /// <summary>Some relevant evidence, but thin (one excerpt) or one compared region has none.</summary>
    Limited,
    /// <summary>No excerpt passed the relevance threshold, or the model found no support in them.</summary>
    Insufficient,
}

/// <summary>
/// One retrieved chunk as it is shown to the model, labelled "S1", "S2", ...
/// TableNotes: the notes printed under the chunk's table (units, fiscal years), when it is a table.
/// </summary>
public sealed record EvidenceSource(string SourceId, ScoredChunk Result, string? TableNotes = null);

/// <summary>Everything sent to the model for one question.</summary>
public sealed record AnswerPrompt(string SystemInstruction, string UserMessage, IReadOnlyList<EvidenceSource> Sources);

/// <summary>The model's raw reply, before validation.</summary>
public sealed record ModelOutput(string? Text, string? FinishReason, TokenUsage? Usage);

/// <summary>
/// The JSON the model must return (the matching response schema is in <see cref="GepAnswerGenerator"/>), so the
/// model's output has this exact shape.
/// </summary>
public sealed record ModelAnswer(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("answer")] string Answer,
    [property: JsonPropertyName("citedSourceIds")] List<string> CitedSourceIds);

/// <summary>The model's answer after our checks.</summary>
public sealed record ValidatedAnswer(
    bool Answered,
    string Text,
    IReadOnlyList<string> CitedSourceIds,
    IReadOnlyList<string> Warnings);
