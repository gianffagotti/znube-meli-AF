using Azure.Storage.Blobs;
using System.Text.Json;

namespace meli_znube_integration.Api;

public class TokensStoreBlob
{
    private readonly BlobClient _blob;

    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    public TokensStoreBlob()
    {
        var connectionString = EnvVars.GetRequiredString(EnvVars.Keys.AzureStorageConnectionString);
        var container = EnvVars.GetString(EnvVars.Keys.TokensContainer, "tokens");
        var name = EnvVars.GetString(EnvVars.Keys.TokensBlobName, "tokens.json");

        var containerClient = new BlobContainerClient(connectionString, container);
        containerClient.CreateIfNotExists();
        _blob = containerClient.GetBlobClient(name);
    }

    public async Task<(string? Access, DateTimeOffset? Exp, string? Refresh)> ReadAsync()
    {
        if (!await _blob.ExistsAsync()) return (null, null, null);
        using var ms = new MemoryStream();
        await _blob.DownloadToAsync(ms);
        ms.Position = 0;
        var data = await JsonSerializer.DeserializeAsync<TokenData>(ms, JsonOptions) ?? new TokenData();
        return (data.AccessToken, data.AccessTokenExp, data.RefreshToken);
    }

    public async Task WriteAsync(string? access, DateTimeOffset? exp, string refresh)
    {
        // Preservar campos no-Meli (p.ej. ZnubeToken) al escribir
        var existing = await ReadTokenDataAsync();
        existing.AccessToken = access;
        existing.AccessTokenExp = exp;
        existing.RefreshToken = refresh;

        var data = existing;
        using var ms = new MemoryStream();
        await JsonSerializer.SerializeAsync(ms, data, JsonOptions);
        ms.Position = 0;
        await _blob.UploadAsync(ms, overwrite: true);
    }

    public async Task<string?> GetZnubeTokenAsync()
    {
        var data = await ReadTokenDataAsync();
        return data.ZnubeToken;
    }

    public async Task WriteZnubeTokenAsync(string? znubeToken)
    {
        var existing = await ReadTokenDataAsync();
        existing.ZnubeToken = znubeToken;

        using var ms = new MemoryStream();
        await JsonSerializer.SerializeAsync(ms, existing, JsonOptions);
        ms.Position = 0;
        await _blob.UploadAsync(ms, overwrite: true);
    }

    private async Task<TokenData> ReadTokenDataAsync()
    {
        if (!await _blob.ExistsAsync()) return new TokenData();
        using var ms = new MemoryStream();
        await _blob.DownloadToAsync(ms);
        ms.Position = 0;
        var data = await JsonSerializer.DeserializeAsync<TokenData>(ms, JsonOptions) ?? new TokenData();
        return data;
    }

    private class TokenData
    {
        public string? AccessToken { get; set; }
        public DateTimeOffset? AccessTokenExp { get; set; }
        public string? RefreshToken { get; set; }
        public string? ZnubeToken { get; set; }
    }
}


