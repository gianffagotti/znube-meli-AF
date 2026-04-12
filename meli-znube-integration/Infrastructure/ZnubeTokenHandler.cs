using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace meli_znube_integration.Infrastructure;

public class ZnubeTokenHandler : DelegatingHandler
{
    private readonly TokensStoreBlob _store;

    public ZnubeTokenHandler(TokensStoreBlob store)
    {
        _store = store;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var sentToken = await _store.GetZnubeTokenAsync();
        if (string.IsNullOrWhiteSpace(sentToken))
        {
            throw new InvalidOperationException("ZnubeToken no disponible en el blob de tokens.");
        }

        request.Headers.Remove("zNube-token");
        request.Headers.TryAddWithoutValidation("zNube-token", sentToken);

        var response = await base.SendAsync(request, cancellationToken);

        if (response.Headers.TryGetValues("zNube-token", out var values))
        {
            var responseToken = values is null ? null : System.Linq.Enumerable.FirstOrDefault(values);
            if (!string.IsNullOrWhiteSpace(responseToken) && !string.Equals(responseToken, sentToken, StringComparison.Ordinal))
            {
                await _store.WriteZnubeTokenAsync(responseToken);
            }
        }

        return response;
    }
}