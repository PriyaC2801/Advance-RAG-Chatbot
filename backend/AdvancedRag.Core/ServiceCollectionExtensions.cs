using AdvancedRag.Core.Ai;
using AdvancedRag.Core.Rag;
using AdvancedRag.Core.Storage;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace AdvancedRag.Core;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers what every pipeline shares: the Gemini client, the PostgreSQL data source, storage paths, the pipeline
    /// registry and the background ingestion coordinator. Pipelines add themselves with their own Add...Pipeline method.
    /// </summary>
    public static IServiceCollection AddAdvancedRagCore(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<GeminiOptions>(configuration.GetSection(GeminiOptions.SectionName));
        services.Configure<StorageOptions>(configuration.GetSection(StorageOptions.SectionName));

        services.AddHttpClient<IGeminiClient, GeminiClient>();
        services.TryAddSingleton<EmbeddingRateLimiter>();
        services.TryAddSingleton<StoragePaths>();

        var connectionString = configuration.GetConnectionString(VectorDb.ConnectionStringName)
            ?? throw new InvalidOperationException("ConnectionStrings:VectorDb is missing. See appsettings.Development.json and docker-compose.yml.");
        services.TryAddSingleton<NpgsqlDataSource>(_ => VectorDb.CreateDataSource(connectionString));

        services.TryAddSingleton<PipelineRegistry>();
        services.TryAddSingleton<IngestionCoordinator>();
        return services;
    }
}
