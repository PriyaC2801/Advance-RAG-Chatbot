using System.Text.Json.Serialization;
using AdvancedRag.Api.Infrastructure;
using AdvancedRag.Core;
using AdvancedRag.Pipelines.Gep;
using AdvancedRag.Pipelines.Gpo;
using AdvancedRag.Pipelines.Imac;

var builder = WebApplication.CreateBuilder(args);

// Shared: Gemini client, PostgreSQL, storage paths, pipeline registry, background ingestion.
builder.Services.AddAdvancedRagCore(builder.Configuration);

// One pipeline per document. Each keeps its own ingestion, chunking, retrieval and prompt.
builder.Services.AddGepPipeline(builder.Configuration);
builder.Services.AddGpoPipeline(builder.Configuration);
builder.Services.AddImacPipeline(builder.Configuration);

builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddHostedService<AutoIndexService>();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddOpenApi();

var app = builder.Build();

app.UseExceptionHandler(); // known errors -> ApiExceptionHandler; others -> generic 500 ProblemDetails, no stack trace

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1.json", "Advanced RAG API v1"));
}

// No HTTPS redirection in local dev: the React dev server proxies plain HTTP to this API.
app.MapControllers();

app.Run();


