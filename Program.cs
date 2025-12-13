using meli_znube_integration.Clients;
using meli_znube_integration.Common;
using meli_znube_integration.Infrastructure;
using meli_znube_integration.Services;
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
        services.AddScoped<MeliClient>();
        services.AddScoped<ZnubeClient>();
        services.AddScoped<NoteService>();
        services.AddSingleton<PackLockStoreBlob>();
        services.AddScoped<PackProcessor>();
        services.AddTransient<ZnubeTokenHandler>();
        services.AddTransient<MeliTokenHandler>();
        services.AddSingleton<MeliRateLimiter>();
        services.AddTransient<MeliRateLimitHandler>();
    })
    .Build();

host.Run();