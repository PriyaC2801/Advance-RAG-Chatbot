using System.Text.RegularExpressions;


namespace AdvancedRag.Pipelines.Gpo;

/// <summary>
/// Packs whole sentences into chunks of roughly <see cref="GpoOptions.WordsPerChunk"/> words, repeating the
/// last sentence or two at the start of the next chunk so an idea that straddles a boundary
/// still appears intact in at least one chunk. Chunks never cross a page, so page citations stay exact.
/// </summary>
internal sealed partial class ChunkingService(GpoOptions options)
{
    private readonly GpoOptions _options = options;


    // A sentence end (. ! ?, optionally followed by a closing quote/bracket) followed by
    // whitespace and something that looks like a sentence start.
    [GeneratedRegex(@"(?<=[.!?][""'”’)\]]?)\s+(?=[\p{Lu}\d""'“‘(])")]
    private static partial Regex SentenceBoundary();

    public IReadOnlyList<ChunkDraft> Chunk(IReadOnlyList<ExtractedPage> pages)
    {
        var chunks = new List<ChunkDraft>();
        var chunkIndex = 0;

        foreach (var page in pages)
        {
            var sentences = SplitIntoSentences(page.Text);
            foreach (var text in PackSentences(sentences))
            {
                chunks.Add(new ChunkDraft(chunkIndex++, page.PageNumber, text));
            }
        }

        return chunks;
    }

    private List<string> SplitIntoSentences(string pageText)
    {
        var sentences = new List<string>();

        // Blank lines separate layout blocks (headings, paragraphs, table cells), which are
        // sentence boundaries even when they carry no end punctuation.
        foreach (var block in Regex.Split(pageText, @"\n\s*\n"))
        {
            foreach (var raw in SentenceBoundary().Split(block))
            {
                var sentence = WhitespaceRun().Replace(raw, " ").Trim();
                if (sentence.Length == 0)
                {
                    continue;
                }

                // A "sentence" longer than a whole chunk (e.g. a table dump with no punctuation)
                // is cut by words so it can still be packed.
                var words = sentence.Split(' ');
                for (var i = 0; i < words.Length; i += _options.WordsPerChunk)
                {
                    sentences.Add(string.Join(' ', words.Skip(i).Take(_options.WordsPerChunk)));
                }
            }
        }

        return sentences;
    }

    private IEnumerable<string> PackSentences(List<string> sentences)
    {
        var current = new List<string>();
        var currentWords = 0;
        var newSentencesInCurrent = 0; // sentences in `current` that are not just carried-over overlap

        foreach (var sentence in sentences)
        {
            var sentenceWords = CountWords(sentence);

            if (currentWords + sentenceWords > _options.WordsPerChunk && newSentencesInCurrent > 0)
            {
                yield return string.Join(' ', current);

                current = TakeOverlap(current);
                currentWords = current.Sum(CountWords);
                newSentencesInCurrent = 0;

                if (currentWords + sentenceWords > _options.WordsPerChunk)
                {
                    current.Clear();
                    currentWords = 0;
                }
            }

            current.Add(sentence);
            currentWords += sentenceWords;
            newSentencesInCurrent++;
        }

        if (newSentencesInCurrent > 0)
        {
            yield return string.Join(' ', current);
        }
    }

    private List<string> TakeOverlap(List<string> chunkSentences)
    {
        var overlap = new List<string>();
        var words = 0;

        for (var i = chunkSentences.Count - 1; i >= 0 && overlap.Count < _options.MaxOverlapSentences; i--)
        {
            var sentenceWords = CountWords(chunkSentences[i]);
            if (words + sentenceWords > _options.MaxOverlapWords)
            {
                break;
            }

            overlap.Insert(0, chunkSentences[i]);
            words += sentenceWords;
        }

        return overlap;
    }

    private static int CountWords(string text) => text.Count(c => c == ' ') + 1;

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRun();
}
