using meli_znube_integration.Api;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices((context, services) =>
    {
        services.AddHttpClient("meli", c =>
        {
            c.BaseAddress = new Uri("https://api.mercadolibre.com/");
            c.Timeout = TimeSpan.FromSeconds(45);
        });

        services.AddHttpClient("znube", c =>
        {
            var baseUrl = context.Configuration["Znube:BaseUrl"];
            if (!string.IsNullOrWhiteSpace(baseUrl))
            {
                c.BaseAddress = new Uri(baseUrl);
            }
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
    })
    .Build();

host.Run();