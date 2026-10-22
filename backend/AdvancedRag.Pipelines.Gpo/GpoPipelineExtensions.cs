using AdvancedRag.Core.Rag;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AdvancedRag.Pipelines.Gpo;

public static class GpoPipelineExtensions
{
    /// <summary>Registers the Gpo pipeline (an <c>IRagPipeline</c> singleton plus everything it needs).</summary>
    public static IServiceCollection AddGpoPipeline(this IServiceCollection services, IConfiguration configuration)
    {
        var options = new GpoOptions();
        configuration.GetSection(GpoOptions.SectionName).Bind(options);

        services.AddSingleton(options);
        services.TryAddSingleton<PdfPigTextExtractor>();
        services.TryAddSingleton<ChunkingService>();
        services.TryAddSingleton<GpoChunkStore>();
        services.AddSingleton<GpoPipeline>();
        services.AddSingleton<IRagPipeline>(sp => sp.GetRequiredService<GpoPipeline>());
        return services;
    }
}
