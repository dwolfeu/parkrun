using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace ParkrunScraper.Services;

/// <summary>
/// Holds the AWS WAF <c>aws-waf-token</c> cookies the user has supplied, keyed by host
/// (e.g. <c>www.parkrun.com</c>, <c>www.parkrun.org.uk</c>), plus the single User-Agent
/// the tokens were solved under.
///
/// parkrun's WAF binds each token to the <b>IP</b> and the <b>User-Agent</b> of the
/// browser that solved its CAPTCHA, so a token is only accepted when replayed with that
/// same UA from the same machine. The token is also per-domain (a <c>.com</c> token is a
/// different cookie from a <c>.org.uk</c> one), hence the host-keyed map.
///
/// Tokens are entirely optional: a run only needs one for a host once that host actually
/// serves a CAPTCHA. They can be pre-seeded from a JSON file (<see cref="Load"/>) or the
/// command line, and otherwise the store prompts for one lazily the first time a host
/// blocks (<see cref="TryRefreshToken"/>). Any token entered at a prompt is written back
/// to the file so later runs reuse it until it expires.
/// </summary>
public class WafTokenStore
{
    private readonly object _gate = new();
    private readonly object _promptGate = new();
    private readonly Dictionary<string, string> _tokens; // host -> "name=value" Cookie header
    private readonly string? _persistPath;

    // Hosts we've already prompted for this run (success or decline) — ask at most once each.
    private readonly HashSet<string> _prompted = new(StringComparer.OrdinalIgnoreCase);

    // Once a prompt times out we assume the user has walked away (e.g. an overnight run)
    // and stop prompting entirely so the process never stalls again.
    private bool _assumeUnattended;

    /// <summary>The User-Agent the tokens were solved under; applied to every request when set.</summary>
    public string? UserAgent { get; private set; }

    /// <summary>How long to wait for a pasted token before continuing unattended.</summary>
    public TimeSpan PromptTimeout { get; set; } = TimeSpan.FromSeconds(300);

    public WafTokenStore(Dictionary<string, string>? tokens = null, string? userAgent = null, string? persistPath = null)
    {
        _tokens = tokens ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        UserAgent = string.IsNullOrWhiteSpace(userAgent) ? null : userAgent.Trim();
        _persistPath = persistPath;
    }

    /// <summary>Hosts with a token currently seeded (for a startup info line).</summary>
    public IReadOnlyCollection<string> Hosts
    {
        get { lock (_gate) { return _tokens.Keys.ToArray(); } }
    }

    /// <summary>Loads a token store from <paramref name="persistPath"/> (missing file => empty store).</summary>
    public static WafTokenStore Load(string? persistPath)
    {
        var tokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? ua = null;

        if (!string.IsNullOrEmpty(persistPath) && File.Exists(persistPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(persistPath));
                var root = doc.RootElement;
                if (root.TryGetProperty("userAgent", out var uaEl) && uaEl.ValueKind == JsonValueKind.String)
                {
                    ua = uaEl.GetString();
                }
                if (root.TryGetProperty("tokens", out var tEl) && tEl.ValueKind == JsonValueKind.Object)
                {
                    foreach (var p in tEl.EnumerateObject())
                    {
                        string? v = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : null;
                        if (!string.IsNullOrWhiteSpace(v))
                        {
                            tokens[p.Name] = NormalizeCookie(v!);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Warning] Could not read WAF token file {persistPath}: {ex.Message}");
            }
        }

        return new WafTokenStore(tokens, ua, persistPath);
    }

    /// <summary>Overrides the User-Agent (e.g. from the <c>--user-agent</c> flag). Blank is ignored.</summary>
    public void SetUserAgent(string? ua)
    {
        if (!string.IsNullOrWhiteSpace(ua)) UserAgent = ua.Trim();
    }

    /// <summary>Seeds/overrides the token for a host (e.g. from a <c>--token host=value</c> flag).</summary>
    public void SetToken(string host, string rawToken)
    {
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(rawToken)) return;
        lock (_gate) { _tokens[host] = NormalizeCookie(rawToken); }
    }

    /// <summary>The <c>Cookie</c> header value to send to <paramref name="host"/>, or null if none seeded.</summary>
    public string? GetCookieHeader(string host)
    {
        lock (_gate) { return _tokens.TryGetValue(host, out var v) ? v : null; }
    }

    /// <summary>
    /// Called when <paramref name="host"/> just served a WAF CAPTCHA. When running
    /// interactively and this host hasn't already been asked about this run, prompts the
    /// user to solve the CAPTCHA in a browser and paste a fresh token (and the matching
    /// User-Agent, the first time one is needed).
    ///
    /// Returns true if a new token was captured and the caller should retry; false to fall
    /// back to backoff/skip — i.e. non-interactive, already-asked, declined, or timed out.
    /// Asks at most once per host per run; after the first timeout it goes fully unattended.
    /// </summary>
    public bool TryRefreshToken(string host, string blockedUrl)
    {
        if (string.IsNullOrEmpty(host)) return false;

        // Serialize prompts so two blocked requests never race for the console.
        lock (_promptGate)
        {
            lock (_gate)
            {
                if (_assumeUnattended || _prompted.Contains(host)) return false;
            }
            if (Console.IsInputRedirected)
            {
                // No interactive console to prompt on — behave like the polite-backoff default.
                lock (_gate) { _assumeUnattended = true; }
                return false;
            }
            lock (_gate) { _prompted.Add(host); } // ask at most once per host, whatever the outcome

            Console.WriteLine();
            Console.WriteLine(new string('=', 78));
            Console.WriteLine($"parkrun's AWS WAF served a CAPTCHA for {host}.");
            Console.WriteLine("To get past it:");
            Console.WriteLine("  1. Open this URL in a browser and solve the CAPTCHA until the page loads:");
            Console.WriteLine($"       {blockedUrl}");
            Console.WriteLine("  2. In that tab's DevTools Console, copy the token to your clipboard:");
            Console.WriteLine("       copy(document.cookie.split('; ').find(c => c.startsWith('aws-waf-token=')))");
            Console.WriteLine("  The token is bound to your IP and User-Agent, so solve it on THIS machine.");
            Console.WriteLine(new string('=', 78));

            if (string.IsNullOrEmpty(UserAgent))
            {
                string? ua = PromptLine("Paste the User-Agent of that browser (blank to skip tokens): ");
                if (string.IsNullOrWhiteSpace(ua))
                {
                    if (ua == null) lock (_gate) { _assumeUnattended = true; } // timed out
                    return false;
                }
                UserAgent = ua.Trim();
            }

            string? token = PromptLine($"Paste the aws-waf-token for {host} (blank to skip): ");
            if (string.IsNullOrWhiteSpace(token))
            {
                if (token == null) lock (_gate) { _assumeUnattended = true; } // timed out
                return false;
            }

            lock (_gate) { _tokens[host] = NormalizeCookie(token.Trim()); }
            Save();
            Console.WriteLine($"[Info] Token stored for {host}; retrying.");
            return true;
        }
    }

    /// <summary>Persists the current tokens + UA back to the token file (no-op without a path).</summary>
    public void Save()
    {
        if (string.IsNullOrEmpty(_persistPath)) return;
        try
        {
            Dictionary<string, string> snapshot;
            string? ua;
            lock (_gate) { snapshot = new Dictionary<string, string>(_tokens); ua = UserAgent; }

            string? dir = Path.GetDirectoryName(_persistPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var payload = new { userAgent = ua, tokens = snapshot };
            File.WriteAllText(_persistPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Warning] Could not save WAF token file: {ex.Message}");
        }
    }

    /// <summary>
    /// Reads one line with a timeout. Returns the text (possibly empty) if the user
    /// responds, or null if <see cref="PromptTimeout"/> elapses first — the caller then
    /// continues unattended. The orphaned read is harmless: a timeout flips the store to
    /// unattended, so it is never prompted again.
    /// </summary>
    private string? PromptLine(string prompt)
    {
        Console.Write(prompt);
        var readTask = Task.Run(() => Console.ReadLine());
        if (readTask.Wait(PromptTimeout))
        {
            return readTask.Result ?? "";
        }
        Console.WriteLine();
        Console.WriteLine($"[Info] No input within {PromptTimeout.TotalSeconds:0}s — continuing unattended (tokens disabled for the rest of this run).");
        return null;
    }

    /// <summary>
    /// Normalizes a pasted value into a <c>Cookie</c> header. Accepts a bare token value,
    /// a single <c>aws-waf-token=...</c> pair (what Chrome's <c>document.cookie</c> yields),
    /// or a full <c>name=value; ...</c> header. A bare value is assumed to be the aws-waf-token.
    /// </summary>
    private static string NormalizeCookie(string raw)
    {
        raw = raw.Trim().Trim('"', '\'');
        int eq = raw.IndexOf('=');
        if (eq > 0)
        {
            string name = raw.Substring(0, eq);
            if (Regex.IsMatch(name, @"^[A-Za-z0-9_\-]+$"))
            {
                return raw; // already name=value (optionally a multi-pair header)
            }
        }
        return "aws-waf-token=" + raw;
    }
}
