namespace AdvancedRag.Pipelines.Gpo;

internal record ParsedRagResponse(bool HasEvidence, string Answer);

/// <summary>
/// Parses the "EVIDENCE: YES/NO" + "ANSWER: ..." format requested in
/// <see cref="RagPromptBuilder.SystemPrompt"/>, falling back gracefully if the
/// model didn't follow it exactly.
/// </summary>
internal static class RagResponseParser
{
    public static ParsedRagResponse Parse(string rawResponse)
    {
        var lines = rawResponse.Split('\n');
        var answerStartIndex = Array.FindIndex(lines, l => l.TrimStart().StartsWith("ANSWER:", StringComparison.OrdinalIgnoreCase));

        if (answerStartIndex < 0)
        {
            // The model didn't follow the format (or the reply was empty/blocked). Fail
            // closed: never present an unverified reply as a grounded answer with sources.
            return new ParsedRagResponse(HasEvidence: false, Answer: string.Empty);
        }

        var evidenceLine = lines.Take(answerStartIndex)
            .FirstOrDefault(l => l.TrimStart().StartsWith("EVIDENCE:", StringComparison.OrdinalIgnoreCase));
        var hasEvidence = evidenceLine is not null &&
            evidenceLine.TrimStart()["EVIDENCE:".Length..].Trim()
                .StartsWith("YES", StringComparison.OrdinalIgnoreCase);

        var answer = string.Join(
            '\n',
            [lines[answerStartIndex][(lines[answerStartIndex].IndexOf("ANSWER:", StringComparison.OrdinalIgnoreCase) + "ANSWER:".Length)..],
                .. lines.Skip(answerStartIndex + 1)]);

        return new ParsedRagResponse(hasEvidence, answer.Trim());
    }
}
