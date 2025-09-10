using System.Text.Json;

namespace meli_znube_integration.Api;

public class MeliAuth
{
    private readonly TokensStoreBlob _store;
    private readonly IHttpClientFactory _httpClientFactory;


    public MeliAuth(TokensStoreBlob store, IHttpClientFactory httpClientFactory)
    {
        _store = store;
        _httpClientFactory = httpClientFactory;
    }

    public async Task ExchangeCodeForTokensAsync(string code)
    {
        var clientId = Env("MELI_CLIENT_ID");
        var clientSecret = Env("MELI_CLIENT_SECRET");
        var redirectUri = Env("MELI_REDIRECT_URI");

        var body = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["code"] = code,
            ["redirect_uri"] = redirectUri
        };

        using var http = _httpClientFactory.CreateClient("meli");
        var res = await http.PostAsync("oauth/token", new FormUrlEncodedContent(body));
        res.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        var access = root.GetProperty("access_token").GetString();
        var refresh = root.GetProperty("refresh_token").GetString();
        var expiresIn = root.GetProperty("expires_in").GetInt32();

        await _store.WriteAsync(access, DateTimeOffset.UtcNow.AddSeconds(expiresIn - 60), refresh!);
    }

    public async Task<string> GetValidAccessTokenAsync()
    {
        var (access, exp, refresh) = await _store.ReadAsync();
        if (!string.IsNullOrEmpty(access) && exp.HasValue && exp > DateTimeOffset.UtcNow)
            return access!;

        if (string.IsNullOrWhiteSpace(refresh))
        {
            throw new InvalidOperationException("No hay tokens disponibles para refrescar.");
        }

        var clientId = Env("MELI_CLIENT_ID");
        var clientSecret = Env("MELI_CLIENT_SECRET");

        var body = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["refresh_token"] = refresh!
        };

        using var http = _httpClientFactory.CreateClient("meli");
        var res = await http.PostAsync("oauth/token", new FormUrlEncodedContent(body));
        res.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        var newAccess = root.GetProperty("access_token").GetString();
        var newRefresh = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : refresh;
        var expiresIn = root.GetProperty("expires_in").GetInt32();

        await _store.WriteAsync(newAccess, DateTimeOffset.UtcNow.AddSeconds(expiresIn - 60), newRefresh!);
        return newAccess!;
    }

    private static string Env(string key) =>
        Environment.GetEnvironmentVariable(key) ?? throw new InvalidOperationException($"Missing {key}");
}


