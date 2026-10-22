using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AdvancedRag.Core.Rag;
using AdvancedRag.Core.Storage;
using AdvancedRag.Pipelines.Gep.Answering;
using AdvancedRag.Pipelines.Gep.Models;
using AdvancedRag.Pipelines.Gep.Retrieval;

namespace AdvancedRag.Pipelines.Gep.Evaluation;

/// <summary>
/// Asks every evaluation question through the real pipeline (retrieval → Gemini →
/// validation) and checks the RESULT against structural rules: where the cited evidence
/// comes from, whether regions stay apart, whether every number in the answer exists in the cited text.
/// No expected answer text is stored anywhere, so a pass cannot be hardcoded.
/// </summary>
public sealed partial class EvaluationService(
    ChatService chat,
    GepVectorStore store,
    TableNotesLookup tableNotes,
    GepAnswerGenerator generator,
    IOptions<GepOptions> options,
    StoragePaths paths,
    ILogger<EvaluationService> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly SemaphoreSlim _runLock = new(1, 1);

    /// <summary>A cited chunk with the metadata the checks need (looked up from the index by chunk id).</summary>
    private sealed record CitedSource(string SourceId, string ChunkId, int? ChapterNumber, string ChapterTitle, string SectionTitle,
        string? SubsectionTitle, string? Region, ContentType ContentType, int PageStart, string? PrintedPageStart);

    /// <summary>One evaluated question, before it is mapped to the unified report.</summary>
    private sealed record CaseOutcome(EvaluationCase Case, int Attempt, bool Passed, IReadOnlyList<EvaluationCheck> Checks,
        AskResult? Result, IReadOnlyList<string> CitedLocations, string? Error);

    public async Task<IReadOnlyList<EvaluationCase>> LoadCasesAsync(CancellationToken cancellationToken)
    {
        var path = paths.Eval(options.Value.Evaluation.CasesFile);
        if (!File.Exists(path)) throw new InvalidDataException($"The evaluation cases file was not found: {path}");
        await using var stream = File.OpenRead(path);
        var file = await JsonSerializer.DeserializeAsync<EvaluationCaseFile>(stream, Json, cancellationToken)
                   ?? throw new InvalidDataException($"No cases in {path}.");
        return file.Cases;
    }

    /// <summary>Runs the cases (optionally one case, optionally several times).</summary>
    public async Task<EvalReport> RunAsync(string? caseId, int repeat, CancellationToken cancellationToken)
    {
        if (!await _runLock.WaitAsync(0, cancellationToken))
        {
            throw new ConflictException("An evaluation of 'gep' is already running.");
        }
        try
        {
            var settings = options.Value.Evaluation;
            repeat = Math.Clamp(repeat, 1, settings.MaxRepeat);
            var cases = (await LoadCasesAsync(cancellationToken))
                .Where(c => caseId is null || c.Id.Equals(caseId, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (cases.Count == 0) throw new BadRequestException($"No evaluation case with id '{caseId}'.");

            var started = DateTimeOffset.UtcNow;
            var clock = Stopwatch.StartNew();
            var outcomes = new List<CaseOutcome>();
            for (var attempt = 1; attempt <= repeat; attempt++)
            {
                foreach (var testCase in cases)
                {
                    if (outcomes.Count > 0) await Task.Delay(settings.DelayBetweenCasesMilliseconds, cancellationToken);
                    var outcome = await RunCaseAsync(testCase, attempt, cancellationToken);
                    logger.LogInformation("Evaluation {Case} (attempt {Attempt}): {Outcome}", testCase.Id, attempt, outcome.Passed ? "PASS" : "FAIL");
                    outcomes.Add(outcome);
                }
            }

            var passed = outcomes.Count(o => o.Passed);
            var report = new EvalReport(
                "gep", Mode: null, started, passed, outcomes.Count,
                outcomes.Select(o => ToCaseResult(o, repeat)).ToList(),
                new Dictionary<string, string>
                {
                    ["generationModel"] = generator.ModelId,
                    ["embeddingModel"] = store.ModelId ?? "(index not loaded)",
                    ["relevanceThreshold"] = options.Value.Retrieval.MinRelevanceScore.ToString("0.00", CultureInfo.InvariantCulture),
                    ["repeat"] = repeat.ToString(CultureInfo.InvariantCulture),
                    ["caseRuns"] = outcomes.Count.ToString(CultureInfo.InvariantCulture),
                    ["failed"] = (outcomes.Count - passed).ToString(CultureInfo.InvariantCulture),
                    ["durationSeconds"] = clock.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture),
                });
            await SaveAsync(report, cancellationToken);
            return report;
        }
        finally
        {
            _runLock.Release();
        }
    }

    public async Task<EvalReport?> LoadLatestAsync(CancellationToken cancellationToken)
    {
        var path = ResultsPath();
        if (!File.Exists(path)) return null;
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<EvalReport>(stream, Json, cancellationToken);
    }

    private async Task<CaseOutcome> RunCaseAsync(EvaluationCase testCase, int attempt, CancellationToken cancellationToken)
    {
        AskResult response;
        try
        {
            response = await chat.AskAsync(testCase.Question, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A Gemini outage or quota error is a failed run, reported honestly — not skipped.
            return new CaseOutcome(testCase, attempt, false, [], null, [], $"{ex.GetType().Name}: {ex.Message}");
        }

        var sources = CitedSources(response);
        var checks = Check(testCase.Expect, response, sources);
        return new CaseOutcome(testCase, attempt, checks.All(c => c.Passed), checks, response,
            sources.Select(s => $"{s.SourceId}: {Describe(s)}").ToList(), null);
    }

    private static EvalCaseResult ToCaseResult(CaseOutcome o, int repeat)
    {
        var metrics = new Dictionary<string, string>();
        if (o.Case.SpecTest is { } spec) metrics["specTest"] = spec.ToString(CultureInfo.InvariantCulture);
        metrics["attempt"] = o.Attempt.ToString(CultureInfo.InvariantCulture);
        if (o.Result is { } result)
        {
            var steps = result.Debug.Steps;
            metrics["evidenceStatus"] = result.EvidenceStatus;
            metrics["checks"] = $"{o.Checks.Count(c => c.Passed)}/{o.Checks.Count} passed";
            metrics["checkDetails"] = string.Join("\n", o.Checks.Select(c => $"{(c.Passed ? "PASS" : "FAIL")} {c.Name}: {c.Detail}"));
            metrics["citedSources"] = string.Join("\n", o.CitedLocations);
            metrics["totalMs"] = result.Debug.TotalMs.ToString(CultureInfo.InvariantCulture);
            metrics["retrievalMs"] = (steps.FirstOrDefault(s => s.Name == "Retrieval")?.DurationMs ?? 0).ToString(CultureInfo.InvariantCulture);
            metrics["generationMs"] = (steps.FirstOrDefault(s => s.Name == "Generation")?.DurationMs ?? 0).ToString(CultureInfo.InvariantCulture);
            if (result.Debug.Extra.TryGetValue("promptTokens", out var prompt)) metrics["promptTokens"] = prompt;
            if (result.Debug.Extra.TryGetValue("outputTokens", out var output)) metrics["outputTokens"] = output;
        }

        var failures = o.Error is not null
            ? [o.Error]
            : o.Checks.Where(c => !c.Passed).Select(c => $"{c.Name}: {c.Detail}").ToList();
        var id = repeat > 1 ? $"{o.Case.Id}#{o.Attempt}" : o.Case.Id;
        return new EvalCaseResult(id, o.Case.Question, o.Passed, o.Result?.Answer, failures, metrics);
    }

    /// <summary>The cited sources with chapter, region and content type, looked up from the chunk index.</summary>
    private List<CitedSource> CitedSources(AskResult response)
    {
        var chunks = store.Chunks.ToDictionary(c => c.Id);
        return response.Citations
            .Where(c => chunks.ContainsKey(c.ChunkId))
            .Select(c =>
            {
                var chunk = chunks[c.ChunkId];
                return new CitedSource(c.Label, chunk.Id, chunk.ChapterNumber, chunk.ChapterTitle, chunk.SectionTitle,
                    chunk.SubsectionTitle, chunk.Region, chunk.ContentType, chunk.PdfPageNumberStart, chunk.PrintedPageStart);
            }).ToList();
    }

    private List<EvaluationCheck> Check(EvaluationExpectations expect, AskResult response, IReadOnlyList<CitedSource> sources)
    {
        var checks = new List<EvaluationCheck>();

        checks.Add(new("Evidence status", expect.EvidenceStatus.Any(s => s.ToString() == response.EvidenceStatus),
            $"got {response.EvidenceStatus}, allowed {string.Join(" / ", expect.EvidenceStatus)}. {response.Debug.Extra.GetValueOrDefault("evidenceReason")}"));

        if (expect.Citations)
        {
            var markers = CitationMarker().Matches(response.Answer).Count;
            checks.Add(new("Has citations", sources.Count > 0 && markers > 0,
                $"{markers} citation marker(s) in the answer, {sources.Count} cited source(s)"));
        }
        else
        {
            checks.Add(new("No citations", sources.Count == 0, $"{sources.Count} cited source(s)"));
        }

        if (expect.InsufficientReply)
        {
            var message = options.Value.Answering.InsufficientEvidenceMessage;
            checks.Add(new("Insufficient-evidence reply", response.Answer == message,
                response.Answer == message ? "the fixed reply was returned" : $"got: {Shorten(response.Answer)}"));
        }

        if (expect.Chapter is { } chapter)
        {
            var wrong = sources.Where(s => s.ChapterNumber != chapter).ToList();
            checks.Add(new($"Sources from Chapter {chapter} only", wrong.Count == 0,
                wrong.Count == 0 ? $"all {sources.Count} cited source(s) are in Chapter {chapter}"
                    : $"outside Chapter {chapter}: {string.Join("; ", wrong.Select(Describe))}"));
        }

        if (expect.ContentType is { } type)
        {
            var wrong = sources.Where(s => s.ContentType != type).ToList();
            checks.Add(new($"Sources are {type} only", wrong.Count == 0,
                wrong.Count == 0 ? $"all {sources.Count} cited source(s) are {type}"
                    : $"other content: {string.Join("; ", wrong.Select(Describe))}"));
        }

        if (expect.Regions is { Count: > 0 } regions)
        {
            var foreign = sources.Where(s => s.Region is not null && !regions.Contains(s.Region)).ToList();
            checks.Add(new("No sources from other regions", foreign.Count == 0,
                foreign.Count == 0 ? "every region-specific source matches the question"
                    : $"other regions cited: {string.Join("; ", foreign.Select(Describe))}"));

            var missing = regions.Where(r => sources.All(s => s.Region != r)).ToList();
            checks.Add(new("Every asked region is cited", missing.Count == 0,
                missing.Count == 0 ? $"cited: {string.Join(", ", regions)}" : $"no cited source for {string.Join(", ", missing)}"));
        }

        if (expect.SeparateRegions && expect.Regions is { Count: > 1 })
        {
            checks.Add(CheckRegionSeparation(response.Answer, expect.Regions, sources));
        }

        if (expect.GroundedNumbers)
        {
            checks.Add(CheckNumbers(response.Answer, sources));
        }

        foreach (var phrase in expect.ForbiddenPhrases ?? [])
        {
            var found = response.Answer.Contains(phrase, StringComparison.OrdinalIgnoreCase);
            checks.Add(new($"Does not say \"{phrase}\"", !found, found ? $"answer contains \"{phrase}\"" : "not present"));
        }

        return checks;
    }

    /// <summary>
    /// The answer must have one part per region ("**South Asia:** ..."), and each sentence in a
    /// region's part may only cite that region's sources (or region-neutral ones). The one exception
    /// is a comparison sentence ("Overall, growth is higher in South Asia ... [S3][S4]"), which the
    /// prompt allows: it cites sources from every compared region, so it is not transferring one
    /// region's statistics to another.
    /// </summary>
    private static EvaluationCheck CheckRegionSeparation(string answer, IReadOnlyList<string> regions, IReadOnlyList<CitedSource> sources)
    {
        var labels = BoldLabel().Matches(answer)
            .Select(m => (m.Index, Region: regions.FirstOrDefault(r => m.Groups[1].Value.Contains(r, StringComparison.OrdinalIgnoreCase))))
            .Where(l => l.Region is not null)
            .ToList();

        var missing = regions.Where(r => labels.All(l => l.Region != r)).ToList();
        if (missing.Count > 0)
        {
            return new("Regions answered separately", false, $"no separate part for {string.Join(", ", missing)}");
        }

        var byId = sources.ToDictionary(s => s.SourceId);
        var problems = new List<string>();
        var comparisonSentences = 0;
        for (var i = 0; i < labels.Count; i++)
        {
            var end = i + 1 < labels.Count ? labels[i + 1].Index : answer.Length;
            var part = answer[labels[i].Index..end];
            foreach (var sentence in SentenceBreak().Split(part))
            {
                var cited = CitationMarker().Matches(sentence)
                    .Select(m => byId.GetValueOrDefault(m.Groups[1].Value))
                    .Where(s => s?.Region is not null)
                    .Select(s => s!)
                    .ToList();
                var citedRegions = cited.Select(s => s.Region).Distinct().ToList();
                if (regions.All(citedRegions.Contains)) { comparisonSentences++; continue; } // a comparison sentence

                foreach (var s in cited.Where(s => s.Region != labels[i].Region))
                {
                    problems.Add($"{labels[i].Region} part cites {s.SourceId} ({s.Region}) in: \"{Shorten(sentence.Trim())}\"");
                }
            }
        }
        return new("Regions answered separately", problems.Count == 0,
            problems.Count == 0
                ? $"one part per region; each cites only its own region's sources ({comparisonSentences} comparison sentence(s) cite both)"
                : string.Join("; ", problems));
    }

    /// <summary>
    /// Hallucination check for numbers: every figure in the answer (decimals, and whole numbers ≥ 13,
    /// which covers years and most statistics) must appear in the full text of a cited chunk or its
    /// table notes. "4.0" matches "4"; signs are ignored ("revised down 0.8" matches "-0.8").
    /// </summary>
    private EvaluationCheck CheckNumbers(string answer, IReadOnlyList<CitedSource> sources)
    {
        var chunks = store.Chunks.ToDictionary(c => c.Id);
        var evidence = new HashSet<decimal>();
        foreach (var source in sources)
        {
            if (!chunks.TryGetValue(source.ChunkId, out var chunk)) continue;
            var text = chunk.Text + "\n" + string.Join("\n", tableNotes.NotesFor(chunk).Select(n => n.Text));
            foreach (var n in Numbers(text)) evidence.Add(n);
        }

        // "FY2026/27" is a period label the model may derive from a table note ("the column for 2023
        // refers to FY2023/24"); it is checked by its first year, the "/27" part is not a statistic.
        var answerText = FiscalYearSuffix().Replace(CitationMarker().Replace(answer, " "), "$1");
        var claimed = Numbers(answerText)
            .Where(n => n != Math.Truncate(n) || n >= 13)
            .Distinct()
            .ToList();
        var unsupported = claimed.Where(n => !evidence.Contains(n)).ToList();

        return new("Numbers appear in cited text", unsupported.Count == 0,
            unsupported.Count == 0
                ? $"all {claimed.Count} figure(s) found in the cited sources"
                : $"not found in cited sources: {string.Join(", ", unsupported.Select(n => n.ToString(CultureInfo.InvariantCulture)))}");
    }

    private static IEnumerable<decimal> Numbers(string text) =>
        NumberToken().Matches(text.Replace("FY", "FY ", StringComparison.Ordinal)) // "FY2026/27" → 2026, 27
            .Select(m => decimal.TryParse(m.Value.Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? (decimal?)Math.Abs(d) : null)
            .Where(d => d.HasValue)
            .Select(d => d!.Value / 1.000000000000000000000000000000000m); // 4.0 == 4

    private static string Describe(CitedSource s) =>
        $"{(s.ChapterNumber is { } n ? $"Chapter {n}" : s.ChapterTitle)} › {s.SectionTitle}{(s.SubsectionTitle is null ? "" : $" › {s.SubsectionTitle}")}"
        + $" [{s.ContentType}{(s.Region is null ? "" : $", {s.Region}")}] p. {s.PrintedPageStart ?? $"PDF {s.PageStart}"}";

    private static string Shorten(string text) => text.Length > 160 ? text[..160] + "…" : text;

    private async Task SaveAsync(EvalReport run, CancellationToken cancellationToken)
    {
        var path = ResultsPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        await using (var stream = File.Create(temp))
        {
            await JsonSerializer.SerializeAsync(stream, run, Json, cancellationToken);
        }
        File.Move(temp, path, overwrite: true);
    }

    private string ResultsPath() => Path.Combine(
        paths.EvalResults(), options.Value.Evaluation.ResultsFileName);

    [GeneratedRegex(@"\[(S\d+)\]")]
    private static partial Regex CitationMarker();

    [GeneratedRegex(@"\*\*([^*]+)\*\*")]
    private static partial Regex BoldLabel();

    [GeneratedRegex(@"(FY\s?\d{4})/\d{2}")]
    private static partial Regex FiscalYearSuffix();

    /// <summary>Sentence end: ". " / "! " / "? " or a line break (decimals like "6.3" are not followed by a space).</summary>
    [GeneratedRegex(@"(?<=[.!?])\s+|\n+")]
    private static partial Regex SentenceBreak();

    /// <summary>12, 6.6, 2,084 — but not the digits inside words like "EM7" or "S1".</summary>
    [GeneratedRegex(@"(?<![\p{L}\d.])\d{1,3}(?:,\d{3})+(?:\.\d+)?|(?<![\p{L}\d.])\d+(?:\.\d+)?")]
    private static partial Regex NumberToken();
}
