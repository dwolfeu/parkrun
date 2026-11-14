using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using HtmlAgilityPack;
using ParkrunScraper.Models;

namespace ParkrunScraper.Services;

public class ParkrunScraperService
{
    private readonly HttpClient _httpClient;

    public ParkrunScraperService(HttpClient? httpClient = null)
    {
        // A caller-supplied client (the shared, WAF-token-aware one from Program) is already
        // configured; only self-built clients need the default headers, and setting them on a
        // shared client would duplicate the headers once per service.
        if (httpClient != null)
        {
            _httpClient = httpClient;
            return;
        }

        _httpClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true });
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(ParkrunHttpClient.DefaultUserAgent);
        _httpClient.DefaultRequestHeaders.Accept.ParseAdd(
            "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8");
        _httpClient.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
    }

    public string BuildUrl(string clubNumOrUrl, string? eventDate = null)
    {
        string input = clubNumOrUrl.Trim();

        if (input.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            input.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrEmpty(eventDate) && !input.Contains("eventdate="))
            {
                string separator = input.Contains("?") ? "&" : "?";
                return $"{input}{separator}eventdate={eventDate}";
            }
            return input;
        }

        var match = Regex.Match(input, @"\d+");
        if (!match.Success)
        {
            throw new ArgumentException($"Invalid club number or URL: {clubNumOrUrl}");
        }

        string clubNum = match.Value;
        string url = $"https://www.parkrun.com/results/consolidatedclub/?clubNum={clubNum}";
        if (!string.IsNullOrEmpty(eventDate))
        {
            url += $"&eventdate={eventDate}";
        }
        return url;
    }

    public async Task<(ConsolidatedReportMetadata Metadata, List<ParkrunRecord> Records)> ScrapeConsolidatedClubAsync(string clubNumOrUrl, string? eventDate = null)
    {
        string html = "";
        string dataDir = Path.Combine(Directory.GetCurrentDirectory(), "data");
        if (!Directory.Exists(dataDir)) Directory.CreateDirectory(dataDir);

        if (File.Exists(clubNumOrUrl))
        {
            Console.WriteLine($"[Info] Loading club report from local file: {clubNumOrUrl}");
            html = await File.ReadAllTextAsync(clubNumOrUrl);
        }
        else
        {
            var match = Regex.Match(clubNumOrUrl, @"\d+");
            string clubNum = match.Success ? match.Value : "club";
            string cacheFileName = $"cache_{clubNum}_{eventDate ?? "latest"}.html";
            string cachePath = Path.Combine(dataDir, cacheFileName);

            string url = BuildUrl(clubNumOrUrl, eventDate);
            try
            {
                html = await FetchReportHtmlAsync(url, cachePath);
            }
            catch (Exception ex) when (File.Exists(cachePath))
            {
                // WAF block (after a token was tried) or a network error, with a cache to fall
                // back on — reuse it rather than failing the run.
                Console.WriteLine($"[Warning] {ex.Message} Falling back to cached file: {cachePath}");
                html = await File.ReadAllTextAsync(cachePath);
            }
        }

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var meta = new ConsolidatedReportMetadata();
        var records = new List<ParkrunRecord>();

        // 1. Extract Report Metadata (Club name, event date, total members, participants)
        var pNodes = doc.DocumentNode.SelectNodes("//p");
        if (pNodes != null)
        {
            foreach (var p in pNodes)
            {
                string pText = HtmlEntity.DeEntitize(p.InnerText).Trim();
                if (pText.Contains("This is a list of members of", StringComparison.OrdinalIgnoreCase))
                {
                    var clubMatch = Regex.Match(pText, @"members of (.*?) who participated", RegexOptions.IgnoreCase);
                    if (clubMatch.Success) meta.ClubName = clubMatch.Groups[1].Value.Trim();

                    var dateMatch = Regex.Match(pText, @"on (\d{4}-\d{2}-\d{2})");
                    if (dateMatch.Success) meta.EventDate = dateMatch.Groups[1].Value.Trim();

                    var countMatch = Regex.Match(pText, @"total (\d+) members, (\d+) took part");
                    if (countMatch.Success)
                    {
                        meta.TotalMembers = countMatch.Groups[1].Value.Trim();
                        meta.TotalParticipants = countMatch.Groups[2].Value.Trim();
                    }
                    break;
                }
            }
        }

        // 2. Parse Events & Tables
        var h2Nodes = doc.DocumentNode.SelectNodes("//h2");
        if (h2Nodes != null)
        {
            foreach (var h2 in h2Nodes)
            {
                string eventName = HtmlEntity.DeEntitize(h2.InnerText).Trim();
                if (string.IsNullOrEmpty(eventName)) continue;

                string eventNumber = "";
                string totalEventParticipants = "";
                HtmlNode? tableNode = null;

                // Traverse sibling elements following this h2 until next h2 or h1
                var sibling = h2.NextSibling;
                while (sibling != null && !sibling.Name.Equals("h2", StringComparison.OrdinalIgnoreCase) && !sibling.Name.Equals("h1", StringComparison.OrdinalIgnoreCase))
                {
                    if (sibling.Name.Equals("p", StringComparison.OrdinalIgnoreCase))
                    {
                        string pText = HtmlEntity.DeEntitize(sibling.InnerText).Trim();
                        var partMatch = Regex.Match(pText, @"total of (\d+) parkrunners", RegexOptions.IgnoreCase);
                        if (partMatch.Success) totalEventParticipants = partMatch.Groups[1].Value;

                        var evMatch = Regex.Match(pText, @"event #(\d+)", RegexOptions.IgnoreCase);
                        if (evMatch.Success) eventNumber = evMatch.Groups[1].Value;

                        var aEvent = sibling.SelectSingleNode(".//a");
                        if (aEvent != null)
                        {
                            string fullUrl = aEvent.GetAttributeValue("href", "");
                            if (!string.IsNullOrEmpty(fullUrl) && fullUrl.Contains("/results/") && !fullUrl.Contains("/consolidatedclub/"))
                            {
                                meta.EventResultUrls[eventName] = fullUrl;
                            }
                        }
                    }
                    else if (sibling.Name.Equals("table", StringComparison.OrdinalIgnoreCase))
                    {
                        tableNode = sibling;
                    }
                    sibling = sibling.NextSibling;
                }

                if (tableNode == null) continue;

                var trNodes = tableNode.SelectNodes(".//tr");
                if (trNodes == null) continue;

                foreach (var tr in trNodes)
                {
                    var tdNodes = tr.SelectNodes(".//td");
                    if (tdNodes == null || tdNodes.Count < 5) continue;

                    string overallPos = HtmlEntity.DeEntitize(tdNodes[0].InnerText).Trim();
                    string genderPos = HtmlEntity.DeEntitize(tdNodes[1].InnerText).Trim();
                    string runnerName = HtmlEntity.DeEntitize(tdNodes[2].InnerText).Trim();
                    string runnerClub = HtmlEntity.DeEntitize(tdNodes[3].InnerText).Trim();
                    string finishTime = HtmlEntity.DeEntitize(tdNodes[4].InnerText).Trim();

                    string profileUrl = "";
                    string parkrunnerId = "";
                    var aNode = tdNodes[2].SelectSingleNode(".//a");
                    if (aNode != null)
                    {
                        profileUrl = aNode.GetAttributeValue("href", "");
                        var idMatch = Regex.Match(profileUrl, @"/parkrunner/(\d+)");
                        if (idMatch.Success) parkrunnerId = idMatch.Groups[1].Value;
                    }

                    records.Add(new ParkrunRecord
                    {
                        EventDate = meta.EventDate,
                        ClubName = !string.IsNullOrEmpty(runnerClub) ? runnerClub : meta.ClubName,
                        EventName = eventName,
                        EventNumber = eventNumber,
                        OverallPosition = overallPos,
                        GenderPosition = genderPos,
                        Parkrunner = runnerName,
                        ParkrunnerId = parkrunnerId,
                        Time = finishTime,
                        EventTotalParticipants = totalEventParticipants,
                        ProfileUrl = profileUrl
                    });
                }
            }
        }

        return (meta, records);
    }

    /// <summary>
    /// Fetches the consolidated club report, caching a successful download. On a WAF CAPTCHA
    /// (either a challenge page or an HTTP 405) it tries once to obtain/reuse a token for the
    /// report's host and retries; if it still can't get past, it throws so the caller can fall
    /// back to any cached copy.
    /// </summary>
    private async Task<string> FetchReportHtmlAsync(string url, string cachePath)
    {
        string host = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : "";

        for (int attempt = 0; ; attempt++)
        {
            string html;
            try
            {
                html = await _httpClient.GetStringAsync(url);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.MethodNotAllowed)
            {
                if (attempt == 0 && ParkrunFetchHelper.TokenStore?.TryRefreshToken(host, url) == true)
                {
                    continue;
                }
                throw new InvalidOperationException("Parkrun WAF CAPTCHA (HTTP 405) on the club report.");
            }

            if (ParkrunFetchHelper.LooksLikeWafChallenge(html))
            {
                if (attempt == 0 && ParkrunFetchHelper.TokenStore?.TryRefreshToken(host, url) == true)
                {
                    continue;
                }
                throw new InvalidOperationException("Parkrun WAF CAPTCHA encountered on the club report.");
            }

            await File.WriteAllTextAsync(cachePath, html);
            return html;
        }
    }

    public static void SaveToCsv(List<ParkrunRecord> records, string outputPath)
    {
        if (records == null || records.Count == 0)
        {
            Console.WriteLine("[Warning] No runner records to save.");
            return;
        }

        string fullPath = ResolvePath(outputPath);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var sb = new StringBuilder();
        // Header (Removed: Event Number, Gender Position, Parkrunner ID)
        sb.AppendLine("Event Date,Club Name,Event Name,Overall Position,Parkrunner,Time,Event Total Participants,Profile URL");

        foreach (var r in records)
        {
            sb.AppendLine(string.Join(",",
                EscapeCsv(r.EventDate),
                EscapeCsv(r.ClubName),
                EscapeCsv(r.EventName),
                EscapeCsv(r.OverallPosition),
                EscapeCsv(r.Parkrunner),
                EscapeCsv(r.Time),
                EscapeCsv(r.EventTotalParticipants),
                EscapeCsv(r.ProfileUrl)
            ));
        }

        // Write UTF-8 with BOM for complete Excel compatibility
        File.WriteAllText(fullPath, sb.ToString(), new UTF8Encoding(true));
        Console.WriteLine($"[Success] Successfully written {records.Count:N0} records to: {fullPath}");
    }

    private static string EscapeCsv(string? field)
    {
        if (string.IsNullOrEmpty(field)) return "";
        if (field.Contains(",") || field.Contains("\"") || field.Contains("\n") || field.Contains("\r"))
        {
            return $"\"{field.Replace("\"", "\"\"")}\"";
        }
        return field;
    }

    public static string ResolvePath(string path)
    {
        if (path.StartsWith("~/") || path.StartsWith("~\\"))
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, path.Substring(2));
        }
        return Path.GetFullPath(path);
    }
}
