using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace ParkrunScraper.Services;

/// <summary>
/// Transparently attaches the right per-host AWS WAF token (and, when set, the matching
/// User-Agent) to every outgoing request, by reading them live from a shared
/// <see cref="WafTokenStore"/>. Because it sits in the <see cref="HttpClient"/> pipeline,
/// all call sites — the club report, per-event pages, and runner profiles — get the token
/// for whatever domain they happen to hit, with no changes at the call site.
/// Reading the store live means a token obtained mid-run (via a prompt) applies to the
/// very next request without rebuilding the client.
/// </summary>
public sealed class WafTokenHandler : DelegatingHandler
{
    private readonly WafTokenStore _store;

    public WafTokenHandler(WafTokenStore store, HttpMessageHandler innerHandler)
        : base(innerHandler)
    {
        _store = store;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string? host = request.RequestUri?.Host;
        if (!string.IsNullOrEmpty(host))
        {
            string? cookie = _store.GetCookieHeader(host);
            if (!string.IsNullOrEmpty(cookie) && !request.Headers.Contains("Cookie"))
            {
                // TryAddWithoutValidation: aws-waf-token values contain '/', '+', ':' etc.
                // that the strict Cookie parser would reject.
                request.Headers.TryAddWithoutValidation("Cookie", cookie);
            }

            // The token is bound to the UA it was solved under, so a request-level UA
            // (which overrides the client's default) must match it exactly.
            if (!string.IsNullOrEmpty(_store.UserAgent))
            {
                request.Headers.UserAgent.Clear();
                request.Headers.TryAddWithoutValidation("User-Agent", _store.UserAgent);
            }
        }

        return base.SendAsync(request, cancellationToken);
    }
}

/// <summary>Builds the shared <see cref="HttpClient"/> wired to inject WAF tokens.</summary>
public static class ParkrunHttpClient
{
    /// <summary>Fallback browser UA, used until/unless the user supplies the UA their token was solved under.</summary>
    public const string DefaultUserAgent =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36";

    public static HttpClient Create(WafTokenStore store)
    {
        var handler = new WafTokenHandler(store, new HttpClientHandler { AllowAutoRedirect = true });
        var client = new HttpClient(handler);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(DefaultUserAgent);
        client.DefaultRequestHeaders.Accept.ParseAdd(
            "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        return client;
    }
}
