using System.Text.RegularExpressions;

namespace AdvancedRag.Pipelines.Imac;

/// <summary>Groups the sentences of a text block into chunks of whole sentences (about <c>maxTokens</c> tokens).</summary>
public static class ImacChunker
{
    // Sentence ends at . ! ? (optionally followed by footnote markers like "[^18]") and a space before a capital letter,
    // quote, bracket or digit. "U.S." is protected so it does not split mid-name.
    private static readonly Regex SentenceEnd = new(@"(?<=[.!?](?:\[\^\d+\])*)(?<!\bU\.S\.(?:\[\^\d+\])*)\s+(?=[A-Z“""(\d])");

    public static IReadOnlyList<string> GroupSentences(string rawText, int maxTokens)
    {
        var groups = new List<string>();
        var current = new List<string>();
        var tokens = 0;

        foreach (var sentence in SentenceEnd.Split(rawText))
        {
            var t = EstimateTokens(sentence);
            if (current.Count > 0 && tokens + t > maxTokens)
            {
                groups.Add(string.Join(" ", current));
                current.Clear();
                tokens = 0;
            }

            current.Add(sentence);
            tokens += t;
        }

        // A single sentence longer than the limit stays whole: never cut mid-sentence.
        if (current.Count > 0)
        {
            groups.Add(string.Join(" ", current));
        }

        return groups;
    }

    // Rough token count (English is about 1.3 tokens per word) - good enough for sizing chunks.
    private static int EstimateTokens(string text) =>
        (int)(text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length * 1.3);
}
