using meli_znube_integration.Clients;
using meli_znube_integration.Common;
using meli_znube_integration.Infrastructure;
using meli_znube_integration.Services;
using meli_znube_integration.Services.Calculators;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Polly;
using Polly.Extensions.Http;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices((context, services) =>
    {
        services.AddHttpClient("meli-auth", c =>
        {
            c.BaseAddress = new Uri(context.Configuration[EnvVars.Keys.MeliBaseUrl]!);
            c.Timeout = TimeSpan.FromSeconds(45);
        });

        services.AddHttpClient("meli", c =>
        {
            c.BaseAddress = new Uri(context.Configuration[EnvVars.Keys.MeliBaseUrl]!);
            c.Timeout = TimeSpan.FromMinutes(5);
        })
        .AddHttpMessageHandler<MeliTokenHandler>()
        .AddHttpMessageHandler<MeliRateLimitHandler>()
        .AddPolicyHandler(ResiliencePolicies.GetMeliResiliencePolicy());

        services.AddHttpClient("znube", c =>
        {
            c.BaseAddress = new Uri(context.Configuration[EnvVars.Keys.ZnubeBaseUrl]!);
            c.Timeout = TimeSpan.FromSeconds(45);
        })
        .AddHttpMessageHandler<ZnubeTokenHandler>();

        services.AddSingleton<TokensStoreBlob>();
        services.AddSingleton<MeliAuth>();
        services.AddScoped<IMeliApiClient, MeliApiClient>();
        services.AddScoped<IZnubeApiClient, ZnubeApiClient>();
        services.AddScoped<IZnubeAllocationService, ZnubeAllocationService>();
        services.AddScoped<NoteService>();
        services.AddSingleton<OrderExecutionStore>();
        services.AddScoped<PackProcessor>();
        services.AddTransient<ZnubeTokenHandler>();
        services.AddTransient<MeliTokenHandler>();
        services.AddSingleton<MeliRateLimiter>();
        services.AddTransient<MeliRateLimitHandler>();
        services.AddSingleton<StockMappingService>();
        services.AddSingleton<StockRuleService>();
        services.AddSingleton<ISkuParser, SkuParserService>();

        // Calculators
        services.AddSingleton<IStockCalculator, FullStockCalculator>();
        services.AddSingleton<IStockCalculator, PackStockCalculator>();
        services.AddSingleton<IStockCalculator, ComboStockCalculator>();
        services.AddSingleton<StockCalculatorFactory>();
    })
    .Build();

host.Run();