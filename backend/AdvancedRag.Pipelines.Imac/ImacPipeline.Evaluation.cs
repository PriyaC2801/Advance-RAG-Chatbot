using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AdvancedRag.Core.Rag;

namespace AdvancedRag.Pipelines.Imac;

public sealed partial class ImacPipeline
{
    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private string QuestionsFile => paths.Eval("imac-questions.json");

    private string ReportFile(string mode) => Path.Combine(paths.EvalResults(), $"latest-{mode}.json");

    public async Task<EvalReport> RunEvaluationAsync(EvalRequest request, CancellationToken cancellationToken)
    {
        var mode = request.Mode ?? Info.DefaultMode!;
        if (!RetrievalModes.IsValid(mode)) throw UnknownMode(mode);
        if (!File.Exists(QuestionsFile)) throw new BadRequestException("The evaluation questions file eval/imac-questions.json was not found.");

        var all = JsonSerializer.Deserialize<List<ImacEvalQuestion>>(await File.ReadAllTextAsync(QuestionsFile, cancellationToken), ReadOptions) ?? [];
        var questions = request.CaseId is null
            ? all
            : all.Where(q => string.Equals(q.Id, request.CaseId, StringComparison.OrdinalIgnoreCase)).ToList();
        if (questions.Count == 0) throw new BadRequestException($"No evaluation case '{request.CaseId}'.");

        Enter("an evaluation");
        try
        {
            var cases = new List<EvalCaseResult>();
            var firstRanks = new List<(ImacEvalQuestion Q, int? Rank)>();

            foreach (var q in questions)
            {
                var outcome = await AskCoreAsync(q.Question, mode, cancellationToken);
                var answer = outcome.Result.Answer;

                // A chunk is "relevant" if it is on an expected page AND contains every must-have string.
                int? rank = null;
                for (var i = 0; i < outcome.Passages.Count && q.ExpectedPages.Length > 0; i++)
                {
                    var c = outcome.Passages[i];
                    if (q.ExpectedPages.Contains(c.Page) && q.MustContain.All(m => Contains(c.Content, m)))
                    {
                        rank = i + 1;
                        break;
                    }
                }

                var answerPass = q.Type == "refusal"
                    ? Contains(answer, ImacPrompts.Refusal)
                    : q.MustContain.All(m => Contains(answer, m));

                var failures = new List<string>();
                if (!answerPass)
                {
                    failures.Add(q.Type == "refusal"
                        ? "Expected the refusal sentence."
                        : "Answer is missing: " + string.Join(", ", q.MustContain.Where(m => !Contains(answer, m))));
                }
                if (q.Type != "refusal" && rank is null) failures.Add("No relevant chunk among the retrieved passages.");

                cases.Add(new EvalCaseResult(q.Id, q.Question, answerPass, answer, failures, new Dictionary<string, string>
                {
                    ["type"] = q.Type,
                    ["firstRelevantRank"] = rank?.ToString(CultureInfo.InvariantCulture) ?? "-",
                    ["retrievedPages"] = string.Join(", ", outcome.Passages.Select(p => p.Page)),
                }));
                firstRanks.Add((q, rank));
                logger.LogInformation("{Id,-4} rank={Rank,-2} answer={Pass}", q.Id, rank?.ToString() ?? "-", answerPass ? "PASS" : "FAIL");
            }

            // Retrieval metrics only count questions that have an expected chunk (refusal has none).
            var scored = firstRanks.Where(r => r.Q.Type != "refusal").ToList();
            double n = Math.Max(1, scored.Count);
            // MRR (mean reciprocal rank): average of 1/rank of the first right chunk (0 if missing).
            // 1.0 = right chunk always first, 0.5 = second on average, 0 = never found.
            var summary = new Dictionary<string, string>
            {
                ["hit@1"] = Fmt(scored.Count(r => r.Rank <= 1) / n),
                ["hit@3"] = Fmt(scored.Count(r => r.Rank <= 3) / n),
                ["hit@5"] = Fmt(scored.Count(r => r.Rank <= 5) / n),
                ["mrr"] = Fmt(scored.Sum(r => r.Rank is int k ? 1.0 / k : 0) / n),
                ["answerPassRate"] = Fmt(cases.Count(c => c.Passed) / (double)cases.Count),
            };

            var passed = cases.Count(c => c.Passed);
            var report = new EvalReport(Info.Id, mode, DateTimeOffset.UtcNow, passed, cases.Count, cases, summary);

            // A single-case run is a spot check, not a report worth replacing the full one.
            if (request.CaseId is null)
            {
                await File.WriteAllTextAsync(ReportFile(mode), JsonSerializer.Serialize(report, WriteOptions), cancellationToken);
            }
            return report;
        }
        finally
        {
            Leave();
        }
    }

    /// <summary>One report per mode that has been run. Reads reports in this pipeline's shape and the original app's shape.</summary>
    public async Task<IReadOnlyList<EvalReport>> GetLatestEvaluationsAsync(CancellationToken cancellationToken)
    {
        var reports = new List<EvalReport>();
        foreach (var mode in RetrievalModes.All)
        {
            var file = ReportFile(mode);
            if (!File.Exists(file)) continue;
            try
            {
                var saved = JsonSerializer.Deserialize<SavedReport>(await File.ReadAllTextAsync(file, cancellationToken), ReadOptions);
                if (saved is not null) reports.Add(saved.ToReport(Info.Id, mode));
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                logger.LogWarning(ex, "Ignoring unreadable evaluation report {File}", file);
            }
        }
        return reports;
    }

    // Reads both this pipeline's EvalReport and the original app's report (Hit1.., Questions[]).
    private sealed class SavedReport
    {
        public string? Mode { get; set; }
        public DateTimeOffset? RanUtc { get; set; }
        public DateTimeOffset? RunAt { get; set; }
        public List<EvalCaseResult>? Cases { get; set; }
        public Dictionary<string, string>? Summary { get; set; }
        public double? Hit1 { get; set; }
        public double? Hit3 { get; set; }
        public double? Hit5 { get; set; }
        public double? Mrr { get; set; }
        public double? AnswerPassRate { get; set; }
        public List<LegacyQuestion>? Questions { get; set; }

        public EvalReport ToReport(string documentId, string fallbackMode)
        {
            var ran = RanUtc ?? RunAt ?? DateTimeOffset.MinValue;
            if (Cases is not null)
            {
                return new EvalReport(documentId, Mode ?? fallbackMode, ran, Cases.Count(c => c.Passed), Cases.Count, Cases, Summary ?? new Dictionary<string, string>());
            }

            var cases = (Questions ?? []).Select(q => new EvalCaseResult(
                q.Id, q.Question, q.AnswerPass, q.Answer,
                q.AnswerPass ? [] : ["Answer check failed."],
                new Dictionary<string, string>
                {
                    ["type"] = q.Type,
                    ["firstRelevantRank"] = q.FirstRelevantRank?.ToString(CultureInfo.InvariantCulture) ?? "-",
                    ["retrievedPages"] = string.Join(", ", q.RetrievedPages ?? []),
                })).ToList();
            var summary = new Dictionary<string, string>
            {
                ["hit@1"] = Fmt(Hit1 ?? 0),
                ["hit@3"] = Fmt(Hit3 ?? 0),
                ["hit@5"] = Fmt(Hit5 ?? 0),
                ["mrr"] = Fmt(Mrr ?? 0),
                ["answerPassRate"] = Fmt(AnswerPassRate ?? 0),
            };
            return new EvalReport(documentId, Mode ?? fallbackMode, ran, cases.Count(c => c.Passed), cases.Count, cases, summary);
        }
    }

    private sealed class LegacyQuestion
    {
        public string Id { get; set; } = "";
        public string Type { get; set; } = "";
        public string Question { get; set; } = "";
        public int? FirstRelevantRank { get; set; }
        public bool AnswerPass { get; set; }
        public string? Answer { get; set; }
        public int[]? RetrievedPages { get; set; }
    }

    private static string Fmt(double value) => value.ToString("0.000", CultureInfo.InvariantCulture);

    // Lower-case, "percent" -> "%", drop spaces and dash variants, so "50 percent" matches "50%".
    private static string Normalize(string s) =>
        Regex.Replace(s.ToLowerInvariant().Replace("percent", "%").Replace('–', '-').Replace('−', '-'), @"[\s ]+", "");

    private static bool Contains(string text, string needle) => Normalize(text).Contains(Normalize(needle));
}
