using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using HtmlAgilityPack;
using ParkrunScraper.Models;

namespace ParkrunScraper.Services;

public class ParkrunVolunteerService
{
    private readonly HttpClient _httpClient;
    private readonly string _storeFilePath;

    public ParkrunVolunteerService(HttpClient? httpClient = null, string? storeFilePath = null)
    {
        _httpClient = httpClient ?? new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = true
        });

        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");
        _httpClient.DefaultRequestHeaders.Accept.ParseAdd(
            "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8");
        _httpClient.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");

        if (!string.IsNullOrEmpty(storeFilePath))
        {
            _storeFilePath = storeFilePath;
        }
        else
        {
            string projectDataDir = Path.Combine(Directory.GetCurrentDirectory(), "data");
            if (!Directory.Exists(projectDataDir))
            {
                Directory.CreateDirectory(projectDataDir);
            }
            _storeFilePath = Path.Combine(projectDataDir, "volunteers.json");
        }
    }

    public async Task<List<ParkrunVolunteerProfile>> GetWeeklyEventVolunteersAsync(
        Dictionary<string, string> eventResultUrls,
        string targetClubName,
        string eventDate,
        bool forceRefresh = false,
        IProgress<string>? progress = null)
    {
        var store = LoadStore();
        var weeklyVolunteers = new Dictionary<string, (string Name, string EventName, string RolesThisWeek, int EventCredits)>();

        string cacheBaseDir = Path.Combine(Directory.GetCurrentDirectory(), "data", $"events_{eventDate}");
        if (!Directory.Exists(cacheBaseDir))
        {
            Directory.CreateDirectory(cacheBaseDir);
        }

        // 1. Scrape volunteer rosters across all attended events for this week
        int total = eventResultUrls.Count;
        int index = 0, fetchedCount = 0, cachedCount = 0, blockedCount = 0;
        ParkrunFetchHelper.LogPassStart("Volunteer rosters", eventResultUrls, cacheBaseDir);
        foreach (var kvp in eventResultUrls)
        {
            index++;
            string eventName = kvp.Key;
            var (eventUrl, cachePath) = ParkrunFetchHelper.ResolveEvent(cacheBaseDir, eventName, kvp.Value);

            var (eventHtml, outcome) = await ParkrunFetchHelper.GetEventHtmlAsync(_httpClient, eventName, eventUrl, cachePath, index, total);
            if (outcome == EventFetchOutcome.FromCache) cachedCount++;
            else if (outcome == EventFetchOutcome.Downloaded) fetchedCount++;
            else blockedCount++;

            if (string.IsNullOrEmpty(eventHtml)) continue;

            // Parse <tr class="Volunteers-table-row">
            var rowMatches = Regex.Matches(eventHtml, @"<tr class=[\x27\x22]Volunteers-table-row[\x27\x22](.*?)</tr>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            foreach (Match row in rowMatches)
            {
                string rContent = row.Groups[1].Value;
                var clubMatch = Regex.Match(rContent, @"data-club=[\x27\x22]([^\x27\x22]*)[\x27\x22]", RegexOptions.IgnoreCase);
                // data-club comes straight from raw HTML, so it is still entity-encoded
                // (e.g. "Flag &amp; Pole Runners"). Decode it before comparing against the
                // already-decoded club name, otherwise names containing &, ', etc. never match.
                string club = clubMatch.Success ? System.Net.WebUtility.HtmlDecode(clubMatch.Groups[1].Value).Trim() : "";

                bool isClubMember = false;
                if (!string.IsNullOrEmpty(club))
                {
                    if (club.Contains(targetClubName, StringComparison.OrdinalIgnoreCase) ||
                        (targetClubName.Contains("Swifts", StringComparison.OrdinalIgnoreCase) && club.Contains("Swifts", StringComparison.OrdinalIgnoreCase)))
                    {
                        isClubMember = true;
                    }
                }

                if (!isClubMember) continue;

                var nameMatch = Regex.Match(rContent, @"data-name=[\x27\x22]([^\x27\x22]*)[\x27\x22]", RegexOptions.IgnoreCase);
                var roleMatch = Regex.Match(rContent, @"data-role=[\x27\x22]([^\x27\x22]*)[\x27\x22]", RegexOptions.IgnoreCase);
                var credMatch = Regex.Match(rContent, @"data-volunteercredits=[\x27\x22]([^\x27\x22]*)[\x27\x22]", RegexOptions.IgnoreCase);
                var idMatch = Regex.Match(rContent, @"parkrunner/(\d+)", RegexOptions.IgnoreCase);

                string pName = nameMatch.Success ? nameMatch.Groups[1].Value.Trim() : "";
                string pId = idMatch.Success ? idMatch.Groups[1].Value.Trim() : "";
                string pRoleRaw = roleMatch.Success ? roleMatch.Groups[1].Value.Trim() : "";
                int pCredits = credMatch.Success && int.TryParse(credMatch.Groups[1].Value, out int cVal) ? cVal : 0;

                if (string.IsNullOrEmpty(pId)) continue;

                // Clean role string: split by comma, trim, filter empty, join with ", "
                string cleanRole = string.Join(", ", pRoleRaw.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()));
                if (string.IsNullOrEmpty(cleanRole)) cleanRole = "Volunteer";

                if (weeklyVolunteers.TryGetValue(pId, out var existing))
                {
                    string combinedRole = existing.RolesThisWeek;
                    if (!combinedRole.Contains(cleanRole, StringComparison.OrdinalIgnoreCase))
                    {
                        combinedRole = $"{combinedRole}, {cleanRole}";
                    }
                    weeklyVolunteers[pId] = (existing.Name, existing.EventName, combinedRole, Math.Max(existing.EventCredits, pCredits));
                }
                else
                {
                    weeklyVolunteers[pId] = (pName, eventName, cleanRole, pCredits);
                }
            }
        }

        ParkrunFetchHelper.LogPassSummary("Volunteer rosters", fetchedCount, cachedCount, blockedCount);

        // 2. Fetch or load detailed volunteer profiles for each active volunteer
        var resultProfiles = new List<ParkrunVolunteerProfile>();
        foreach (var kvp in weeklyVolunteers)
        {
            string pId = kvp.Key;
            var (pName, eventName, rolesThisWeek, eventCredits) = kvp.Value;

            ParkrunVolunteerProfile profile;
            if (!forceRefresh &&
                store.Profiles.TryGetValue(pId, out var cached) &&
                (!string.IsNullOrEmpty(cached.TopRolesSummary) && cached.TopRolesSummary != "-"))
            {
                profile = cached;
                profile.ParkrunnerName = pName;
            }
            else
            {
                progress?.Report($"Fetching full volunteer profile for {pName} (A{pId})...");
                profile = await ScrapeVolunteerProfileAsync(pId, pName);
                store.Profiles[pId] = profile;
            }

            if (eventCredits > profile.TotalCredits)
            {
                profile.TotalCredits = eventCredits;
            }

            string computedMilestone = GetHighestMilestoneFromCredits(profile.TotalCredits);
            if (GetMilestoneWeight(computedMilestone) > GetMilestoneWeight(profile.HighestMilestone))
            {
                profile.HighestMilestone = computedMilestone;
            }

            profile.EventName = eventName;
            profile.RoleThisWeek = rolesThisWeek;
            profile.VolunteeredThisWeek = true;

            resultProfiles.Add(profile);
        }

        // 3. Record snapshot & week-over-week deltas
        if (!store.Snapshots.ContainsKey(eventDate))
        {
            store.Snapshots[eventDate] = new Dictionary<string, int>();
        }

        foreach (var p in resultProfiles)
        {
            store.Snapshots[eventDate][p.ParkrunnerId] = p.TotalCredits;
        }

        var priorDates = store.Snapshots.Keys
            .Where(d => string.Compare(d, eventDate, StringComparison.Ordinal) < 0)
            .OrderByDescending(d => d)
            .ToList();

        if (priorDates.Count > 0)
        {
            string mostRecentPriorDate = priorDates.First();
            var priorSnapshot = store.Snapshots[mostRecentPriorDate];

            foreach (var p in resultProfiles)
            {
                if (priorSnapshot.TryGetValue(p.ParkrunnerId, out int priorCredits))
                {
                    p.DeltaCredits = Math.Max(0, p.TotalCredits - priorCredits);
                }
            }
        }

        SaveStore(store);

        // Sort by TotalCredits descending, then milestone weight, then name
        return resultProfiles
            .OrderByDescending(p => p.TotalCredits)
            .ThenByDescending(p => GetMilestoneWeight(p.HighestMilestone))
            .ThenBy(p => p.ParkrunnerName)
            .ToList();
    }

    public async Task<List<ParkrunVolunteerProfile>> GetVolunteerProfilesAsync(
        IEnumerable<ParkrunRecord> records,
        string eventDate,
        bool forceRefresh = false,
        IProgress<string>? progress = null)
    {
        var store = LoadStore();

        // Distinct runners by ParkrunnerId
        var distinctRunners = records
            .Where(r => !string.IsNullOrEmpty(r.ParkrunnerId))
            .GroupBy(r => r.ParkrunnerId)
            .Select(g => (Id: g.Key, Name: g.First().Parkrunner))
            .ToList();

        var profiles = new ConcurrentBag<ParkrunVolunteerProfile>();
        var semaphore = new SemaphoreSlim(4); // Max 4 concurrent HTTP requests to be polite

        var tasks = distinctRunners.Select(async runner =>
        {
            await semaphore.WaitAsync();
            try
            {
                // Check cache if fresh (fetched within 24 hours) and forceRefresh is false
                if (!forceRefresh &&
                    store.Profiles.TryGetValue(runner.Id, out var cached) &&
                    (DateTime.UtcNow - cached.LastFetched).TotalHours < 24)
                {
                    cached.ParkrunnerName = runner.Name; // Keep name fresh
                    profiles.Add(cached);
                    return;
                }

                // Scrape profile
                progress?.Report($"Fetching volunteer profile for {runner.Name} (A{runner.Id})...");
                var profile = await ScrapeVolunteerProfileAsync(runner.Id, runner.Name);
                profiles.Add(profile);

                // Update store
                store.Profiles[runner.Id] = profile;

                // Small polite delay between requests
                await Task.Delay(80);
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);

        // Record Snapshot for this event date
        if (!store.Snapshots.ContainsKey(eventDate))
        {
            store.Snapshots[eventDate] = new Dictionary<string, int>();
        }

        foreach (var p in profiles)
        {
            store.Snapshots[eventDate][p.ParkrunnerId] = p.TotalCredits;
        }

        // Calculate Week-over-Week Deltas against the most recent prior snapshot
        var priorDates = store.Snapshots.Keys
            .Where(d => string.Compare(d, eventDate, StringComparison.Ordinal) < 0)
            .OrderByDescending(d => d)
            .ToList();

        if (priorDates.Count > 0)
        {
            string mostRecentPriorDate = priorDates.First();
            var priorSnapshot = store.Snapshots[mostRecentPriorDate];

            foreach (var p in profiles)
            {
                if (priorSnapshot.TryGetValue(p.ParkrunnerId, out int priorCredits))
                {
                    p.DeltaCredits = Math.Max(0, p.TotalCredits - priorCredits);
                }
            }
        }

        // Save updated store
        SaveStore(store);

        // Return sorted: TotalCredits descending, then highest milestone tier, then name
        return profiles
            .OrderByDescending(p => p.TotalCredits)
            .ThenByDescending(p => GetMilestoneWeight(p.HighestMilestone))
            .ThenBy(p => p.ParkrunnerName)
            .ToList();
    }

    private async Task<ParkrunVolunteerProfile> ScrapeVolunteerProfileAsync(string parkrunnerId, string runnerName)
    {
        var profile = new ParkrunVolunteerProfile
        {
            ParkrunnerId = parkrunnerId,
            ParkrunnerName = runnerName,
            LastFetched = DateTime.UtcNow
        };

        string url = $"https://www.parkrun.org.uk/parkrunner/{parkrunnerId}/";

        try
        {
            string html = await _httpClient.GetStringAsync(url);
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            // 1. Extract Milestones
            var msMatches = Regex.Matches(html, @"Vanity-page--milestones\s+Vanity-page--([a-z0-9]+)ms", RegexOptions.IgnoreCase);
            var vMilestones = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (Match m in msMatches)
            {
                string tag = m.Groups[1].Value.ToUpperInvariant();
                if (tag.StartsWith("V"))
                {
                    vMilestones.Add(tag);
                }
            }

            profile.Milestones = vMilestones.OrderByDescending(GetMilestoneWeight).ToList();
            profile.HighestMilestone = profile.Milestones.Count > 0 ? profile.Milestones[0] : "-";

            // 2. Extract Volunteer Summary Table
            var volMatch = Regex.Match(html, @"id=[\x27\x22]volunteer-summary[\x27\x22].*?</table>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (volMatch.Success)
            {
                string tableHtml = volMatch.Value;

                // Total Credits
                var credMatch = Regex.Match(tableHtml, @"Total Credits.*?(\d+)", RegexOptions.Singleline | RegexOptions.IgnoreCase);
                if (credMatch.Success && int.TryParse(credMatch.Groups[1].Value, out int credits))
                {
                    profile.TotalCredits = credits;
                }

                // Roles
                var rows = Regex.Matches(tableHtml, @"<tr[^>]*>\s*<td[^>]*>(.*?)</td>\s*<td[^>]*>(.*?)</td>\s*</tr>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
                foreach (Match r in rows)
                {
                    string roleRaw = Regex.Replace(r.Groups[1].Value, @"<[^>]+>", "").Trim();
                    string countRaw = Regex.Replace(r.Groups[2].Value, @"<[^>]+>", "").Trim();

                    if (!roleRaw.Equals("Role", StringComparison.OrdinalIgnoreCase) &&
                        !roleRaw.Contains("Total", StringComparison.OrdinalIgnoreCase) &&
                        int.TryParse(countRaw, out int occCount))
                    {
                        profile.Roles[roleRaw] = occCount;
                    }
                }

                // Summarise top 3 roles
                var topRoles = profile.Roles
                    .OrderByDescending(kv => kv.Value)
                    .Take(3)
                    .Select(kv => $"{kv.Key} ({kv.Value})");

                string topStr = string.Join(", ", topRoles);
                if (!string.IsNullOrEmpty(topStr))
                {
                    profile.TopRolesSummary = topStr;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Warning] Failed to fetch volunteer stats for {runnerName} (A{parkrunnerId}): {ex.Message}");
        }

        return profile;
    }

    public static string GetHighestMilestoneFromCredits(int credits)
    {
        return credits switch
        {
            >= 1000 => "V1000",
            >= 900 => "V900",
            >= 800 => "V800",
            >= 700 => "V700",
            >= 600 => "V600",
            >= 500 => "V500",
            >= 400 => "V400",
            >= 300 => "V300",
            >= 250 => "V250",
            >= 200 => "V200",
            >= 100 => "V100",
            >= 50 => "V50",
            >= 25 => "V25",
            _ => "-"
        };
    }

    private static int GetMilestoneWeight(string milestone)
    {
        return milestone.ToUpperInvariant() switch
        {
            "V1000" => 1000,
            "V900" => 900,
            "V800" => 800,
            "V700" => 700,
            "V600" => 600,
            "V500" => 500,
            "V400" => 400,
            "V300" => 300,
            "V250" => 250,
            "V200" => 200,
            "V100" => 100,
            "V50" => 50,
            "V25" => 25,
            "V10" => 10,
            _ => 0
        };
    }

    private VolunteerStore LoadStore()
    {
        if (File.Exists(_storeFilePath))
        {
            try
            {
                string json = File.ReadAllText(_storeFilePath);
                var store = JsonSerializer.Deserialize<VolunteerStore>(json);
                if (store != null) return store;
            }
            catch { }
        }

        return new VolunteerStore();
    }

    private void SaveStore(VolunteerStore store)
    {
        try
        {
            string? dir = Path.GetDirectoryName(_storeFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string json = JsonSerializer.Serialize(store, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_storeFilePath, json);

            // Also synchronize with project directory if running from bin
            string projectDataDir = Path.Combine(Directory.GetCurrentDirectory(), "data", "volunteers.json");
            if (File.Exists(projectDataDir) && !string.Equals(Path.GetFullPath(projectDataDir), Path.GetFullPath(_storeFilePath), StringComparison.OrdinalIgnoreCase))
            {
                File.WriteAllText(projectDataDir, json);
            }
        }
        catch { }
    }
}
