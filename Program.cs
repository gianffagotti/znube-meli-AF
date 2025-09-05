using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Net.Http;
using meli_znube_integration.Api;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices(services =>
    {
        services.AddHttpClient("meli", c =>
        {
            c.BaseAddress = new Uri("https://api.mercadolibre.com/");
            c.Timeout = TimeSpan.FromSeconds(15);
        });

        services.AddSingleton<TokensStoreBlob>();
        services.AddSingleton<MeliAuth>();
        services.AddScoped<MeliClient>();
    })
    .Build();

host.Run();
