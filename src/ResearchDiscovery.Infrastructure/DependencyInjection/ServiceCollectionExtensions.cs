using Anthropic;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Polly;
using ResearchDiscovery.Application.Abstractions;
using ResearchDiscovery.Application.Options;
using ResearchDiscovery.Infrastructure.Analysis;
using ResearchDiscovery.Infrastructure.Arxiv;
using ResearchDiscovery.Infrastructure.Embeddings;
using ResearchDiscovery.Infrastructure.Ingestion;
using ResearchDiscovery.Infrastructure.Llm;
using ResearchDiscovery.Infrastructure.Persistence;
using ResearchDiscovery.Infrastructure.Profile;
using ResearchDiscovery.Infrastructure.Queries;
using ResearchDiscovery.Infrastructure.Search;

namespace ResearchDiscovery.Infrastructure.DependencyInjection;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Single composition point shared by the web host and the CLI ingestion
    /// mode, so both processes are configured identically.
    /// </summary>
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ArxivOptions>()
            .Bind(configuration.GetSection(ArxivOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<IngestionOptions>()
            .Bind(configuration.GetSection(IngestionOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<AdminOptions>()
            .Bind(configuration.GetSection(AdminOptions.SectionName));

        // The single provider-specific registration in the entire codebase.
        // Swapping to SQL Server = swap the Npgsql package for
        // Microsoft.EntityFrameworkCore.SqlServer, change this call to
        // UseSqlServer, and regenerate the migrations. Nothing else moves.
        services.AddDbContextFactory<AppDbContext>(options =>
            options.UseNpgsql(configuration.GetConnectionString("Default")));

        var arxiv = configuration.GetSection(ArxivOptions.SectionName).Get<ArxivOptions>()
            ?? new ArxivOptions();

        services.AddSingleton<ArxivThrottlingHandler>();

        var httpBuilder = services.AddHttpClient<IArxivClient, ArxivClient>(client =>
        {
            // arXiv's terms of use ask API consumers to identify themselves.
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "ResearchDiscovery/1.0 (research-to-project discovery tool)");
        });

        // Resilience is registered BEFORE the throttling handler so it sits
        // outside it: every retry attempt passes back through the throttle and
        // stays within arXiv's rate-limit etiquette. The standard retry honors
        // Retry-After on 429/503 responses.
        httpBuilder.AddStandardResilienceHandler(options =>
        {
            options.Retry.MaxRetryAttempts = arxiv.MaxRetries;
            options.Retry.Delay = TimeSpan.FromSeconds(arxiv.RetryBaseDelaySeconds);
            options.Retry.BackoffType = DelayBackoffType.Exponential;
            options.Retry.UseJitter = true;
            options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(30);
            options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(90);
            options.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(5);
        });

        httpBuilder.AddHttpMessageHandler(sp => sp.GetRequiredService<ArxivThrottlingHandler>());

        services.AddSingleton<PaperUpserter>();
        services.AddSingleton<IIngestionLockManager, DbIngestionLockManager>();
        services.AddScoped<IIngestionService, IngestionService>();
        services.AddScoped<IPaperQueryService, PaperQueryService>();

        services.AddOptions<AnalysisOptions>()
            .Bind(configuration.GetSection(AnalysisOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Phase 2 analysis layer. The Anthropic SDK resolves credentials from
        // the environment (ANTHROPIC_API_KEY); a missing key surfaces as an
        // auth error on the first analysis call, never at startup, so the
        // browse/ingestion paths run fine without one.
        services.AddSingleton(_ => new AnthropicClient());
        services.AddScoped<IPaperAnalyzer, AnthropicPaperAnalyzer>();
        services.AddScoped<IAnalysisService, AnalysisService>();

        // Personalized discovery: LLM registry/settings, profile, local
        // embeddings, and the search pipeline.
        services.AddOptions<LlmOptions>()
            .Bind(configuration.GetSection(LlmOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<EmbeddingOptions>()
            .Bind(configuration.GetSection(EmbeddingOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<ILlmSettingsService, LlmSettingsService>();
        services.AddSingleton<ProfileService>();

        // The embedder is a singleton: it owns the loaded ONNX session. The
        // named client is only used to download model files on first use.
        services.AddHttpClient(OnnxTextEmbedder.HttpClientName, client =>
            client.Timeout = TimeSpan.FromMinutes(10));
        services.AddSingleton<ITextEmbedder, OnnxTextEmbedder>();
        services.AddSingleton<IEmbeddingIndex, InMemoryEmbeddingIndex>();
        services.AddSingleton<IPaperEmbeddingService, PaperEmbeddingService>();

        services.AddScoped<ISearchService, SearchService>();
        services.AddScoped<ISearchPlanCompiler, AnthropicSearchPlanCompiler>();

        return services;
    }
}
