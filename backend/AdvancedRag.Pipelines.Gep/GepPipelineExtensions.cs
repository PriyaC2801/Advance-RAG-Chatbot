using AdvancedRag.Core.Rag;
using AdvancedRag.Pipelines.Gep.Answering;
using AdvancedRag.Pipelines.Gep.Chunking;
using AdvancedRag.Pipelines.Gep.Embeddings;
using AdvancedRag.Pipelines.Gep.Evaluation;
using AdvancedRag.Pipelines.Gep.Pdf;
using AdvancedRag.Pipelines.Gep.Retrieval;

namespace AdvancedRag.Pipelines.Gep;

public static class GepPipelineExtensions
{
    /// <summary>Registers the Gep pipeline (an <c>IRagPipeline</c> singleton plus everything it needs).</summary>
    public static IServiceCollection AddGepPipeline(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<GepOptions>()
            .Bind(configuration.GetSection(GepOptions.SectionName))
            .PostConfigure(o => o.ApplyListDefaults());

        // PDF → classified lines → chunks
        services.AddSingleton<IDocumentTextExtractor, PdfPigTextExtractor>();
        services.AddSingleton<GepLayoutClassifier>();
        services.AddSingleton<DocumentExtractionService>();
        services.AddSingleton<IDocumentChunker, StructureAwareChunker>();
        services.AddSingleton<IFigureDataService, FigureDataService>();
        services.AddSingleton<ChunkStore>();
        services.AddSingleton<ChunkingService>();

        // embeddings + index
        services.AddSingleton<GepEmbedder>();
        services.AddSingleton<EmbeddingStore>();
        services.AddSingleton<GepVectorStore>();
        services.AddSingleton<GepIngestionService>();

        // answering
        services.AddSingleton<QueryAnalyzer>();
        services.AddSingleton<RetrievalService>();
        services.AddSingleton<TableNotesLookup>();
        services.AddSingleton<GepAnswerGenerator>();
        services.AddSingleton<ChatService>();
        services.AddSingleton<EvaluationService>();

        services.AddSingleton<GepPipeline>();
        services.AddSingleton<IRagPipeline>(sp => sp.GetRequiredService<GepPipeline>());
        services.AddSingleton<IEvaluablePipeline>(sp => sp.GetRequiredService<GepPipeline>());
        return services;
    }
}
