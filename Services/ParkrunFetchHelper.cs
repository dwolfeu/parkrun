using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace ParkrunScraper.Services;

/// <summary>How a single event's results page was obtained.</summary>
public enum EventFetchOutcome
{
    /// <summary>Read from the local <c>data/events_&lt;date&gt;/</c> cache — no network hit.</summary>
    FromCache,
    /// <summary>Downloaded live and written to the cache.</summary>
    Downloaded,
    /// <summary>Could not be obtained (WAF/405 block or network error); left uncached for a later re-run.</summary>
    Blocked
}

/// <summary>
/// Shared, polite fetcher for parkrun per-event results pages.
///
/// parkrun sits behind AWS WAF, which starts serving a CAPTCHA challenge
/// (returned as HTTP 405) once it sees a burst of automated requests. This
/// helper spaces requests out and retries with backoff to stay under that
/// threshold, so a single slow run can fetch all events without tripping it.
///
/// Every successfully fetched page is written to the local cache, so runs are
/// resumable: anything already cached is reused instantly and never re-fetched,
/// and only blocked/missing events are retried on the next run.
/// </summary>
public static class ParkrunFetchHelper
{
    /// <summary>
    /// Politeness delay before every live network request (ms). Defaults to 5000.
    /// Override via the <c>--delay</c> CLI flag (seconds) or the
    /// <c>PARKRUN_REQUEST_DELAY_MS</c> environment variable (milliseconds).
    /// </summary>
    public static int RequestDelayMs { get; set; } = ResolveDefaultDelayMs();

    private static int ResolveDefaultDelayMs()
    {
        const int fallback = 5000;
        string? env = Environment.GetEnvironmentVariable("PARKRUN_REQUEST_DELAY_MS");
        if (!string.IsNullOrWhiteSpace(env) && int.TryParse(env, out int ms) && ms >= 0)
        {
            return ms;
        }
        return fallback;
    }

    /// <summary>Backoff waits after a failed/blocked attempt. Length also sets the retry count.</summary>
    private static readonly int[] RetryBackoffMs = { 3000, 8000 };

    /// <summary>
    /// Normalizes an event's results URL (prefixing the UK domain for relative links)
    /// and derives its cache file path, using the same slug/number scheme both services share.
    /// </summary>
    public static (string Url, string CachePath) ResolveEvent(string cacheBaseDir, string eventName, string eventUrl)
    {
        if (!eventUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !eventUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            eventUrl = "https://www.parkrun.org.uk" + (eventUrl.StartsWith("/") ? "" : "/") + eventUrl;
        }

        var m = Regex.Match(eventUrl, @"parkrun\.[a-z\.]+/([^/]+)/results/(\d+)");
        string slug = m.Success ? m.Groups[1].Value : Regex.Replace(eventName, @"[^a-zA-Z0-9]", "_").ToLowerInvariant();
        string num = m.Success ? m.Groups[2].Value : "latest";
        return (eventUrl, Path.Combine(cacheBaseDir, $"{slug}_{num}.html"));
    }

    /// <summary>
    /// Prints a heads-up before an event-fetching pass: how many events there are and
    /// how many are already cached, so a fully-cached (silent) re-run reads clearly
    /// instead of looking stalled.
    /// </summary>
    public static void LogPassStart(string passName, IReadOnlyDictionary<string, string> eventResultUrls, string cacheBaseDir)
    {
        int total = eventResultUrls.Count;
        int cached = eventResultUrls.Count(kvp => File.Exists(ResolveEvent(cacheBaseDir, kvp.Key, kvp.Value).CachePath));
        int toFetch = total - cached;
        Console.WriteLine($"[Info]     {passName}: {total} events — {cached} already cached, fetching up to {toFetch}.");
    }

    /// <summary>
    /// Returns the results-page HTML for one event — from the local cache if present,
    /// otherwise fetched live (throttled + retried) and saved to the cache.
    ///
    /// Emits one explicit status line per event so cache hits, live downloads and
    /// WAF/405 blocks are all visible in the console. Returns null HTML when the page
    /// could not be obtained; that event stays uncached and is retried on the next run.
    /// </summary>
    public static async Task<(string? Html, EventFetchOutcome Outcome)> GetEventHtmlAsync(
        HttpClient client, string eventName, string eventUrl, string cachePath, int index, int total)
    {
        string position = total > 0 ? $"[{index}/{total}] " : "";

        // Cache hit — no network, no delay. Keeps re-runs fast and makes them resumable.
        if (File.Exists(cachePath))
        {
            return (await File.ReadAllTextAsync(cachePath), EventFetchOutcome.FromCache);
        }

        var (html, error) = await FetchEventHtmlAsync(client, eventUrl);
        if (html == null)
        {
            Console.WriteLine($"[Blocked]  {position}{eventName} — {error}; not cached, will retry on next run.");
            return (null, EventFetchOutcome.Blocked);
        }

        await File.WriteAllTextAsync(cachePath, html);
        Console.WriteLine($"[Fetched]  {position}{eventName} — downloaded and cached.");
        return (html, EventFetchOutcome.Downloaded);
    }

    /// <summary>
    /// Prints a one-line summary of an event-fetching pass, including guidance to
    /// re-run when some events were blocked by the WAF.
    /// </summary>
    public static void LogPassSummary(string passName, int fetched, int fromCache, int blocked)
    {
        Console.WriteLine(
            $"[Summary]  {passName}: {fetched} downloaded, {fromCache} from cache, {blocked} blocked.");
        if (blocked > 0)
        {
            Console.WriteLine(
                $"[Summary]  {blocked} event(s) were blocked by parkrun's AWS WAF (HTTP 405 CAPTCHA). " +
                "Re-run the same command to retry only those — already-cached events are reused instantly.");
        }
    }

    /// <summary>
    /// Fetches an event results page with throttling and retry/backoff.
    /// Returns the HTML on success, or (null, lastError) if it stays blocked.
    /// </summary>
    private static async Task<(string? Html, string? Error)> FetchEventHtmlAsync(HttpClient client, string url)
    {
        string? lastError = null;
        int maxAttempts = RetryBackoffMs.Length + 1;

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            // Space out live requests so we never look like a bot burst to the WAF.
            await Task.Delay(RequestDelayMs);

            try
            {
                string html = await client.GetStringAsync(url);
                if (LooksLikeWafChallenge(html))
                {
                    lastError = "AWS WAF CAPTCHA challenge page";
                }
                else
                {
                    return (html, null);
                }
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.MethodNotAllowed)
            {
                // The telltale sign of the WAF CAPTCHA block — call it out explicitly.
                lastError = "HTTP 405 (AWS WAF CAPTCHA block)";
            }
            catch (Exception ex)
            {
                // e.g. a timeout surfaces as TaskCanceledException.
                lastError = ex.Message;
            }

            if (attempt < RetryBackoffMs.Length)
            {
                await Task.Delay(RetryBackoffMs[attempt]);
            }
        }

        return (null, lastError);
    }

    /// <summary>True if the body is an AWS WAF challenge page rather than real results.</summary>
    public static bool LooksLikeWafChallenge(string? html) =>
        !string.IsNullOrEmpty(html) &&
        (html.Contains("Amazon WAF", StringComparison.OrdinalIgnoreCase) ||
         html.Contains("x-amzn-waf", StringComparison.OrdinalIgnoreCase) ||
         html.Contains("awswaf", StringComparison.OrdinalIgnoreCase));
}
