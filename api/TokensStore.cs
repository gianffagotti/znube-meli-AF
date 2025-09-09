using System.Text.Json;
using System.Text.Json.Serialization;

namespace meli_znube_integration.Api;

public class TokensStore
{
    private readonly string _filePath;

    public TokensStore(string filePath)
    {
        _filePath = filePath;
    }

    public async Task<OAuthTokens?> GetAsync()
    {
        if (!File.Exists(_filePath))
        {
            return null;
        }

        await using var stream = File.OpenRead(_filePath);
        using var document = await JsonDocument.ParseAsync(stream);
        var root = document.RootElement;

        var accessToken = root.TryGetProperty("access_token", out var at) ? at.GetString() : null;
        var refreshToken = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;

        DateTimeOffset? expiresAt = null;
        if (root.TryGetProperty("expires_at", out var ea) && ea.ValueKind == JsonValueKind.String)
        {
            if (DateTimeOffset.TryParse(ea.GetString(), out var parsed))
            {
                expiresAt = parsed;
            }
        }
        else if (root.TryGetProperty("expires_in", out var ei) && ei.ValueKind == JsonValueKind.Number)
        {
            var seconds = ei.GetInt32();
            expiresAt = DateTimeOffset.UtcNow.AddSeconds(seconds);
        }

        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return null;
        }

        return new OAuthTokens
        {
            AccessToken = accessToken!,
            RefreshToken = refreshToken,
            ExpiresAt = expiresAt ?? DateTimeOffset.UtcNow.AddMinutes(5)
        };
    }

    public async Task SaveAsync(OAuthTokens tokens)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory!);
        }

        var payload = new
        {
            access_token = tokens.AccessToken,
            refresh_token = tokens.RefreshToken,
            expires_at = tokens.ExpiresAt.ToUniversalTime().ToString("o")
        };

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        });

        await File.WriteAllTextAsync(_filePath, json);
    }
}

public class OAuthTokens
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = string.Empty;

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }
    
    public DateTimeOffset ExpiresAt { get; set; }
}


