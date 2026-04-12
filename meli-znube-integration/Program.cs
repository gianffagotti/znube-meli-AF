using meli_znube_integration.Clients;
using meli_znube_integration.Common;
using meli_znube_integration.Core.Application.Orders;
using meli_znube_integration.Core.Domain.Orders;
using meli_znube_integration.Core.Ports;
using meli_znube_integration.Infrastructure;
using meli_znube_integration.Infrastructure.Adapters.AzureTables;
using meli_znube_integration.Infrastructure.Adapters.MercadoLibre;
using meli_znube_integration.Infrastructure.Adapters.Znube;
using meli_znube_integration.Services;
using meli_znube_integration.Services.Calculators;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

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
        .AddHttpMessageHandler<ZnubeTokenHandler>()
        .AddPolicyHandler(ResiliencePolicies.GetZnubeResiliencePolicy());

        services.AddSingleton<TokensStoreBlob>();
        services.AddSingleton<MeliAuth>();
        services.AddScoped<IMeliApiClient, MeliApiClient>();
        services.AddScoped<IMarketplacePort, MeliMarketplaceAdapter>();
        services.AddScoped<MeliOrderAdapter>();
        services.AddScoped<IOrderPort>(sp => sp.GetRequiredService<MeliOrderAdapter>());
        services.AddScoped<INotePort>(sp => sp.GetRequiredService<MeliOrderAdapter>());
        services.AddScoped<IItemRulePort, TableStorageRuleAdapter>();
        services.AddScoped<IZnubeApiClient, ZnubeApiClient>();
        services.AddScoped<IInventoryPort, ZnubeInventoryAdapter>();
        services.AddSingleton<IAllocationDomainService, AllocationDomainService>();
        services.AddScoped<INoteFormatter, NoteFormatter>();
        services.AddScoped<IOrderExpansionService, OrderExpansionService>();
        services.AddScoped<GenerateOrderNoteUseCase>();
        services.AddSingleton<IOrderExecutionStore, OrderExecutionStore>();
        services.AddScoped<PackProcessor>();
        services.AddTransient<ZnubeTokenHandler>();
        services.AddTransient<MeliTokenHandler>();
        services.AddSingleton<MeliRateLimiter>();
        services.AddTransient<MeliRateLimitHandler>();
        services.AddSingleton<StockRuleService>();
        services.AddSingleton<IDashboardLogService, DashboardLogService>();
        services.AddSingleton<FullRuleDiscoveryStateService>();
        services.AddSingleton<FullRuleDiscoveryQueueService>();
        services.AddScoped<FullRuleDiscoveryService>();
        services.AddSingleton<StockLocationQueueService>();
        services.AddSingleton<OrderQueueService>();
        services.AddScoped<StockLocationProcessor>();
        services.AddScoped<IStockSyncSourceService, StockSyncSourceService>();
        services.AddSingleton<ISkuParser, SkuParserService>();

        // Calculators
        services.AddSingleton<IStockCalculator, FullStockCalculator>();
        services.AddSingleton<IStockCalculator, PackStockCalculator>();
        services.AddSingleton<IStockCalculator, ComboStockCalculator>();
        services.AddSingleton<StockCalculatorFactory>();
    })
    .Build();

host.Run();