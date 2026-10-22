using AdvancedRag.Pipelines.Gep.Answering;

namespace AdvancedRag.Pipelines.Gep.Models;

// ---- eval/gep-cases.json ----

public sealed record EvaluationCaseFile(string? Description, IReadOnlyList<EvaluationCase> Cases);

public sealed record EvaluationCase(string Id, string Question, EvaluationExpectations Expect, int? SpecTest = null, string? Note = null);

/// <summary>
/// What a good answer must look like structurally. Nothing here contains expected answer text,
/// so a case cannot pass because a "right answer" was written into the test.
/// </summary>
public sealed record EvaluationExpectations(
    IReadOnlyList<EvidenceStatus> EvidenceStatus,
    bool Citations,
    int? Chapter = null,
    ContentType? ContentType = null,
    IReadOnlyList<string>? Regions = null,
    bool SeparateRegions = false,
    bool GroundedNumbers = false,
    bool InsufficientReply = false,
    IReadOnlyList<string>? ForbiddenPhrases = null);

public sealed record EvaluationCheck(string Name, bool Passed, string Detail);
