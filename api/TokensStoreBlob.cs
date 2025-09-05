using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Azure.Storage.Blobs;

namespace meli_znube_integration.Api
{
    public class TokensStoreBlob
    {
        private readonly BlobClient _blob;

        public TokensStoreBlob()
        {
            var connectionString = Environment.GetEnvironmentVariable("AZURE_STORAGE_CONNECTION_STRING")
                      ?? throw new InvalidOperationException("Missing AZURE_STORAGE_CONNECTION_STRING");
            var container = Environment.GetEnvironmentVariable("TOKENS_CONTAINER") ?? "secrets";
            var name = Environment.GetEnvironmentVariable("TOKENS_BLOB_NAME") ?? "meli-tokens.json";

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
}


