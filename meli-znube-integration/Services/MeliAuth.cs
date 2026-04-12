using System.Text.Json;

namespace meli_znube_integration.Services;

using meli_znube_integration.Infrastructure;
using meli_znube_integration.Common;

public class MeliAuth
{
    private readonly TokensStoreBlob _store;
    private readonly IHttpClientFactory _httpClientFactory;

    // In-memory cache
    private string? _cachedAccessToken;
    private DateTimeOffset? _cachedAccessTokenExp;
    // Buffer para considerar el token expirado un poco antes y evitar race conditions
    private readonly TimeSpan _expirationBuffer = TimeSpan.FromMinutes(5);

    public MeliAuth(TokensStoreBlob store, IHttpClientFactory httpClientFactory)
    {
        _store = store;
        _httpClientFactory = httpClientFactory;
    }

    public async Task ExchangeCodeForTokensAsync(string code)
    {
        var clientId = EnvVars.GetRequiredString(EnvVars.Keys.MeliClientId);
        var clientSecret = EnvVars.GetRequiredString(EnvVars.Keys.MeliClientSecret);
        var redirectUri = EnvVars.GetRequiredString(EnvVars.Keys.MeliRedirectUri);

        var body = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["code"] = code,
            ["redirect_uri"] = redirectUri
        };

        using var http = _httpClientFactory.CreateClient("meli-auth");
        var res = await http.PostAsync("oauth/token", new FormUrlEncodedContent(body));
        res.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        var access = root.GetProperty("access_token").GetString();
        var refresh = root.GetProperty("refresh_token").GetString();
        var expiresIn = root.GetProperty("expires_in").GetInt32();

        var exp = DateTimeOffset.UtcNow.AddSeconds(expiresIn); // Expiración real
        await _store.WriteAsync(access, exp, refresh!);

        // Update cache
        _cachedAccessToken = access;
        _cachedAccessTokenExp = exp;
    }

    public async Task<string> GetValidAccessTokenAsync()
    {
        // 1. Check in-memory cache
        if (!string.IsNullOrEmpty(_cachedAccessToken) && 
            _cachedAccessTokenExp.HasValue && 
            _cachedAccessTokenExp.Value > DateTimeOffset.UtcNow.Add(_expirationBuffer))
        {
            return _cachedAccessToken;
        }

        // 2. Read from store (maybe another instance updated it)
        var (access, exp, refresh) = await _store.ReadAsync();
        
        // Update cache from store
        if (!string.IsNullOrEmpty(access) && exp.HasValue)
        {
            _cachedAccessToken = access;
            _cachedAccessTokenExp = exp;

            if (exp.Value > DateTimeOffset.UtcNow.Add(_expirationBuffer))
            {
                return access!;
            }
        }

        // 3. Refresh if needed
        if (string.IsNullOrWhiteSpace(refresh))
        {
            throw new InvalidOperationException("No hay tokens disponibles para refrescar.");
        }

        var clientId = EnvVars.GetRequiredString(EnvVars.Keys.MeliClientId);
        var clientSecret = EnvVars.GetRequiredString(EnvVars.Keys.MeliClientSecret);

        var body = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["refresh_token"] = refresh!
        };

        using var http = _httpClientFactory.CreateClient("meli-auth");
        var res = await http.PostAsync("oauth/token", new FormUrlEncodedContent(body));
        res.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        var newAccess = root.GetProperty("access_token").GetString();
        var newRefresh = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : refresh;
        var expiresIn = root.GetProperty("expires_in").GetInt32();

        var newExp = DateTimeOffset.UtcNow.AddSeconds(expiresIn);
        await _store.WriteAsync(newAccess, newExp, newRefresh!);
        
        // Update cache
        _cachedAccessToken = newAccess;
        _cachedAccessTokenExp = newExp;

        return newAccess!;
    }
}
