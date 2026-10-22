using System.Text.RegularExpressions;

namespace AdvancedRag.Pipelines.Gep.Chunking;

/// <summary>A piece of text cut from a segment, with the character range it covers.</summary>
public sealed record TextPiece(string Text, int Start, int End);

/// <summary>
/// Cuts long text into chunks at sentence boundaries, with a small overlap.
///
/// Why sentences: a chunk that ends mid-sentence ("growth is projected to slow to") loses the very
/// number a question asks about. Why overlap: the sentence that sets up a fact ("In India, ...")
/// is often at the end of the previous chunk; repeating it keeps context together.
/// </summary>
public sealed partial class TextSplitter(ChunkingOptions options)
{
    /// <summary>Abbreviations that end with a period but do not end a sentence.</summary>
    private static readonly string[] Abbreviations =
        ["U.S.", "U.K.", "e.g.", "i.e.", "et al.", "vs.", "No.", "Fig.", "Dem.", "Rep.", "Fed.", "Sts.", "St.", "Jan.", "Feb.", "Dec.", "approx."];

    public IReadOnlyList<TextPiece> SplitIntoSentences(string text)
    {
        var sentences = new List<TextPiece>();
        var start = 0;

        foreach (Match match in SentenceBoundary().Matches(text))
        {
            var end = match.Index + match.Length;               // includes trailing whitespace
            var candidate = text[start..match.Index].TrimEnd();
            if (Abbreviations.Any(a => candidate.EndsWith(a, StringComparison.Ordinal))) continue;

            AddSentence(sentences, text, start, end);
            start = end;
        }
        AddSentence(sentences, text, start, text.Length);
        return sentences;
    }

    /// <summary>Greedily packs sentences into chunks of at most MaxChunkCharacters, with overlap.</summary>
    public IReadOnlyList<TextPiece> Split(string text)
    {
        var sentences = SplitIntoSentences(text).SelectMany(SplitLongSentence).ToList();
        if (sentences.Count == 0) return [];

        var ranges = new List<(int First, int Last)>();
        var i = 0;
        while (i < sentences.Count)
        {
            var j = i;
            var length = 0;
            while (j < sentences.Count && (j == i || length + 1 + sentences[j].Text.Length <= options.MaxChunkCharacters))
            {
                length += sentences[j].Text.Length + 1;
                j++;
            }
            ranges.Add((i, j - 1));
            if (j >= sentences.Count) break;

            // Step back over whole sentences that fit in the overlap budget (always moving forward).
            var overlap = 0;
            var back = 0;
            while (back < j - i - 1 && overlap + sentences[j - 1 - back].Text.Length <= options.OverlapCharacters)
            {
                overlap += sentences[j - 1 - back].Text.Length;
                back++;
            }
            i = j - back;
        }

        // A tiny last piece is merged into the previous one rather than standing alone.
        if (ranges.Count > 1)
        {
            var last = ranges[^1];
            var lastLength = sentences[last.Last].End - sentences[last.First].Start;
            if (lastLength < options.MinChunkCharacters)
            {
                ranges[^2] = (ranges[^2].First, last.Last);
                ranges.RemoveAt(ranges.Count - 1);
            }
        }

        return ranges
            .Select(r => new TextPiece(
                text[sentences[r.First].Start..sentences[r.Last].End].Trim(),
                sentences[r.First].Start,
                sentences[r.Last].End))
            .ToList();
    }

    /// <summary>Packs whole units (e.g. table rows) into groups without overlap.</summary>
    public IReadOnlyList<IReadOnlyList<T>> Pack<T>(IReadOnlyList<T> units, Func<T, int> length, int budget)
    {
        var groups = new List<IReadOnlyList<T>>();
        var current = new List<T>();
        var size = 0;
        foreach (var unit in units)
        {
            var unitLength = length(unit) + 1;
            if (current.Count > 0 && size + unitLength > budget)
            {
                groups.Add(current);
                current = [];
                size = 0;
            }
            current.Add(unit);
            size += unitLength;
        }
        if (current.Count > 0) groups.Add(current);
        return groups;
    }

    private IEnumerable<TextPiece> SplitLongSentence(TextPiece sentence)
    {
        if (sentence.Text.Length <= options.MaxChunkCharacters)
        {
            yield return sentence;
            yield break;
        }

        // Rare (long reference lists): cut at the last space before the limit.
        var offset = 0;
        var text = sentence.Text;
        while (text.Length - offset > options.MaxChunkCharacters)
        {
            var cut = text.LastIndexOf(' ', offset + options.MaxChunkCharacters, options.MaxChunkCharacters);
            if (cut <= offset) cut = offset + options.MaxChunkCharacters;
            yield return new TextPiece(text[offset..cut], sentence.Start + offset, sentence.Start + cut);
            offset = cut + 1;
        }
        yield return new TextPiece(text[offset..], sentence.Start + offset, sentence.End);
    }

    private static void AddSentence(List<TextPiece> sentences, string text, int start, int end)
    {
        var sentence = text[start..end].Trim();
        if (sentence.Length > 0) sentences.Add(new TextPiece(sentence, start, end));
    }

    /// <summary>". " / "? " / "! " (optionally after a closing quote or bracket) before a capital, digit or quote.</summary>
    [GeneratedRegex(@"(?<=[.!?][""”’)]?)\s+(?=[""“(]?[A-Z0-9])")]
    private static partial Regex SentenceBoundary();
}
