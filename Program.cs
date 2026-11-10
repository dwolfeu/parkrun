using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ParkrunScraper.Models;
using ParkrunScraper.Services;

namespace ParkrunScraper;

class Program
{
    static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("=========================================================================================");
        Console.WriteLine("                PARKRUN CONSOLIDATED CLUB RESULTS SCRAPER (C# .NET)                      ");
        Console.WriteLine("=========================================================================================");

        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string configPath = Path.Combine(baseDir, "appsettings.json");
        if (!File.Exists(configPath))
        {
            configPath = Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json");
        }

        string defaultClubNum = "21925";
        string defaultClubName = "Birmingham Swifts";
        string downloadFolder = "~/Downloads";
        string outputPattern = "parkrun_{0}_{1}.pdf";
        string singleOutputFilename = "parkrun_club_results.pdf";
        bool overwriteSingleFile = false;

        if (File.Exists(configPath))
        {
            try
            {
                string json = File.ReadAllText(configPath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("DefaultClubNum", out var cn)) defaultClubNum = cn.GetString() ?? defaultClubNum;
                if (root.TryGetProperty("DefaultClubName", out var cname)) defaultClubName = cname.GetString() ?? defaultClubName;
                if (root.TryGetProperty("DownloadFolder", out var df)) downloadFolder = df.GetString() ?? downloadFolder;
                if (root.TryGetProperty("OutputFilenamePattern", out var op)) outputPattern = op.GetString() ?? outputPattern;
                if (root.TryGetProperty("SingleOutputFilename", out var sof)) singleOutputFilename = sof.GetString() ?? singleOutputFilename;
                if (root.TryGetProperty("OverwriteSingleFile", out var osf)) overwriteSingleFile = osf.GetBoolean();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Warning] Failed to read appsettings.json: {ex.Message}");
            }
        }

        string clubInput = defaultClubNum;
        string? eventDate = null;
        string? customOutput = null;
        bool singleFileFlag = overwriteSingleFile;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if ((arg.Equals("--club", StringComparison.OrdinalIgnoreCase) || arg.Equals("-c", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
            {
                clubInput = args[++i];
            }
            else if ((arg.Equals("--date", StringComparison.OrdinalIgnoreCase) || arg.Equals("-d", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
            {
                eventDate = args[++i];
            }
            else if ((arg.Equals("--output", StringComparison.OrdinalIgnoreCase) || arg.Equals("-o", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
            {
                customOutput = args[++i];
            }
            else if ((arg.Equals("--delay", StringComparison.OrdinalIgnoreCase) || arg.Equals("-w", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
            {
                string delayArg = args[++i];
                if (double.TryParse(delayArg, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double delaySeconds) && delaySeconds >= 0)
                {
                    ParkrunFetchHelper.RequestDelayMs = (int)Math.Round(delaySeconds * 1000);
                }
                else
                {
                    Console.WriteLine($"[Warning] Invalid --delay value '{delayArg}'; using {ParkrunFetchHelper.RequestDelayMs / 1000.0:0.##}s.");
                }
            }
            else if (!arg.StartsWith("-"))
            {
                clubInput = arg;
            }
        }

        if (string.IsNullOrEmpty(eventDate))
        {
            DateTime now = DateTime.UtcNow.Date;
            int diff = (7 + ((int)now.DayOfWeek - (int)DayOfWeek.Saturday)) % 7;
            DateTime mostRecentSaturday = now.AddDays(-diff);
            eventDate = mostRecentSaturday.ToString("yyyy-MM-dd");
        }

        Console.WriteLine($"[Info] Per-event request spacing: {ParkrunFetchHelper.RequestDelayMs / 1000.0:0.##}s (override with --delay <seconds>)");

        var scraperService = new ParkrunScraperService();
        string requestUrl = scraperService.BuildUrl(clubInput, eventDate);

        Console.WriteLine($"Fetching consolidated club report from:\n  {requestUrl}\n");

        try
        {
            var (meta, records) = await scraperService.ScrapeConsolidatedClubAsync(clubInput, eventDate);

            string effectiveClubName = !string.IsNullOrEmpty(meta.ClubName) ? meta.ClubName : defaultClubName;
            string effectiveDate = !string.IsNullOrEmpty(meta.EventDate) ? meta.EventDate : (eventDate ?? DateTime.UtcNow.ToString("yyyy-MM-dd"));
            // Pin the report date to the requested date so an empty/unparsed report never
            // silently falls back to today's date in the PDF.
            meta.EventDate = effectiveDate;
            int totalRunners = records.Count;
            int totalEvents = records.Select(r => r.EventName).Distinct().Count();
            int totalMembers = int.TryParse(meta.TotalMembers, out int tm) ? tm : 0;

            Console.WriteLine($"Club Name:                       {effectiveClubName}");
            Console.WriteLine($"Event Date:                      {effectiveDate}");
            Console.WriteLine($"Total Club Members Registered:   {(string.IsNullOrEmpty(meta.TotalMembers) ? "N/A" : meta.TotalMembers)}");
            Console.WriteLine($"Total Club Runners on Date:      {totalRunners}");
            Console.WriteLine($"Distinct Events Attended:        {totalEvents:N0}");
            Console.WriteLine($"Total Runner Records Parsed:     {records.Count:N0}\n");

            // No participants => parkrun has no results for this club/date. Don't emit a
            // misleading empty PDF; tell the user clearly and stop here.
            if (records.Count == 0)
            {
                Console.WriteLine($"[Warning] parkrun has no results for this club/date combination ({effectiveClubName}, {effectiveDate}).");
                Console.WriteLine("          Nothing to report — skipping PDF generation. Try a date when the club had participants (parkruns are on Saturdays).");
                return 0;
            }

            // Save Snapshot into History
            var historyService = new ParkrunHistoryService();
            historyService.SaveSnapshot(new WeeklyClubSnapshot
            {
                EventDate = effectiveDate,
                ClubName = effectiveClubName,
                TotalRunners = totalRunners,
                DistinctEvents = totalEvents,
                TotalMembersRegistered = totalMembers
            });

            // Get historical trends and generate chart
            var (trends, recentHistory) = historyService.GetTrends(effectiveClubName, effectiveDate);
            byte[]? trendChartBytes = null;
            if (recentHistory.Count >= 2)
            {
                trendChartBytes = ParkrunChartGenerator.GenerateWeeklyTrendChart(recentHistory);
            }

            // Track Member Volunteering for this specific week across attended events
            Console.WriteLine("Fetching member volunteering for this specific week across attended events...");
            var volunteerService = new ParkrunVolunteerService();
            var volunteerProfiles = await volunteerService.GetWeeklyEventVolunteersAsync(
                meta.EventResultUrls,
                effectiveClubName,
                effectiveDate);

            int totalVolCredits = volunteerProfiles.Sum(v => v.TotalCredits);
            int activeVolunteers = volunteerProfiles.Count;
            int milestoneHolders = volunteerProfiles.Count(v => !string.IsNullOrEmpty(v.HighestMilestone) && v.HighestMilestone != "-");
            Console.WriteLine($"Volunteering (This Week):        {activeVolunteers} members volunteered ({totalVolCredits:N0} combined lifetime credits, {milestoneHolders} milestone achievers)\n");

            // Track and detect weekly club milestones
            Console.WriteLine("Analyzing runner and volunteer milestones for this weekend...");
            var milestoneService = new ParkrunMilestoneService();
            var milestones = await milestoneService.ProcessAndTrackMilestonesAsync(
                meta,
                records,
                effectiveClubName,
                effectiveDate,
                volunteerProfiles);

            Console.WriteLine($"Milestones Achieved This Week:   {milestones.Count} members reached a milestone\n");

            var upcomingMilestones = milestoneService.GetUpcomingMilestones(records, effectiveDate);
            Console.WriteLine($"Upcoming Milestones Watch:       {upcomingMilestones.Count} members within 5 runs/credits of a milestone\n");

            string destinationPdf;
            if (!string.IsNullOrEmpty(customOutput))
            {
                destinationPdf = customOutput;
            }
            else
            {
                string resolvedDownloadDir = ParkrunScraperService.ResolvePath(downloadFolder);
                if (singleFileFlag)
                {
                    destinationPdf = Path.Combine(resolvedDownloadDir, singleOutputFilename);
                }
                else
                {
                    string slug = Regex.Replace(effectiveClubName, @"[^a-zA-Z0-9_-]", "_").Trim('_');
                    if (string.IsNullOrEmpty(slug)) slug = "Club";
                    string fileName = string.Format(outputPattern, slug, effectiveDate);
                    destinationPdf = Path.Combine(resolvedDownloadDir, fileName);
                }
            }

            // Generate PDF Report with weekly milestones section, volunteers section, trend chart, and upcoming milestones
            ParkrunPdfGenerator.GeneratePdf(meta, records, destinationPdf, trendChartBytes, volunteerProfiles, milestones, upcomingMilestones);

            if (milestones.Count > 0)
            {
                Console.WriteLine("\nClub Milestones Achieved This Week:");
                Console.WriteLine(new string('-', 95));
                Console.WriteLine($"{"Parkrunner",-26} | {"Milestone",-18} | {"Event",-32} | {"Details",-14}");
                Console.WriteLine(new string('-', 95));
                foreach (var m in milestones)
                {
                    string cleanEv = m.EventName.Replace(" parkrun", "", StringComparison.OrdinalIgnoreCase).Trim();
                    string details = !string.IsNullOrEmpty(m.Achievement) ? $"{m.FinishTime} ({m.Achievement})" : m.FinishTime;
                    Console.WriteLine($"{m.ParkrunnerName,-26} | {m.MilestoneTitle,-18} | {cleanEv,-32} | {details,-14}");
                }
                Console.WriteLine(new string('-', 95));
            }

            var pbs = records.Where(r => r.IsPb).ToList();
            if (pbs.Count > 0)
            {
                Console.WriteLine($"\nPersonal Bests (PBs) This Week ({pbs.Count}):");
                Console.WriteLine(new string('-', 95));
                Console.WriteLine($"{"Parkrunner",-26} | {"Time",-12} | {"Event",-35}");
                Console.WriteLine(new string('-', 95));
                foreach (var p in pbs)
                {
                    string cleanEv = p.EventName.Replace(" parkrun", "", StringComparison.OrdinalIgnoreCase).Trim();
                    Console.WriteLine($"{p.Parkrunner,-26} | {p.Time,-12} | {cleanEv,-35}");
                }
                Console.WriteLine(new string('-', 95));
            }

            var firstTimers = records.Where(r => r.IsFirstTimer).ToList();
            if (firstTimers.Count > 0)
            {
                Console.WriteLine($"\nFirst-Time Event Visits This Week ({firstTimers.Count}):");
                Console.WriteLine(new string('-', 95));
                Console.WriteLine($"{"Parkrunner",-26} | {"Total Runs",-12} | {"Event Visited",-35}");
                Console.WriteLine(new string('-', 95));
                foreach (var ft in firstTimers)
                {
                    string cleanEv = ft.EventName.Replace(" parkrun", "", StringComparison.OrdinalIgnoreCase).Trim();
                    Console.WriteLine($"{ft.Parkrunner,-26} | {ft.TotalRuns,-12} | {cleanEv,-35}");
                }
                Console.WriteLine(new string('-', 95));
            }

            if (records.Count > 0)
            {
                Console.WriteLine("\nPreview of extracted records (first 5):");
                Console.WriteLine(new string('-', 95));
                Console.WriteLine($"{"Event Name",-30} | {"Pos",-5} | {"Parkrunner",-26} | {"Time",-8}");
                Console.WriteLine(new string('-', 95));
                foreach (var r in records.Take(5))
                {
                    Console.WriteLine($"{r.EventName,-30} | {r.OverallPosition,-5} | {r.Parkrunner,-26} | {r.Time,-8}");
                }
                Console.WriteLine(new string('-', 95));
            }

            if (volunteerProfiles.Count > 0)
            {
                Console.WriteLine("\nVolunteers of the Week:");
                Console.WriteLine(new string('-', 110));
                Console.WriteLine($"{"Parkrunner",-24} | {"Event",-24} | {"Role This Week",-34} | {"Credits",-7} | {"Milestone",-9}");
                Console.WriteLine(new string('-', 110));
                foreach (var v in volunteerProfiles)
                {
                    string cleanEvent = v.EventName.Replace(" parkrun", "", StringComparison.OrdinalIgnoreCase).Trim();
                    Console.WriteLine($"{v.ParkrunnerName,-24} | {cleanEvent,-24} | {v.RoleThisWeek,-34} | {v.TotalCredits,-7} | {v.HighestMilestone,-9}");
                }
                Console.WriteLine(new string('-', 110));
            }

            if (upcomingMilestones.Count > 0)
            {
                Console.WriteLine($"\nUpcoming Club Milestones Watch (Within 5 Runs / Credits):");
                Console.WriteLine(new string('-', 95));
                Console.WriteLine($"{"Parkrunner",-26} | {"Type",-10} | {"Current",-8} | {"Target Club",-14} | {"To Go",-10} | {"Last Seen",-10}");
                Console.WriteLine(new string('-', 95));
                foreach (var u in upcomingMilestones)
                {
                    Console.WriteLine($"{u.ParkrunnerName,-26} | {u.MilestoneType,-10} | {u.CurrentCount,-8} | {u.MilestoneClub,-14} | {$"{u.Remaining} away",-10} | {u.LastSeenDate,-10}");
                }
                Console.WriteLine(new string('-', 95));
            }

            Console.WriteLine("\nExtraction completed successfully!");
            Console.WriteLine("=========================================================================================");
            return 0;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"\n[Error] Scraper failed: {ex.Message}");
            Console.ResetColor();
            return 1;
        }
    }
}
