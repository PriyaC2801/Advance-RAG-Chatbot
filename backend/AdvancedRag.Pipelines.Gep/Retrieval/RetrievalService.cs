using AdvancedRag.Pipelines.Gep.Models;
using AdvancedRag.Core.Rag;
using AdvancedRag.Pipelines.Gep.Embeddings;

namespace AdvancedRag.Pipelines.Gep.Retrieval;

/// <summary>
/// Semantic retrieval with transparent, metadata-aware ranking:
///   1. embed the question (query vector)
///   2. narrow the candidates with filters (from the request, or stated in the question)
///   3. score = cosine similarity + small, explained adjustments
///   4. for comparisons, retrieve each region separately so both sides are represented
///   5. mark results below the relevance threshold
/// Every score change is recorded so the debug view can show exactly why a chunk ranked where it did.
/// </summary>
public sealed class RetrievalService(
    GepEmbedder embeddings,
    GepVectorStore store,
    QueryAnalyzer analyzer,
    IOptions<GepOptions> options)
{
    private IReadOnlyDictionary<string, string>? _countryRegions;
    private IReadOnlyList<DocumentChunk>? _countryRegionsSource;
    private KeywordIndex? _keywordIndex;
    private IReadOnlyList<DocumentChunk>? _keywordSource;

    public async Task<RetrievalResult> SearchAsync(RetrievalRequest request, CancellationToken cancellationToken)
    {
        if (!store.IsLoaded || store.Chunks.Count == 0)
        {
            throw new IndexNotReadyException("The search index is empty. Ingest the document first.");
        }

        var settings = options.Value.Retrieval;
        var items = store.Chunks;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var analysis = analyzer.Analyze(request.Question, CountryRegions(items));
        var analysisMs = clock.ElapsedMilliseconds;
        var topK = Math.Clamp(request.TopK ?? settings.DefaultTopK, 1, settings.MaxTopK);
        var filters = new List<string>();
        var notes = new List<string>();

        clock.Restart();
        var queryVector = await embeddings.EmbedQueryAsync(request.Question, cancellationToken);
        var embeddingMs = clock.ElapsedMilliseconds;
        var candidates = ApplyFilters(items, request, analysis, filters, notes);

        // Keyword matches are computed for the filtered candidates only, against statistics of the whole index.
        var keywords = request.UseMetadataRanking && settings.KeywordSearchEnabled
            ? Keywords(items).Match(request.Question, candidates)
            : [];
        var maxBm25 = keywords.Count == 0 ? 0 : keywords.Values.Max(k => k.Bm25);

        // The vector store computes the similarity (pgvector when PostgreSQL is the provider).
        var similarities = await store.SimilaritiesAsync(queryVector, candidates.Select(c => c.Id).ToList(), cancellationToken);
        var scored = candidates
            .Where(c => similarities.ContainsKey(c.Id))
            .Select(chunk => Score(chunk, similarities[chunk.Id], analysis, request.UseMetadataRanking, settings,
                keywords.GetValueOrDefault(chunk.Id), maxBm25))
            .ToList();

        List<ScoredChunk> top;
        if (request.UseMetadataRanking && analysis.IsComparison && request.RegionFilter is null)
        {
            // Retrieve each compared region on its own, so one region can never crowd out the other.
            var perRegion = (int)Math.Ceiling(topK / (double)analysis.Regions.Count);
            top = analysis.Regions
                .SelectMany(region => scored
                    .Where(s => s.Chunk.Region == region)
                    .OrderByDescending(s => s.FinalScore)
                    .Take(perRegion)
                    .Select(s => s with { RetrievedForRegion = region }))
                .OrderByDescending(s => s.FinalScore)
                .ToList();
            notes.Add($"Comparison detected: retrieved up to {perRegion} chunk(s) separately for each of {string.Join(" and ", analysis.Regions)}.");
            foreach (var missing in analysis.Regions.Where(r => top.All(t => t.RetrievedForRegion != r)))
            {
                notes.Add($"No indexed chunks found for {missing}.");
            }
        }
        else
        {
            top = scored.OrderByDescending(s => s.FinalScore).Take(topK).ToList();
        }

        var ranked = top.Select((s, i) => s with { Rank = i + 1 }).ToList();
        if (!ranked.Any(r => r.AboveThreshold))
        {
            notes.Add($"No chunk reached the minimum similarity of {settings.MinRelevanceScore:0.00}; the report may not cover this question.");
        }

        return new RetrievalResult(analysis, filters, notes, candidates.Count, settings.MinRelevanceScore,
            store.ModelId ?? embeddings.ModelId, ranked)
        {
            AnalysisMilliseconds = analysisMs,
            EmbeddingMilliseconds = embeddingMs,
            KeywordMatchCount = keywords.Count,
        };
    }

    private static List<DocumentChunk> ApplyFilters(IReadOnlyList<DocumentChunk> items, RetrievalRequest request,
        QueryAnalysis analysis, List<string> filters, List<string> notes)
    {
        IEnumerable<DocumentChunk> query = items;

        // Filters chosen by the user always apply.
        if (request.ChapterFilter is int chapter)
        {
            query = query.Where(i => i.ChapterNumber == chapter);
            filters.Add($"Chapter = {chapter} (request filter)");
        }
        if (!string.IsNullOrWhiteSpace(request.RegionFilter))
        {
            query = query.Where(i => string.Equals(i.Region, request.RegionFilter, StringComparison.OrdinalIgnoreCase));
            filters.Add($"Region = {request.RegionFilter} (request filter)");
        }
        if (request.ContentTypes is { Count: > 0 } types)
        {
            query = query.Where(i => types.Contains(i.ContentType));
            filters.Add($"Content type in [{string.Join(", ", types)}] (request filter)");
        }

        // Constraints stated in the question apply only with metadata ranking on
        // (switch it off to see what pure semantic similarity does).
        if (request.UseMetadataRanking)
        {
            if (request.ChapterFilter is null && analysis.ExplicitChapter is int asked)
            {
                if (items.Any(i => i.ChapterNumber == asked))
                {
                    query = query.Where(i => i.ChapterNumber == asked);
                    filters.Add($"Chapter = {asked} (named in the question)");
                }
                else
                {
                    notes.Add($"The question names Chapter {asked}, but the report has no such chapter; the constraint was ignored.");
                }
            }
            if (analysis.AsksForExecutiveSummary && request.ContentTypes is null)
            {
                query = query.Where(i => i.ContentType == ContentType.ExecutiveSummary);
                filters.Add("Content type = ExecutiveSummary (named in the question)");
            }
        }

        return query.ToList();
    }

    private static ScoredChunk Score(DocumentChunk chunk, double similarity, QueryAnalysis analysis,
        bool useMetadata, RetrievalOptions settings, KeywordMatch? keyword, double maxBm25)
    {
        var adjustments = new List<ScoreAdjustment>();

        if (useMetadata)
        {
            var askedRegions = analysis.AllRegions;
            if (chunk.Region is not null && askedRegions.Count > 0)
            {
                if (askedRegions.Contains(chunk.Region))
                {
                    var via = analysis.Regions.Contains(chunk.Region) ? "named in question" : "region of a named country";
                    adjustments.Add(new($"Region '{chunk.Region}' matches the question ({via})", settings.RegionMatchBoost));
                }
                else
                {
                    adjustments.Add(new($"Region '{chunk.Region}' is not a region the question asks about", -settings.RegionMismatchPenalty));
                }
            }

            var heading = $"{chunk.SectionTitle} {chunk.SubsectionTitle}".ToLowerInvariant();
            var term = analysis.KeyTerms.FirstOrDefault(t => heading.Contains(t, StringComparison.Ordinal));
            if (term is not null)
            {
                adjustments.Add(new($"Heading contains '{term}'", settings.HeadingMatchBoost));
            }

            if (!analysis.AsksForReferenceMaterial && chunk.ContentType is ContentType.References or ContentType.FrontMatter)
            {
                adjustments.Add(new($"{chunk.ContentType} content rarely answers questions", -settings.LowValueContentPenalty));
            }
        }

        var aboveThreshold = similarity >= settings.MinRelevanceScore;
        if (keyword is not null && maxBm25 > 0)
        {
            var terms = string.Join(", ", keyword.MatchedTerms);
            var boost = settings.KeywordMatchBoost * (keyword.Bm25 / maxBm25) * keyword.Coverage;
            adjustments.Add(new($"Keyword match ({keyword.Coverage:P0} of the question's terms: {terms})", boost));

            // A near-miss on meaning with strong exact-word evidence still counts as relevant.
            if (!aboveThreshold && keyword.MatchedTerms.Count >= 2 && keyword.Coverage >= settings.KeywordRescueCoverage
                && similarity >= settings.MinRelevanceScore - settings.KeywordRescueMargin)
            {
                aboveThreshold = true;
                adjustments.Add(new("Similarity is just below the threshold, but the chunk contains most of the question's terms", 0));
            }
        }

        var finalScore = similarity + adjustments.Sum(a => a.Delta);
        return new ScoredChunk(chunk, 0, similarity, adjustments, finalScore,
            AboveThreshold: aboveThreshold, RetrievedForRegion: null);
    }

    private KeywordIndex Keywords(IReadOnlyList<DocumentChunk> items)
    {
        // Rebuilt only when the index is replaced.
        if (!ReferenceEquals(_keywordSource, items))
        {
            _keywordIndex = KeywordIndex.Build(items);
            _keywordSource = items;
        }
        return _keywordIndex!;
    }

    private IReadOnlyDictionary<string, string> CountryRegions(IReadOnlyList<DocumentChunk> items)
    {
        // Rebuilt only when the index is replaced.
        if (!ReferenceEquals(_countryRegionsSource, items))
        {
            _countryRegions = CountryRegionIndex.Build(items);
            _countryRegionsSource = items;
        }
        return _countryRegions!;
    }
}
