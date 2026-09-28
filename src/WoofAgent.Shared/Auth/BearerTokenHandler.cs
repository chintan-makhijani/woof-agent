using System.Net;
using System.Net.Http.Headers;

namespace WoofAgent.Shared.Auth;

/// <summary>
/// Attaches the current token from a <see cref="FileTokenStore"/> to every outgoing request,
/// so a token refreshed on disk is used by the next MCP call without reconnecting.
/// </summary>
public sealed class BearerTokenHandler : DelegatingHandler
{
    private readonly FileTokenStore _store;
    private readonly string _serviceName;

    public BearerTokenHandler(FileTokenStore store, string serviceName)
        : base(new HttpClientHandler())
    {
        _store = store;
        _serviceName = serviceName;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = _store.GetCurrent();
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

        var response = await base.SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            Console.Error.WriteLine(
                $"[{_serviceName}] 401 Unauthorized — token expired or revoked. " +
                "Run: python3 scripts/swiggy_token.py login (a running session picks up the new token automatically)");
        }

        return response;
    }
}
