using System.Diagnostics;
using System.Globalization;
using AdvancedRag.Core.Ai;
using AdvancedRag.Core.Rag;
using AdvancedRag.Pipelines.Gep.Retrieval;

namespace AdvancedRag.Pipelines.Gep.Answering;

/// <summary>
/// The RAG flow for one question:
///   retrieve → judge the evidence → (only if there is evidence) prompt the model → validate → cite.
/// Returns the unified <see cref="AskResult"/>, including the step-by-step trace the UI shows.
/// </summary>
public sealed class ChatService(
    RetrievalService retrieval,
    GepAnswerGenerator generator,
    TableNotesLookup tableNotes,
    IOptions<GepOptions> options,
    ILogger<ChatService> logger)
{
    public const string DocumentId = GepPipeline.Id;

    public async Task<AskResult> AskAsync(string question, CancellationToken cancellationToken)
    {
        var settings = options.Value.Answering;
        question = question.Trim();

        var total = Stopwatch.StartNew();
        var clock = Stopwatch.StartNew();
        var found = await retrieval.SearchAsync(new RetrievalRequest(question), cancellationToken);
        var retrievalMs = clock.ElapsedMilliseconds;

        clock.Restart();
        var evidence = tableNotes.Attach(GroundedPromptBuilder.SelectEvidence(found, settings), settings.MaxTableNotesCharacters);
        var (status, reason) = AssessEvidence(found, evidence, settings);
        var assessMs = clock.ElapsedMilliseconds;

        var steps = new List<PipelineStep>
        {
            AnalysisStep(found),
            RetrievalStep(found, retrievalMs),
            new("Evidence assessment",
                "Judges the evidence from retrieval scores alone: how many chunks passed the threshold and, for comparisons, whether every region is covered.",
                assessMs, new Dictionary<string, string>
                {
                    ["status"] = status.ToString(),
                    ["reason"] = reason,
                    ["chunksAboveThreshold"] = found.Results.Count(r => r.AboveThreshold).ToString(CultureInfo.InvariantCulture),
                    ["chunksSelectedForPrompt"] = evidence.Count.ToString(CultureInfo.InvariantCulture),
                    ["tablesWithNotesAttached"] = evidence.Count(e => e.TableNotes is not null).ToString(CultureInfo.InvariantCulture),
                }),
        };

        if (status == EvidenceStatus.Insufficient)
        {
            // Nothing relevant was retrieved: answer without calling the model (saves quota, and the
            // model cannot be tempted to answer from general knowledge).
            logger.LogInformation("No relevant evidence for question ({Length} chars); model not called", question.Length);
            steps.Add(Skipped("Prompt assembly", reason));
            steps.Add(Skipped("Generation", reason));
            steps.Add(Skipped("Validation", reason));
            return ToResult(question, settings.InsufficientEvidenceMessage, status, [], found, evidence, [], steps, total.ElapsedMilliseconds,
                prompt: null, extra: Extra(found, reason, null, null, []));
        }

        clock.Restart();
        var prompt = GroundedPromptBuilder.Build(question, found.Analysis, evidence);
        var contextCharacters = evidence.Sum(e => e.Result.Chunk.Text.Length + (e.TableNotes?.Length ?? 0));
        steps.Add(new("Prompt assembly",
            "Labels each selected chunk S1, S2, … with its location and wraps it in <source> tags next to the grounding rules.",
            clock.ElapsedMilliseconds, new Dictionary<string, string>
            {
                ["sources"] = evidence.Count.ToString(CultureInfo.InvariantCulture),
                ["contextCharacters"] = contextCharacters.ToString(CultureInfo.InvariantCulture),
                ["promptCharacters"] = (prompt.SystemInstruction.Length + prompt.UserMessage.Length).ToString(CultureInfo.InvariantCulture),
                ["maxContextCharacters"] = settings.MaxContextCharacters.ToString(CultureInfo.InvariantCulture),
            }));

        clock.Restart();
        var output = await generator.GenerateAsync(prompt, cancellationToken);
        steps.Add(new("Generation",
            "Gemini answers from the supplied excerpts only, as JSON: status, answer with [S#] markers, cited source ids.",
            clock.ElapsedMilliseconds, new Dictionary<string, string>
            {
                ["model"] = generator.ModelId,
                ["finishReason"] = output.FinishReason ?? "",
                ["promptTokens"] = output.Usage?.PromptTokens?.ToString(CultureInfo.InvariantCulture) ?? "",
                ["outputTokens"] = output.Usage?.OutputTokens?.ToString(CultureInfo.InvariantCulture) ?? "",
            }));

        clock.Restart();
        var validated = AnswerValidator.Validate(output, evidence.Select(e => e.SourceId).ToList());

        var answer = validated.Text;
        string? explanation = null;
        if (!validated.Answered)
        {
            // The model may lower the status (it found no support in the excerpts), never raise it.
            status = EvidenceStatus.Insufficient;
            reason = $"{reason} The model then reported that the excerpts do not support an answer.";
            explanation = validated.Text;
            answer = settings.InsufficientEvidenceMessage;
        }

        steps.Add(new("Validation",
            "Checks the reply is the JSON we asked for, drops citations to sources that were not supplied, and refuses an answer with no valid citation.",
            clock.ElapsedMilliseconds, new Dictionary<string, string>
            {
                ["modelStatus"] = validated.Answered ? AnswerValidator.Answered : AnswerValidator.InsufficientEvidence,
                ["citedSources"] = string.Join(", ", validated.CitedSourceIds),
                ["warnings"] = validated.Warnings.Count == 0 ? "none" : string.Join(" | ", validated.Warnings),
            }));

        return ToResult(question, answer, status, validated.CitedSourceIds, found, evidence, validated.CitedSourceIds, steps,
            total.ElapsedMilliseconds, prompt, Extra(found, reason, output, explanation, validated.Warnings));
    }

    /// <summary>
    /// Evidence quality from retrieval only: how many chunks passed the similarity threshold and,
    /// for comparisons, whether every compared region has at least one.
    /// </summary>
    public static (EvidenceStatus Status, string Reason) AssessEvidence(
        RetrievalResult found, IReadOnlyList<EvidenceSource> evidence, AnsweringOptions settings)
    {
        if (evidence.Count == 0)
        {
            return (EvidenceStatus.Insufficient,
                $"No chunk reached the minimum similarity of {found.Threshold:0.00}.");
        }

        var best = evidence.Max(e => e.Result.Similarity);
        if (found.Analysis.IsComparison)
        {
            var missing = found.Analysis.Regions
                .Where(region => evidence.All(e => e.Result.Chunk.Region != region))
                .ToList();
            if (missing.Count > 0)
            {
                return (EvidenceStatus.Limited,
                    $"No relevant chunk was found for {string.Join(" and ", missing)}, so only part of the comparison is supported.");
            }
        }

        if (evidence.Count < settings.MinRelevantChunksForSufficient)
        {
            return (EvidenceStatus.Limited,
                $"Only {evidence.Count} chunk passed the similarity threshold of {found.Threshold:0.00} (best {best:0.000}).");
        }

        return (EvidenceStatus.Sufficient,
            $"{evidence.Count} chunks passed the similarity threshold of {found.Threshold:0.00} (best {best:0.000}).");
    }

    // ---- mapping to the unified contract ----

    private AskResult ToResult(string question, string answer, EvidenceStatus status, IReadOnlyList<string> citedIds,
        RetrievalResult found, IReadOnlyList<EvidenceSource> evidence, IReadOnlyList<string> cited,
        List<PipelineStep> steps, long totalMs, AnswerPrompt? prompt, Dictionary<string, string> extra)
    {
        var settings = options.Value.Answering;
        var bySourceId = evidence.ToDictionary(e => e.SourceId);
        var citations = citedIds.Select(id => ToCitation(bySourceId[id], settings.SourceExcerptLength)).ToList();

        var sourceIds = evidence.ToDictionary(e => e.Result.Chunk.Id, e => e.SourceId);
        var denseRanks = found.Results
            .OrderByDescending(r => r.Similarity)
            .Select((r, i) => (r.Chunk.Id, Rank: i + 1))
            .ToDictionary(x => x.Id, x => x.Rank);
        var passages = found.Results.Select(r =>
        {
            var sourceId = sourceIds.GetValueOrDefault(r.Chunk.Id);
            var notes = new List<string> { $"Raw cosine similarity {r.Similarity:0.0000} (threshold {found.Threshold:0.00}, {(r.AboveThreshold ? "above" : "below")})" };
            notes.AddRange(r.Adjustments.Select(a => $"{a.Reason}: {(a.Delta >= 0 ? "+" : "")}{a.Delta:0.000}"));
            if (r.RetrievedForRegion is not null) notes.Add($"Retrieved separately for {r.RetrievedForRegion} (comparison)");
            if (sourceId is not null) notes.Add(cited.Contains(sourceId) ? $"Sent to the model as {sourceId} and cited" : $"Sent to the model as {sourceId}");
            return new RankedPassage(sourceId ?? $"#{r.Rank}", r.Chunk.Id, Math.Round(r.FinalScore, 4),
                denseRanks[r.Chunk.Id], KeywordRank: null, notes);
        }).ToList();

        var debug = new AskDebug(
            "Structure-aware report pipeline", Mode: null, steps, passages,
            generator.ModelId, prompt?.SystemInstruction, prompt?.UserMessage, totalMs, extra);

        return new AskResult(DocumentId, question, answer, status.ToString(), status != EvidenceStatus.Insufficient,
            citations, debug);
    }

    private static Citation ToCitation(EvidenceSource source, int excerptLength)
    {
        var c = source.Result.Chunk;
        var excerpt = c.Text.Length > excerptLength ? c.Text[..excerptLength].TrimEnd() + "…" : c.Text;
        var details = new Dictionary<string, string>
        {
            ["chapter"] = $"{(c.ChapterNumber is { } n ? $"Chapter {n}: " : "")}{c.ChapterTitle}",
            ["pdfPages"] = c.PdfPageNumberStart == c.PdfPageNumberEnd ? $"{c.PdfPageNumberStart}" : $"{c.PdfPageNumberStart}–{c.PdfPageNumberEnd}",
            ["similarity"] = source.Result.Similarity.ToString("0.000", CultureInfo.InvariantCulture),
        };
        if (c.Region is not null) details["region"] = c.Region;

        string? printed = c.PrintedPageStart is null ? null
            : c.PrintedPageEnd is null || c.PrintedPageEnd == c.PrintedPageStart ? c.PrintedPageStart : $"{c.PrintedPageStart}–{c.PrintedPageEnd}";
        return new Citation(source.SourceId, c.Id, c.PdfPageNumberStart, printed, c.HeadingPath, c.ContentType.ToString(),
            excerpt, Math.Round(source.Result.FinalScore, 4), details);
    }

    private static PipelineStep AnalysisStep(RetrievalResult found)
    {
        var a = found.Analysis;
        return new("Question analysis",
            "Reads what the question states about the report's structure (chapter, regions, countries, comparison, executive summary). Meaning is still matched by embeddings.",
            found.AnalysisMilliseconds, new Dictionary<string, string>
            {
                ["explicitChapter"] = a.ExplicitChapter?.ToString(CultureInfo.InvariantCulture) ?? "none",
                ["regions"] = a.Regions.Count == 0 ? "none" : string.Join("; ", a.Regions),
                ["regionsImpliedByCountries"] = a.RegionsImpliedByCountries.Count == 0 ? "none" : string.Join("; ", a.RegionsImpliedByCountries),
                ["countriesMentioned"] = a.CountriesMentioned.Count == 0 ? "none" : string.Join(", ", a.CountriesMentioned),
                ["isComparison"] = a.IsComparison.ToString(),
                ["asksForExecutiveSummary"] = a.AsksForExecutiveSummary.ToString(),
                ["asksForReferenceMaterial"] = a.AsksForReferenceMaterial.ToString(),
                ["keyTerms"] = string.Join(", ", a.KeyTerms),
            });
    }

    private PipelineStep RetrievalStep(RetrievalResult found, long retrievalMs) => new("Retrieval",
        "Embeds the question, filters candidates, scores them by pgvector cosine similarity plus small explained adjustments (region, heading, low-value content, BM25 keywords), and retrieves each region separately for comparisons.",
        retrievalMs, new Dictionary<string, string>
        {
            ["embeddingModel"] = found.EmbeddingModel,
            ["questionEmbeddingMs"] = found.EmbeddingMilliseconds.ToString(CultureInfo.InvariantCulture),
            ["appliedFilters"] = found.AppliedFilters.Count == 0 ? "none" : string.Join(" | ", found.AppliedFilters),
            ["candidates"] = found.CandidateCount.ToString(CultureInfo.InvariantCulture),
            ["keywordMatches"] = found.KeywordMatchCount.ToString(CultureInfo.InvariantCulture),
            ["retrieved"] = found.Results.Count.ToString(CultureInfo.InvariantCulture),
            ["threshold"] = found.Threshold.ToString("0.00", CultureInfo.InvariantCulture),
            ["notes"] = found.Notes.Count == 0 ? "none" : string.Join(" | ", found.Notes),
        });

    private static PipelineStep Skipped(string name, string reason) => new(name,
        "Skipped: no relevant evidence, so the model is not called.", null, new Dictionary<string, string> { ["skippedReason"] = reason });

    private Dictionary<string, string> Extra(RetrievalResult found, string reason, ModelOutput? output, string? explanation,
        IReadOnlyList<string> warnings)
    {
        var extra = new Dictionary<string, string>
        {
            ["evidenceReason"] = reason,
            ["embeddingModel"] = found.EmbeddingModel,
            ["relevanceThreshold"] = found.Threshold.ToString("0.00", CultureInfo.InvariantCulture),
            ["modelCalled"] = (output is not null).ToString(),
        };
        if (output is not null)
        {
            extra["finishReason"] = output.FinishReason ?? "";
            extra["promptTokens"] = output.Usage?.PromptTokens?.ToString(CultureInfo.InvariantCulture) ?? "";
            extra["outputTokens"] = output.Usage?.OutputTokens?.ToString(CultureInfo.InvariantCulture) ?? "";
            extra["thoughtTokens"] = output.Usage?.ThoughtTokens?.ToString(CultureInfo.InvariantCulture) ?? "";
            extra["totalTokens"] = output.Usage?.TotalTokens?.ToString(CultureInfo.InvariantCulture) ?? "";
        }
        if (explanation is not null) extra["modelExplanation"] = explanation;
        if (warnings.Count > 0) extra["validationWarnings"] = string.Join(" | ", warnings);
        return extra;
    }
}
