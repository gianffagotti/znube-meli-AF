using Azure.Storage.Blobs;
using System.Text.Json;

namespace meli_znube_integration.Api;

public class TokensStoreBlob
{
    private readonly BlobClient _blob;

    public TokensStoreBlob()
    {
        var connectionString = EnvVars.GetRequiredString(EnvVars.Keys.AzureStorageConnectionString);
        var container = EnvVars.GetString(EnvVars.Keys.TokensContainer, "secrets");
        var name = EnvVars.GetString(EnvVars.Keys.TokensBlobName, "meli-tokens.json");

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
        var data = await JsonSerializer.DeserializeAsync<TokenData>(ms) ?? new TokenData();
        return (data.AccessToken, data.AccessTokenExp, data.RefreshToken);
    }

    public async Task WriteAsync(string? access, DateTimeOffset? exp, string refresh)
    {
        var data = new TokenData { AccessToken = access, AccessTokenExp = exp, RefreshToken = refresh };
        using var ms = new MemoryStream();
        await JsonSerializer.SerializeAsync(ms, data);
        ms.Position = 0;
        await _blob.UploadAsync(ms, overwrite: true);
    }

    private class TokenData
    {
        public string? AccessToken { get; set; }
        public DateTimeOffset? AccessTokenExp { get; set; }
        public string? RefreshToken { get; set; }
    }
}


