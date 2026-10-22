using System.Text.Json;
using AdvancedRag.Core.Ai;
using System.Text.RegularExpressions;

namespace AdvancedRag.Pipelines.Gep.Answering;

/// <summary>
/// Never trust model output blindly. This checks the reply is the JSON we asked for, removes
/// citations to sources that were not supplied, and refuses answers that cite nothing.
/// </summary>
public static partial class AnswerValidator
{
    public const string Answered = "answered";
    public const string InsufficientEvidence = "insufficient_evidence";

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static ValidatedAnswer Validate(ModelOutput output, IReadOnlyCollection<string> suppliedIds)
    {
        switch (output.FinishReason?.ToUpperInvariant())
        {
            case null or "" or "STOP":
                break;
            case "MAX_TOKENS":
                throw new GeminiApiException(GeminiErrorKind.InvalidResponse,
                    "The model's answer was cut off (output token limit reached). Try a narrower question.");
            default:
                throw new GeminiApiException(GeminiErrorKind.InvalidResponse,
                    $"The model stopped without a complete answer (reason: {output.FinishReason}).");
        }

        var parsed = Parse(output.Text);
        var status = parsed.Status?.Trim().ToLowerInvariant();
        if (status is not (Answered or InsufficientEvidence))
        {
            throw new GeminiApiException(GeminiErrorKind.InvalidResponse, $"The model returned an unknown status '{parsed.Status}'.");
        }

        var warnings = new List<string>();
        var known = new HashSet<string>(suppliedIds, StringComparer.OrdinalIgnoreCase);
        var cited = new List<string>();

        // Keep citations to supplied sources; drop the rest (and say so in the debug output).
        var text = CitationGroup().Replace(parsed.Answer ?? string.Empty, match =>
        {
            var ids = match.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var valid = new List<string>();
            foreach (var id in ids.Select(i => i.ToUpperInvariant()))
            {
                if (known.Contains(id)) { valid.Add(id); if (!cited.Contains(id)) cited.Add(id); }
                else warnings.Add($"Removed a citation to {id}, which was not one of the supplied sources.");
            }
            return string.Concat(valid.Select(id => $"[{id}]"));
        }).Trim();

        foreach (var id in (parsed.CitedSourceIds ?? []).Select(i => i.Trim().ToUpperInvariant()))
        {
            if (!known.Contains(id)) warnings.Add($"Ignored {id} in citedSourceIds: not a supplied source.");
            else if (!cited.Contains(id)) warnings.Add($"{id} was listed in citedSourceIds but not cited in the answer text.");
        }

        if (status == InsufficientEvidence)
        {
            return new ValidatedAnswer(false, text, [], warnings);
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new GeminiApiException(GeminiErrorKind.InvalidResponse, "The model said it answered but returned no text.");
        }

        if (cited.Count == 0)
        {
            // An answer with no valid citation cannot be checked against the report: treat it as unsupported.
            warnings.Add("The answer cited no supplied source, so it was treated as insufficient evidence.");
            return new ValidatedAnswer(false, text, [], warnings);
        }

        return new ValidatedAnswer(true, text, cited, warnings);
    }

    private static ModelAnswer Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new GeminiApiException(GeminiErrorKind.InvalidResponse, "The model returned an empty reply.");
        }

        // JSON mode should give bare JSON; tolerate a ```json fence just in case.
        var json = text.Trim();
        if (json.StartsWith("```"))
        {
            json = json[(json.IndexOf('\n') + 1)..];
            if (json.EndsWith("```")) json = json[..^3];
        }

        try
        {
            return JsonSerializer.Deserialize<ModelAnswer>(json, Json)
                   ?? throw new GeminiApiException(GeminiErrorKind.InvalidResponse, "The model returned an empty JSON value.");
        }
        catch (JsonException ex)
        {
            throw new GeminiApiException(GeminiErrorKind.InvalidResponse, "The model's reply was not the requested JSON.", inner: ex);
        }
    }

    /// <summary>[S1] or [S1, S3].</summary>
    [GeneratedRegex(@"\[\s*(S\d+(?:\s*,\s*S\d+)*)\s*\]", RegexOptions.IgnoreCase)]
    private static partial Regex CitationGroup();
}
