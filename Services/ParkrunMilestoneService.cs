using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using HtmlAgilityPack;
using ParkrunScraper.Models;

namespace ParkrunScraper.Services;

public class ParkrunMilestoneService
{
    private readonly HttpClient _httpClient;
    private readonly string _milestonesStorePath;

    public ParkrunMilestoneService(HttpClient? httpClient = null, string? milestonesStorePath = null)
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

        if (!string.IsNullOrEmpty(milestonesStorePath))
        {
            _milestonesStorePath = milestonesStorePath;
        }
        else
        {
            string projectDataDir = Path.Combine(Directory.GetCurrentDirectory(), "data");
            if (!Directory.Exists(projectDataDir))
            {
                Directory.CreateDirectory(projectDataDir);
            }
            _milestonesStorePath = Path.Combine(projectDataDir, "milestones.json");
        }
    }

    public async Task<List<ParkrunMilestone>> ProcessAndTrackMilestonesAsync(
        ConsolidatedReportMetadata meta,
        List<ParkrunRecord> records,
        string targetClubName,
        string eventDate,
        List<ParkrunVolunteerProfile>? volunteerProfiles = null)
    {
        string cacheBaseDir = Path.Combine(Directory.GetCurrentDirectory(), "data", $"events_{eventDate}");
        if (!Directory.Exists(cacheBaseDir))
        {
            Directory.CreateDirectory(cacheBaseDir);
        }

        // 1. Gather all event result HTMLs and extract runner row attributes
        var runnerEventMap = new Dictionary<string, (int Runs, int Vols, string AgeGroup, string Achievement, bool IsPb, bool IsFirstTimer)>();

        int total = meta.EventResultUrls.Count;
        int index = 0, fetchedCount = 0, cachedCount = 0, blockedCount = 0;
        ParkrunFetchHelper.LogPassStart("Milestone analysis", meta.EventResultUrls, cacheBaseDir);
        foreach (var kvp in meta.EventResultUrls)
        {
            index++;
            string eventName = kvp.Key;
            var (eventUrl, cachePath) = ParkrunFetchHelper.ResolveEvent(cacheBaseDir, eventName, kvp.Value);

            var (eventHtml, outcome) = await ParkrunFetchHelper.GetEventHtmlAsync(_httpClient, eventName, eventUrl, cachePath, index, total);
            if (outcome == EventFetchOutcome.FromCache) cachedCount++;
            else if (outcome == EventFetchOutcome.Downloaded) fetchedCount++;
            else blockedCount++;

            if (string.IsNullOrEmpty(eventHtml)) continue;

            // Parse Results-table-row
            var rowMatches = Regex.Matches(eventHtml, @"<tr class=[\x27\x22]Results-table-row[\x27\x22](.*?)</tr>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            foreach (Match row in rowMatches)
            {
                string rContent = row.Groups[1].Value;
                var idMatch = Regex.Match(rContent, @"parkrunner/(\d+)", RegexOptions.IgnoreCase);
                if (!idMatch.Success) continue;
                string pId = idMatch.Groups[1].Value.Trim();

                var runsMatch = Regex.Match(rContent, @"data-runs=[\x27\x22](\d+)[\x27\x22]", RegexOptions.IgnoreCase);
                var volsMatch = Regex.Match(rContent, @"data-vols=[\x27\x22](\d+)[\x27\x22]", RegexOptions.IgnoreCase);
                var ageMatch = Regex.Match(rContent, @"data-agegroup=[\x27\x22]([^\x27\x22]*)[\x27\x22]", RegexOptions.IgnoreCase);
                var achMatch = Regex.Match(rContent, @"data-achievement=[\x27\x22]([^\x27\x22]*)[\x27\x22]", RegexOptions.IgnoreCase);

                int runs = runsMatch.Success && int.TryParse(runsMatch.Groups[1].Value, out int rVal) ? rVal : 0;
                int vols = volsMatch.Success && int.TryParse(volsMatch.Groups[1].Value, out int vVal) ? vVal : 0;
                string ageGroup = ageMatch.Success ? ageMatch.Groups[1].Value.Trim() : "";
                string achievement = achMatch.Success ? achMatch.Groups[1].Value.Trim() : "";

                bool isPb = achievement.Contains("PB", StringComparison.OrdinalIgnoreCase) ||
                            rContent.Contains("Results-table-td--pb", StringComparison.OrdinalIgnoreCase) ||
                            runs == 1;

                bool isFirstTimer = achievement.Contains("First Timer", StringComparison.OrdinalIgnoreCase);

                runnerEventMap[pId] = (runs, vols, ageGroup, achievement, isPb, isFirstTimer);
            }
        }

        ParkrunFetchHelper.LogPassSummary("Milestone analysis", fetchedCount, cachedCount, blockedCount);

        // 2. Enrich records & identify running milestones
        var milestones = new List<ParkrunMilestone>();

        // Special Club Celebrations (e.g. C25K Graduation on 2026-11-07)
        var c25kGraduates = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "5982675",  // John VINCENT-BOND
            "12906742", // Neil SMITH
            "12853086", // Andrey FJELDSTAD
            "12850291"  // Andrea BOHN
        };

        foreach (var record in records)
        {
            if (string.IsNullOrEmpty(record.ParkrunnerId)) continue;

            if (runnerEventMap.TryGetValue(record.ParkrunnerId, out var stats))
            {
                record.TotalRuns = stats.Runs;
                record.TotalVols = stats.Vols;
                record.AgeGroup = stats.AgeGroup;
                record.Achievement = stats.Achievement;
                record.IsPb = stats.IsPb;
                record.IsFirstTimer = stats.IsFirstTimer;

                var mInfo = EvaluateRunMilestone(record.TotalRuns, record.AgeGroup);
                if (mInfo == null && eventDate == "2026-11-07" && c25kGraduates.Contains(record.ParkrunnerId))
                {
                    mInfo = ("C25K Graduation", "C25K Graduate", "#00796B", "#FFFFFF");
                }

                if (mInfo != null)
                {
                    record.Milestone = mInfo.Value.Title;
                    record.MilestoneBadgeBg = mInfo.Value.BgColor;
                    record.MilestoneBadgeText = mInfo.Value.TextColor;

                    milestones.Add(new ParkrunMilestone
                    {
                        ParkrunnerName = record.Parkrunner,
                        ParkrunnerId = record.ParkrunnerId,
                        EventName = record.EventName,
                        MilestoneType = "Run",
                        MilestoneCount = record.TotalRuns,
                        MilestoneTitle = mInfo.Value.Title,
                        MilestoneClub = mInfo.Value.Club,
                        BadgeBgColor = mInfo.Value.BgColor,
                        BadgeTextColor = mInfo.Value.TextColor,
                        FinishTime = record.Time,
                        Achievement = record.Achievement,
                        ProfileUrl = record.ProfileUrl,
                        EventDate = eventDate
                    });
                }
            }
        }

        // 3. Track volunteer milestones (if any volunteer reached 25, 50, 100, 200, 250, 500 this week)
        if (volunteerProfiles != null)
        {
            foreach (var vp in volunteerProfiles)
            {
                if (vp.DeltaCredits > 0 && IsVolunteerMilestone(vp.TotalCredits))
                {
                    var vInfo = EvaluateVolunteerMilestone(vp.TotalCredits);
                    if (vInfo != null)
                    {
                        // Check if not already added
                        if (!milestones.Any(m => m.ParkrunnerId == vp.ParkrunnerId && m.MilestoneType == "Volunteer"))
                        {
                            milestones.Add(new ParkrunMilestone
                            {
                                ParkrunnerName = vp.ParkrunnerName,
                                ParkrunnerId = vp.ParkrunnerId,
                                EventName = vp.EventName,
                                MilestoneType = "Volunteer",
                                MilestoneCount = vp.TotalCredits,
                                MilestoneTitle = vInfo.Value.Title,
                                MilestoneClub = vInfo.Value.Club,
                                BadgeBgColor = vInfo.Value.BgColor,
                                BadgeTextColor = vInfo.Value.TextColor,
                                FinishTime = "",
                                Achievement = vp.RoleThisWeek,
                                ProfileUrl = $"https://www.parkrun.org.uk/parkrunner/{vp.ParkrunnerId}/",
                                EventDate = eventDate
                            });
                        }
                    }
                }
            }
        }

        // Sort milestones: highest count descending, then by name
        milestones = milestones
            .OrderByDescending(m => m.MilestoneCount)
            .ThenBy(m => m.ParkrunnerName)
            .ToList();

        // 4. Save to persistent milestones store
        SaveToMilestonesStore(milestones);

        return milestones;
    }

    public static readonly int[] OfficialAdultMilestoneTiers = { 25, 50, 100, 200, 250, 300, 400, 500, 600, 700, 800, 900, 1000 };

    public static (string Title, string Club, string BgColor, string TextColor)? EvaluateRunMilestone(int runCount, string ageGroup)
    {
        return runCount switch
        {
            10 when ageGroup.StartsWith("J", StringComparison.OrdinalIgnoreCase) =>
                ("10th parkrun", "Junior 10 Club", "#1D1D1B", "#FFFFFF"),
            25 =>
                ("25th parkrun", "25 Club", "#6A5D9E", "#FFFFFF"),     // Official Purple
            50 =>
                ("50th parkrun", "50 Club", "#C81D31", "#FFFFFF"),     // Official Red
            100 =>
                ("100th parkrun", "100 Club", "#1D1D1B", "#FFFFFF"),   // Official Black
            200 =>
                ("200th parkrun", "200 Club", "#4FA4D9", "#2B233D"),   // Official Sky Blue
            250 =>
                ("250th parkrun", "250 Club", "#2C504A", "#FFFFFF"),   // Official Forest Green
            300 =>
                ("300th parkrun", "300 Club", "#E6612D", "#FFFFFF"),   // Official Burnt Orange
            400 =>
                ("400th parkrun", "400 Club", "#88002F", "#FFFFFF"),   // Official Burgundy
            500 =>
                ("500th parkrun", "500 Club", "#2E4DA7", "#FFFFFF"),   // Official Royal Blue
            600 =>
                ("600th parkrun", "600 Club", "#B1D600", "#2B233D"),   // Official Lime Green
            700 =>
                ("700th parkrun", "700 Club", "#252E5F", "#FFFFFF"),   // Official Dark Navy
            800 =>
                ("800th parkrun", "800 Club", "#7F217D", "#FFFFFF"),   // Official Plum/Magenta
            900 =>
                ("900th parkrun", "900 Club", "#5C8B30", "#FFFFFF"),   // Official Olive Green
            1000 =>
                ("1000th parkrun", "1000 Club", "#FFE049", "#1D1D1B"), // Official Gold
            _ => null
        };
    }

    public static bool IsVolunteerMilestone(int credits)
    {
        return credits is 25 or 50 or 100 or 200 or 250 or 300 or 400 or 500 or 600 or 700 or 800 or 900 or 1000;
    }

    public static (string Title, string Club, string BgColor, string TextColor)? EvaluateVolunteerMilestone(int credits)
    {
        return credits switch
        {
            10 => ("10th Volunteer Credit", "V10 Club", "#231F20", "#FFFFFF"),
            25 => ("25th Volunteer Credit", "V25 Club", "#6A5D9E", "#FFFFFF"),
            50 => ("50th Volunteer Credit", "V50 Club", "#C81D31", "#FFFFFF"),
            100 => ("100th Volunteer Credit", "V100 Club", "#1D1D1B", "#FFFFFF"),
            200 => ("200th Volunteer Credit", "V200 Club", "#4FA4D9", "#2B233D"),
            250 => ("250th Volunteer Credit", "V250 Club", "#2C504A", "#FFFFFF"),
            300 => ("300th Volunteer Credit", "V300 Club", "#E6612D", "#FFFFFF"),
            400 => ("400th Volunteer Credit", "V400 Club", "#88002F", "#FFFFFF"),
            500 => ("500th Volunteer Credit", "V500 Club", "#2E4DA7", "#FFFFFF"),
            600 => ("600th Volunteer Credit", "V600 Club", "#B1D600", "#2B233D"),
            700 => ("700th Volunteer Credit", "V700 Club", "#252E5F", "#FFFFFF"),
            800 => ("800th Volunteer Credit", "V800 Club", "#7F217D", "#FFFFFF"),
            900 => ("900th Volunteer Credit", "V900 Club", "#5C8B30", "#FFFFFF"),
            1000 => ("1000th Volunteer Credit", "V1000 Club", "#FFE049", "#1D1D1B"),
            _ => null
        };
    }

    public List<UpcomingMilestone> GetUpcomingMilestones(
        List<ParkrunRecord> currentRecords,
        string eventDate,
        int maxRemaining = 5)
    {
        string dataDir = Path.Combine(Directory.GetCurrentDirectory(), "data");
        var memberStats = new Dictionary<string, (string Name, int Runs, int Vols, string AgeGroup, string LastSeen)>();

        if (Directory.Exists(dataDir))
        {
            var clubCaches = Directory.GetFiles(dataDir, "cache_21925_*.html")
                .OrderBy(f => f)
                .ToList();

            foreach (var cpath in clubCaches)
            {
                var dateMatch = Regex.Match(Path.GetFileName(cpath), @"cache_21925_(\d{4}-\d{2}-\d{2})\.html");
                if (!dateMatch.Success) continue;
                string edate = dateMatch.Groups[1].Value;
                if (string.Compare(edate, eventDate, StringComparison.Ordinal) > 0) continue;

                string html = File.ReadAllText(cpath);
                var linkMatches = Regex.Matches(html, @"parkrunner/(\d+)[^>]*>([^<]+)</a>", RegexOptions.IgnoreCase);
                foreach (Match m in linkMatches)
                {
                    string pid = m.Groups[1].Value.Trim();
                    string name = HtmlEntity.DeEntitize(m.Groups[2].Value).Trim();
                    if (!memberStats.TryGetValue(pid, out var cur))
                    {
                        memberStats[pid] = (name, 0, 0, "", edate);
                    }
                    else
                    {
                        memberStats[pid] = (name, cur.Runs, cur.Vols, cur.AgeGroup, edate);
                    }
                }

                string evDir = Path.Combine(dataDir, $"events_{edate}");
                if (Directory.Exists(evDir))
                {
                    foreach (var efile in Directory.GetFiles(evDir, "*.html"))
                    {
                        string ehtml = File.ReadAllText(efile);
                        var rowMatches = Regex.Matches(ehtml, @"<tr class=[\x27\x22]Results-table-row[\x27\x22](.*?)</tr>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
                        foreach (Match row in rowMatches)
                        {
                            string rc = row.Groups[1].Value;
                            var idm = Regex.Match(rc, @"parkrunner/(\d+)", RegexOptions.IgnoreCase);
                            if (!idm.Success) continue;
                            string pid = idm.Groups[1].Value.Trim();
                            if (!memberStats.TryGetValue(pid, out var cur)) continue;

                            var rm = Regex.Match(rc, @"data-runs=[\x27\x22](\d+)[\x27\x22]", RegexOptions.IgnoreCase);
                            var vm = Regex.Match(rc, @"data-vols=[\x27\x22](\d+)[\x27\x22]", RegexOptions.IgnoreCase);
                            var am = Regex.Match(rc, @"data-agegroup=[\x27\x22]([^\x27\x22]*)[\x27\x22]", RegexOptions.IgnoreCase);

                            int runs = rm.Success && int.TryParse(rm.Groups[1].Value, out int rVal) ? rVal : 0;
                            int vols = vm.Success && int.TryParse(vm.Groups[1].Value, out int vVal) ? vVal : 0;
                            string age = am.Success ? am.Groups[1].Value.Trim() : cur.AgeGroup;

                            if (runs >= cur.Runs)
                            {
                                memberStats[pid] = (cur.Name, runs, Math.Max(cur.Vols, vols), age, edate);
                            }
                        }
                    }
                }
            }

            // Also incorporate volunteer store credits
            string volPath = Path.Combine(dataDir, "volunteers.json");
            if (File.Exists(volPath))
            {
                try
                {
                    var vStore = JsonSerializer.Deserialize<VolunteerStore>(File.ReadAllText(volPath));
                    if (vStore?.Profiles != null)
                    {
                        foreach (var kvp in vStore.Profiles)
                        {
                            string pid = kvp.Key;
                            var vp = kvp.Value;
                            if (memberStats.TryGetValue(pid, out var cur))
                            {
                                memberStats[pid] = (cur.Name, cur.Runs, Math.Max(cur.Vols, vp.TotalCredits), cur.AgeGroup, cur.LastSeen);
                            }
                            else if (!string.IsNullOrEmpty(vp.ParkrunnerName))
                            {
                                memberStats[pid] = (vp.ParkrunnerName, 0, vp.TotalCredits, "", eventDate);
                            }
                        }
                    }
                }
                catch { }
            }
        }

        // Overlay currentRecords
        foreach (var r in currentRecords)
        {
            if (string.IsNullOrEmpty(r.ParkrunnerId)) continue;
            if (memberStats.TryGetValue(r.ParkrunnerId, out var cur))
            {
                memberStats[r.ParkrunnerId] = (
                    r.Parkrunner,
                    Math.Max(cur.Runs, r.TotalRuns),
                    Math.Max(cur.Vols, r.TotalVols),
                    !string.IsNullOrEmpty(r.AgeGroup) ? r.AgeGroup : cur.AgeGroup,
                    eventDate
                );
            }
            else
            {
                memberStats[r.ParkrunnerId] = (r.Parkrunner, r.TotalRuns, r.TotalVols, r.AgeGroup, eventDate);
            }
        }

        var upcoming = new List<UpcomingMilestone>();

        foreach (var kvp in memberStats)
        {
            string pid = kvp.Key;
            var (name, runs, vols, ageGroup, lastSeen) = kvp.Value;
            string profileUrl = $"https://www.parkrun.org.uk/parkrunner/{pid}/";

            // Check upcoming Run milestones
            if (runs > 0)
            {
                var runTiers = ageGroup.StartsWith("J", StringComparison.OrdinalIgnoreCase)
                    ? new[] { 10 }.Concat(OfficialAdultMilestoneTiers)
                    : OfficialAdultMilestoneTiers;

                foreach (int target in runTiers)
                {
                    int diff = target - runs;
                    if (diff >= 1 && diff <= maxRemaining)
                    {
                        var mInfo = EvaluateRunMilestone(target, ageGroup);
                        if (mInfo != null)
                        {
                            upcoming.Add(new UpcomingMilestone
                            {
                                ParkrunnerName = name,
                                ParkrunnerId = pid,
                                MilestoneType = "Run",
                                CurrentCount = runs,
                                TargetMilestone = target,
                                MilestoneTitle = mInfo.Value.Title,
                                MilestoneClub = mInfo.Value.Club,
                                BadgeBgColor = mInfo.Value.BgColor,
                                BadgeTextColor = mInfo.Value.TextColor,
                                LastSeenDate = lastSeen,
                                ProfileUrl = profileUrl
                            });
                        }
                        break;
                    }
                }
            }

            // Check upcoming Volunteer milestones
            if (vols > 0)
            {
                var volTiers = ageGroup.StartsWith("J", StringComparison.OrdinalIgnoreCase)
                    ? new[] { 10 }.Concat(OfficialAdultMilestoneTiers)
                    : OfficialAdultMilestoneTiers;

                foreach (int target in volTiers)
                {
                    int diff = target - vols;
                    if (diff >= 1 && diff <= maxRemaining)
                    {
                        var vInfo = EvaluateVolunteerMilestone(target);
                        if (vInfo != null)
                        {
                            upcoming.Add(new UpcomingMilestone
                            {
                                ParkrunnerName = name,
                                ParkrunnerId = pid,
                                MilestoneType = "Volunteer",
                                CurrentCount = vols,
                                TargetMilestone = target,
                                MilestoneTitle = vInfo.Value.Club,
                                MilestoneClub = vInfo.Value.Club,
                                BadgeBgColor = vInfo.Value.BgColor,
                                BadgeTextColor = vInfo.Value.TextColor,
                                LastSeenDate = lastSeen,
                                ProfileUrl = profileUrl
                            });
                        }
                        break;
                    }
                }
            }
        }

        return upcoming
            .OrderBy(u => u.MilestoneType == "Run" ? 0 : 1)
            .ThenBy(u => u.Remaining)
            .ThenByDescending(u => u.TargetMilestone)
            .ThenBy(u => u.ParkrunnerName)
            .ToList();
    }

    private void SaveToMilestonesStore(List<ParkrunMilestone> newMilestones)
    {
        if (newMilestones == null || newMilestones.Count == 0) return;

        try
        {
            var existing = new List<ParkrunMilestone>();
            if (File.Exists(_milestonesStorePath))
            {
                string json = File.ReadAllText(_milestonesStorePath);
                existing = JsonSerializer.Deserialize<List<ParkrunMilestone>>(json) ?? new List<ParkrunMilestone>();
            }

            bool addedAny = false;
            foreach (var m in newMilestones)
            {
                // Check if already in store (same date, runner id, count, and type)
                if (!existing.Any(e => e.EventDate == m.EventDate && e.ParkrunnerId == m.ParkrunnerId && e.MilestoneCount == m.MilestoneCount && e.MilestoneType == m.MilestoneType))
                {
                    existing.Add(m);
                    addedAny = true;
                }
            }

            if (addedAny)
            {
                string? dir = Path.GetDirectoryName(_milestonesStorePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                string outJson = JsonSerializer.Serialize(existing, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_milestonesStorePath, outJson);

                // Synchronize with project directory if running from bin
                string projectDataDir = Path.Combine(Directory.GetCurrentDirectory(), "data", "milestones.json");
                if (File.Exists(projectDataDir) && !string.Equals(Path.GetFullPath(projectDataDir), Path.GetFullPath(_milestonesStorePath), StringComparison.OrdinalIgnoreCase))
                {
                    File.WriteAllText(projectDataDir, outJson);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Warning] Failed to save milestones to store: {ex.Message}");
        }
    }
}
