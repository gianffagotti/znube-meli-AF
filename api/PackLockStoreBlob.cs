using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using System.Text;

namespace meli_znube_integration.Api;

// Usa Azure Blob Storage como sistema de locking por pack.
// - Contenedor configurable vía env `LOCKS_CONTAINER` (default: "packs").
// - Blob por pack: `packs/{packId}.lock` con create-if-not-exists (If-None-Match) para exclusión mutua.
// - Tras procesar, marca metadatos `done=true` y `ts`.
// Recomendación: configurar una regla de Lifecycle Management en la cuenta de Storage
// para eliminar blobs con prefijo `packs/` mayores a 3 días y así mantener costo ~0.
public class PackLockStoreBlob
{
    private readonly BlobContainerClient _container;

    public PackLockStoreBlob()
    {
        var connectionString = Environment.GetEnvironmentVariable("AZURE_STORAGE_CONNECTION_STRING")
            ?? throw new InvalidOperationException("Missing AZURE_STORAGE_CONNECTION_STRING");
        var container = Environment.GetEnvironmentVariable("LOCKS_CONTAINER") ?? "packs";
        _container = new BlobContainerClient(connectionString, container);
        _container.CreateIfNotExists();
    }

    public async Task<(bool Acquired, BlobClient Blob)> TryAcquireAsync(string packId)
    {
        var blob = _container.GetBlobClient($"packs/{packId}.lock");
        try
        {
            var content = new BinaryData(Encoding.UTF8.GetBytes(DateTimeOffset.UtcNow.ToString("o")));
            var conditions = new BlobRequestConditions { IfNoneMatch = new ETag("*") };
            await blob.UploadAsync(content, new BlobUploadOptions { Conditions = conditions });
            return (true, blob);
        }
        catch (RequestFailedException ex) when (ex.Status == 412 || ex.Status == 409)
        {
            return (false, blob);
        }
    }

    public async Task MarkDoneAsync(BlobClient blob)
    {
        try
        {
            var md = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["done"] = "true",
                ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString()
            };
            await blob.SetMetadataAsync(md);
        }
        catch { }
    }
}


