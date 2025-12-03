using System.Net.Http.Headers;
using meli_znube_integration.Services;

namespace meli_znube_integration.Infrastructure;

public class MeliTokenHandler : DelegatingHandler
{
    private readonly MeliAuth _auth;

    public MeliTokenHandler(MeliAuth auth)
    {
        _auth = auth;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var accessToken = await _auth.GetValidAccessTokenAsync();
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        return await base.SendAsync(request, cancellationToken);
    }
}
