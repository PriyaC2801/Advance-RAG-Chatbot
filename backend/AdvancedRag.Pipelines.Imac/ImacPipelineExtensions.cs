using AdvancedRag.Core.Rag;

namespace AdvancedRag.Pipelines.Imac;

public static class ImacPipelineExtensions
{
    /// <summary>Registers the iMac pipeline (an <c>IRagPipeline</c> singleton plus everything it needs).</summary>
    public static IServiceCollection AddImacPipeline(this IServiceCollection services, IConfiguration configuration)
    {
        var options = new ImacOptions();
        configuration.GetSection(ImacOptions.SectionName).Bind(options);
        services.AddSingleton(options);

        services.AddSingleton<ImacStore>();
        services.AddSingleton<ImacVisionDescriber>();
        services.AddSingleton<ImacPipeline>();
        services.AddSingleton<IRagPipeline>(sp => sp.GetRequiredService<ImacPipeline>());
        return services;
    }
}
