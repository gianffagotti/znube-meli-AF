using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Net.Http;
using meli_znube_integration.Api;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices((context, services) =>
    {
        services.AddHttpClient("meli", c =>
        {
            c.BaseAddress = new Uri("https://api.mercadolibre.com/");
            c.Timeout = TimeSpan.FromSeconds(15);
        });

        services.AddHttpClient("znube", c =>
        {
            var baseUrl = context.Configuration["Znube:BaseUrl"];
            if (!string.IsNullOrWhiteSpace(baseUrl))
            {
                c.BaseAddress = new Uri(baseUrl);
            }
            c.Timeout = TimeSpan.FromSeconds(15);
            var token = context.Configuration["Znube:Token"];
            if (!string.IsNullOrWhiteSpace(token))
            {
                c.DefaultRequestHeaders.TryAddWithoutValidation("zNube-token", token);
            }
        });

        services.AddSingleton<TokensStoreBlob>();
        services.AddSingleton<MeliAuth>();
        services.AddScoped<MeliClient>();
        services.AddScoped<ZnubeClient>();
    })
    .Build();

host.Run();
