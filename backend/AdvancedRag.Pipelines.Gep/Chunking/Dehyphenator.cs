using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AdvancedRag.Pipelines.Gep.Chunking;

/// <summary>Joined text plus, for each source line, the character offset where it starts.</summary>
public sealed record JoinedText(string Text, IReadOnlyList<int> LineStarts);

/// <summary>
/// Joins wrapped lines back into running text and repairs words split across lines.
///
/// The hard part: "infla-" + "tion" must become "inflation", but "non-" + "resource" must stay
/// "non-resource". Instead of guessing, we ask the report itself: we collect every word that
/// appears un-split somewhere in the document, and keep the hyphen only if the hyphenated form is
/// what the report uses elsewhere.
/// </summary>
public sealed partial class Dehyphenator
{
    private readonly HashSet<string> _vocabulary;

    private Dehyphenator(HashSet<string> vocabulary) => _vocabulary = vocabulary;

    /// <summary>Learns the vocabulary from all lines of the document.</summary>
    public static Dehyphenator Build(IEnumerable<string> lines)
    {
        var vocabulary = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < tokens.Length; i++)
            {
                // The first and last token of a line may be fragments of a split word — skip them.
                if (i == 0 || i == tokens.Length - 1) continue;
                var word = CleanToken(tokens[i]);
                if (word.Length > 0) vocabulary.Add(word);
            }
        }
        return new Dehyphenator(vocabulary);
    }

    public JoinedText Join(IReadOnlyList<string> lines)
    {
        var builder = new StringBuilder();
        var starts = new List<int>(lines.Count);

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (builder.Length == 0)
            {
                starts.Add(0);
                builder.Append(line);
                continue;
            }

            if (EndsWithWordHyphen(builder) && line.Length > 0 && char.IsLower(line[0]))
            {
                if (!ShouldKeepHyphen(builder, line)) builder.Length--; // drop the line-break hyphen
                starts.Add(builder.Length);
                builder.Append(line);
            }
            else
            {
                builder.Append(' ');
                starts.Add(builder.Length);
                builder.Append(line);
            }
        }

        return new JoinedText(builder.ToString(), starts);
    }

    private bool ShouldKeepHyphen(StringBuilder builder, string nextLine)
    {
        var text = builder.ToString();
        var lastSpace = text.LastIndexOf(' ');
        var prefix = CleanToken(text[(lastSpace + 1)..^1]);           // "infla" from "infla-"
        var suffix = CleanToken(nextLine.Split(' ', 2)[0]);           // "tion," → "tion"

        if (_vocabulary.Contains(prefix + suffix)) return false;      // "inflation" is a known word
        return _vocabulary.Contains($"{prefix}-{suffix}");            // "non-resource" is a known word
    }

    private static bool EndsWithWordHyphen(StringBuilder builder) =>
        builder.Length >= 2 && builder[^1] == '-' && char.IsLetter(builder[^2]);

    private static string CleanToken(string token) =>
        EdgePunctuation().Replace(token, string.Empty).ToLower(CultureInfo.InvariantCulture);

    [GeneratedRegex(@"^[^\p{L}\p{N}]+|[^\p{L}\p{N}]+$")]
    private static partial Regex EdgePunctuation();
}
