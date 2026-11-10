using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using ParkrunScraper.Models;

namespace ParkrunScraper.Services;

public class ParkrunPdfGenerator
{
    static ParkrunPdfGenerator()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public static void GeneratePdf(
        ConsolidatedReportMetadata meta,
        List<ParkrunRecord> records,
        string outputPath,
        byte[]? trendChartBytes = null,
        List<ParkrunVolunteerProfile>? volunteerProfiles = null,
        List<ParkrunMilestone>? milestones = null,
        List<UpcomingMilestone>? upcomingMilestones = null)
    {
        string fullPath = ParkrunScraperService.ResolvePath(outputPath);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string clubName = !string.IsNullOrEmpty(meta.ClubName) ? meta.ClubName : "Parkrun Club";
        string eventDate = !string.IsNullOrEmpty(meta.EventDate) ? meta.EventDate : DateTime.UtcNow.ToString("yyyy-MM-dd");
        int totalRunners = records.Count;
        int totalEvents = records.Select(r => r.EventName).Distinct().Count();
        int totalVolunteers = volunteerProfiles?.Count ?? 0;

        // Check for club logo image
        string logoPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "birmingham_swifts_logo.jpg");
        if (!File.Exists(logoPath))
        {
            logoPath = Path.Combine(Directory.GetCurrentDirectory(), "assets", "birmingham_swifts_logo.jpg");
        }

        var doc = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.MarginHorizontal(18);
                page.MarginVertical(15);
                page.PageColor(Colors.White);
                page.DefaultTextStyle(x => x.FontSize(9).FontColor(Colors.Grey.Darken3));

                // Header (Repeats on each page cleanly)
                page.Header().Column(column =>
                {
                    column.Item().Row(row =>
                    {
                        row.RelativeItem().Column(titleCol =>
                        {
                            if (File.Exists(logoPath) && (clubName.Contains("Birmingham Swifts", StringComparison.OrdinalIgnoreCase) || clubName.Contains("Swifts", StringComparison.OrdinalIgnoreCase)))
                            {
                                titleCol.Item().MaxHeight(32).MaxWidth(140).Image(logoPath).FitArea();
                            }
                            else
                            {
                                titleCol.Item().Text(clubName)
                                    .FontSize(17)
                                    .Bold()
                                    .FontColor(Colors.Indigo.Darken3);
                            }

                            titleCol.Item().PaddingTop(1).Text("Parkrun Club Results")
                                .FontSize(10.5f)
                                .FontColor(Colors.Grey.Darken1);
                        });

                        row.AutoItem().Column(dateCol =>
                        {
                            dateCol.Item().AlignRight().Text($"Event Date: {eventDate}")
                                .FontSize(9.5f)
                                .Bold()
                                .FontColor(Colors.Grey.Darken3);

                            dateCol.Item().AlignRight().Text($"Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC")
                                .FontSize(8)
                                .FontColor(Colors.Grey.Medium);
                        });
                    });

                    // Summary Stats Badges
                    column.Item().PaddingTop(4).PaddingBottom(4).Row(statRow =>
                    {
                        statRow.Spacing(8);

                        // Card 1: Runners
                        statRow.RelativeItem().Background(Colors.Indigo.Lighten5).Border(1).BorderColor(Colors.Indigo.Lighten3).Padding(5).Column(c =>
                        {
                            c.Item().Text("Total Club Runners").FontSize(7.5f).FontColor(Colors.Indigo.Darken2).SemiBold();
                            c.Item().Text($"{totalRunners}").FontSize(13).Bold().FontColor(Colors.Indigo.Darken4);
                        });

                        // Card 2: Events
                        statRow.RelativeItem().Background(Colors.Teal.Lighten5).Border(1).BorderColor(Colors.Teal.Lighten3).Padding(5).Column(c =>
                        {
                            c.Item().Text("Events Attended").FontSize(7.5f).FontColor(Colors.Teal.Darken2).SemiBold();
                            c.Item().Text($"{totalEvents}").FontSize(13).Bold().FontColor(Colors.Teal.Darken4);
                        });

                        // Card 3: Registered Members
                        statRow.RelativeItem().Background(Colors.Orange.Lighten5).Border(1).BorderColor(Colors.Orange.Lighten3).Padding(5).Column(c =>
                        {
                            c.Item().Text("Club Members Reg.").FontSize(7.5f).FontColor(Colors.Orange.Darken2).SemiBold();
                            string memberStr = string.IsNullOrEmpty(meta.TotalMembers) ? "N/A" : meta.TotalMembers;
                            c.Item().Text(memberStr).FontSize(13).Bold().FontColor(Colors.Orange.Darken4);
                        });

                        // Card 4: Volunteers This Week
                        statRow.RelativeItem().Background(Colors.Purple.Lighten5).Border(1).BorderColor(Colors.Purple.Lighten3).Padding(5).Column(c =>
                        {
                            c.Item().Text("Club Volunteers").FontSize(7.5f).FontColor(Colors.Purple.Darken2).SemiBold();
                            c.Item().Text($"{totalVolunteers}").FontSize(13).Bold().FontColor(Colors.Purple.Darken4);
                        });
                    });

                    column.Item().PaddingTop(1).LineHorizontal(1).LineColor(Colors.Grey.Lighten2);
                });

                // Content (Milestones Section at the top, then Members Table, followed by Trends Graph & Volunteers)
                page.Content().PaddingTop(5).Column(col =>
                {
                    // =========================================================================
                    // Weekly Milestones Section (Top of Report)
                    // =========================================================================
                    if (milestones != null && milestones.Count > 0)
                    {
                        col.Item().PaddingBottom(7).Column(mCol =>
                        {
                            // Section Header
                            mCol.Item().Row(mr =>
                            {
                                mr.RelativeItem().Text(t =>
                                {
                                    t.Span("🎉 ").FontSize(12f);
                                    t.Span("Club Milestones This Week: ").FontSize(11.5f).Bold().FontColor(Colors.Pink.Darken3);
                                    t.Span($"Celebrating {milestones.Count} {clubName} milestone achievement{(milestones.Count > 1 ? "s" : "")} this weekend!").FontSize(9f).FontColor(Colors.Grey.Darken2);
                                });
                            });

                            // Milestone Cards Grid (up to 3 per row for clean multi-card layout)
                            int cardsPerRow = milestones.Count <= 2 ? milestones.Count : 3;
                            float nameFontSize = milestones.Count > 2 ? 10f : 12f;
                            float badgeFontSize = milestones.Count > 2 ? 8f : 9.5f;

                            foreach (var rowChunk in milestones.Chunk(cardsPerRow))
                            {
                                mCol.Item().PaddingTop(3).Row(cardsRow =>
                                {
                                    cardsRow.Spacing(6);

                                    foreach (var m in rowChunk)
                                    {
                                        cardsRow.RelativeItem().Background(Colors.Pink.Lighten5)
                                            .Border(1f).BorderColor(Colors.Pink.Lighten2)
                                            .PaddingVertical(4).PaddingHorizontal(6)
                                            .Column(card =>
                                            {
                                                // Top row in card: Runner Name + Milestone Badge
                                                card.Item().Row(topRow =>
                                                {
                                                    topRow.Spacing(4);
                                                    if (!string.IsNullOrEmpty(m.ProfileUrl))
                                                    {
                                                        topRow.RelativeItem().AlignMiddle().Hyperlink(m.ProfileUrl)
                                                            .Text(m.ParkrunnerName).FontSize(nameFontSize).Bold().FontColor(Colors.Indigo.Darken4);
                                                    }
                                                    else
                                                    {
                                                        topRow.RelativeItem().AlignMiddle().Text(m.ParkrunnerName).FontSize(nameFontSize).Bold().FontColor(Colors.Indigo.Darken4);
                                                    }

                                                    topRow.AutoItem().AlignMiddle().Background(m.BadgeBgColor).PaddingHorizontal(5).PaddingVertical(1.5f)
                                                        .Text(m.MilestoneTitle).FontSize(badgeFontSize).Bold().FontColor(m.BadgeTextColor);
                                                });

                                                // Sub-row: Event, Time, Achievement (uses Text for smooth wrapping)
                                                card.Item().PaddingTop(2).Text(detailText =>
                                                {
                                                    string cleanEvent = m.EventName.Replace(" parkrun", "", StringComparison.OrdinalIgnoreCase).Trim();
                                                    detailText.Span(cleanEvent).FontSize(8.5f).FontColor(Colors.Grey.Darken3);

                                                    if (!string.IsNullOrEmpty(m.FinishTime))
                                                    {
                                                        detailText.Span(" • ").FontSize(8f).FontColor(Colors.Grey.Medium);
                                                        detailText.Span(m.FinishTime).FontSize(8.5f).SemiBold().FontColor(Colors.Indigo.Darken3);
                                                    }

                                                    if (!string.IsNullOrEmpty(m.Achievement))
                                                    {
                                                        detailText.Span(" • ").FontSize(8f).FontColor(Colors.Grey.Medium);
                                                        detailText.Span($" {m.Achievement} ").FontSize(7.5f).Bold().FontColor(Colors.Green.Darken4).BackgroundColor(Colors.Green.Lighten4);
                                                    }
                                                });
                                            });
                                    }

                                    // Fill empty slots if last row has fewer than cardsPerRow items
                                    for (int pad = rowChunk.Length; pad < cardsPerRow; pad++)
                                    {
                                        cardsRow.RelativeItem();
                                    }
                                });
                            }
                        });
                    }

                    // Table
                    col.Item().Table(table =>
                    {
                        table.ColumnsDefinition(columns =>
                        {
                            columns.RelativeColumn(5.4f); // Event Name (with First Timer badge)
                            columns.ConstantColumn(30);   // Pos
                            columns.RelativeColumn(4.6f); // Runner Name (with Milestone & PB badges)
                            columns.ConstantColumn(58);   // Time
                            columns.ConstantColumn(56);   // Finishers
                        });

                        // Table Header
                        table.Header(header =>
                        {
                            header.Cell().Background(Colors.Indigo.Darken3).PaddingVertical(3).PaddingHorizontal(3).Text("Event Name").Bold().FontColor(Colors.White).FontSize(12.5f);
                            header.Cell().Background(Colors.Indigo.Darken3).PaddingVertical(3).PaddingHorizontal(2).AlignCenter().Text("Pos").Bold().FontColor(Colors.White).FontSize(12.5f);
                            header.Cell().Background(Colors.Indigo.Darken3).PaddingVertical(3).PaddingHorizontal(3).Text("Parkrunner").Bold().FontColor(Colors.White).FontSize(12.5f);
                            header.Cell().Background(Colors.Indigo.Darken3).PaddingVertical(3).PaddingHorizontal(2).AlignCenter().Text("Time").Bold().FontColor(Colors.White).FontSize(12.5f);
                            header.Cell().Background(Colors.Indigo.Darken3).PaddingVertical(3).PaddingHorizontal(2).AlignCenter().Text("Finishers").Bold().FontColor(Colors.White).FontSize(12.5f);
                        });

                        // Table Rows
                        for (int i = 0; i < records.Count; i++)
                        {
                            var r = records[i];
                            bool hasMilestone = !string.IsNullOrEmpty(r.Milestone);
                            bool hasPb = r.IsPb;
                            string bgColor = hasMilestone ? Colors.Pink.Lighten5 : ((i % 2 == 0) ? Colors.White : Colors.Grey.Lighten4);

                            // Event Name Cell (with First Timer badge if attending for the first time)
                            var eventCell = table.Cell().Background(bgColor).PaddingVertical(1.2f).PaddingHorizontal(3);
                            if (r.IsFirstTimer)
                            {
                                eventCell.Row(eRow =>
                                {
                                    eRow.Spacing(3);
                                    eRow.RelativeItem().AlignMiddle().Text(r.EventName).FontSize(11f);
                                    eRow.AutoItem().AlignMiddle().Background(Colors.Teal.Lighten5).Border(0.8f).BorderColor(Colors.Teal.Darken1).PaddingHorizontal(3f).PaddingVertical(1).Text("First Timer").FontSize(7f).Bold().FontColor(Colors.Teal.Darken3);
                                });
                            }
                            else
                            {
                                eventCell.Text(r.EventName).FontSize(11f);
                            }

                            table.Cell().Background(bgColor).PaddingVertical(1.2f).PaddingHorizontal(2).AlignCenter().Text(r.OverallPosition).FontSize(13f).Bold();

                            // Runner Name Cell (with Milestone and PB tags)
                            var runnerCell = table.Cell().Background(bgColor).PaddingVertical(1.2f).PaddingHorizontal(3);

                            if (!hasMilestone && !hasPb)
                            {
                                if (!string.IsNullOrEmpty(r.ProfileUrl))
                                {
                                    runnerCell.Hyperlink(r.ProfileUrl).Text(r.Parkrunner).FontSize(12.5f).SemiBold().FontColor(Colors.Indigo.Darken4);
                                }
                                else
                                {
                                    runnerCell.Text(r.Parkrunner).FontSize(12.5f).SemiBold();
                                }
                            }
                            else
                            {
                                runnerCell.Row(rRow =>
                                {
                                    rRow.Spacing(2.5f);
                                    if (!string.IsNullOrEmpty(r.ProfileUrl))
                                    {
                                        rRow.RelativeItem().AlignMiddle().Hyperlink(r.ProfileUrl).Text(r.Parkrunner).FontSize(12.5f).SemiBold().FontColor(Colors.Indigo.Darken4);
                                    }
                                    else
                                    {
                                        rRow.RelativeItem().AlignMiddle().Text(r.Parkrunner).FontSize(12.5f).SemiBold();
                                    }

                                    if (hasMilestone)
                                    {
                                        string badgeBg = !string.IsNullOrEmpty(r.MilestoneBadgeBg) ? r.MilestoneBadgeBg : Colors.Purple.Darken2;
                                        string badgeText = !string.IsNullOrEmpty(r.MilestoneBadgeText) ? r.MilestoneBadgeText : Colors.White;
                                        rRow.AutoItem().AlignMiddle().Background(badgeBg).PaddingHorizontal(3f).PaddingVertical(1).Text(r.Milestone).FontSize(8f).Bold().FontColor(badgeText);
                                    }

                                    if (hasPb)
                                    {
                                        rRow.AutoItem().AlignMiddle().Background(Colors.Green.Darken2).PaddingHorizontal(3f).PaddingVertical(1).Text("PB").FontSize(8f).Bold().FontColor(Colors.White);
                                    }
                                });
                            }

                            table.Cell().Background(bgColor).PaddingVertical(1.2f).PaddingHorizontal(2).AlignCenter().Text(r.Time).FontSize(13f).Bold().FontColor(Colors.Indigo.Darken2);
                            table.Cell().Background(bgColor).PaddingVertical(1.2f).PaddingHorizontal(2).AlignCenter().Text(r.EventTotalParticipants).FontSize(15f).FontColor(Colors.Grey.Darken2);
                        }
                    });

                    // =========================================================================
                    // Trend Graph Section (Last 15 Weeks) - right after weekly runner section
                    // =========================================================================
                    if (trendChartBytes != null)
                    {
                        col.Item().ShowEntire().PaddingTop(10).Column(chartCol =>
                        {
                            chartCol.Item().Row(cr =>
                            {
                                cr.RelativeItem().Text("Weekly Trends & Participation History (Last 15 Weeks)").FontSize(10f).Bold().FontColor(Colors.Indigo.Darken3);
                            });
                            chartCol.Item().PaddingTop(3).Border(0.5f).BorderColor(Colors.Grey.Lighten2).Image(trendChartBytes).FitWidth();

                            // Legend Panel positioned below the graph
                            chartCol.Item().PaddingTop(3).AlignCenter().Row(lr =>
                            {
                                lr.Spacing(24);

                                lr.AutoItem().Row(r1 =>
                                {
                                    r1.Spacing(4);
                                    r1.AutoItem().Text("●").FontSize(9).FontColor(Colors.Indigo.Darken3);
                                    r1.AutoItem().Text("Club Runners").FontSize(7.5f).Bold().FontColor(Colors.Grey.Darken3);
                                });

                                lr.AutoItem().Row(r2 =>
                                {
                                    r2.Spacing(4);
                                    r2.AutoItem().Text("●").FontSize(9).FontColor(Colors.Teal.Darken2);
                                    r2.AutoItem().Text("Events Attended").FontSize(7.5f).Bold().FontColor(Colors.Grey.Darken3);
                                });
                            });
                        });
                    }

                    // =========================================================================
                    // Volunteering Section (always shown at the end of the report)
                    // =========================================================================
                    col.Item().PaddingTop(8).Column(volCol =>
                    {
                        if (volunteerProfiles != null && volunteerProfiles.Count > 0)
                        {
                            int totalWeeklyVolunteers = volunteerProfiles.Count;
                            int distinctEventsSupported = volunteerProfiles.Select(v => v.EventName).Where(e => !string.IsNullOrEmpty(e)).Distinct().Count();
                            int totalVolCredits = volunteerProfiles.Sum(v => v.TotalCredits);
                            int milestoneHolders = volunteerProfiles.Count(v => !string.IsNullOrEmpty(v.HighestMilestone) && v.HighestMilestone != "-");

                            // Section Header
                            volCol.Item().Row(vr =>
                            {
                                vr.RelativeItem().Column(vc =>
                                {
                                    vc.Item().Row(r =>
                                    {
                                        r.Spacing(6);
                                        r.AutoItem().Text("💜").FontSize(10.5f);
                                        r.AutoItem().Text("Club Volunteers This Week & Community Champions")
                                            .FontSize(11f).Bold().FontColor(Colors.Purple.Darken3);
                                    });
                                    vc.Item().PaddingTop(1).Text($"Celebrating {totalWeeklyVolunteers} {clubName} members who volunteered at parkrun events this weekend ({eventDate}).")
                                        .FontSize(8f).FontColor(Colors.Grey.Darken1);
                                });
                            });

                            // Summary Tiles
                            volCol.Item().PaddingTop(3).PaddingBottom(3).Row(statRow =>
                            {
                                statRow.Spacing(8);

                                // Card 1: Volunteers This Week
                                statRow.RelativeItem().Background(Colors.Purple.Lighten5).Border(1).BorderColor(Colors.Purple.Lighten3).Padding(4).Column(c =>
                                {
                                    c.Item().Text("Volunteers This Week").FontSize(7.5f).FontColor(Colors.Purple.Darken2).SemiBold();
                                    c.Item().Text($"{totalWeeklyVolunteers} Members").FontSize(11.5f).Bold().FontColor(Colors.Purple.Darken4);
                                });

                                // Card 2: Events Supported
                                statRow.RelativeItem().Background(Colors.Teal.Lighten5).Border(1).BorderColor(Colors.Teal.Lighten3).Padding(4).Column(c =>
                                {
                                    c.Item().Text("Events Supported").FontSize(7.5f).FontColor(Colors.Teal.Darken2).SemiBold();
                                    c.Item().Text($"{distinctEventsSupported} Events").FontSize(11.5f).Bold().FontColor(Colors.Teal.Darken4);
                                });

                                // Card 3: Combined Lifetime Credits
                                statRow.RelativeItem().Background(Colors.Indigo.Lighten5).Border(1).BorderColor(Colors.Indigo.Lighten3).Padding(4).Column(c =>
                                {
                                    c.Item().Text("Combined Volunteer Credits").FontSize(7.5f).FontColor(Colors.Indigo.Darken2).SemiBold();
                                    c.Item().Text($"{totalVolCredits:N0} Credits").FontSize(11.5f).Bold().FontColor(Colors.Indigo.Darken4);
                                });

                                // Card 4: Milestone Club Achievers
                                statRow.RelativeItem().Background(Colors.Amber.Lighten5).Border(1).BorderColor(Colors.Amber.Lighten3).Padding(4).Column(c =>
                                {
                                    c.Item().Text("Milestone Club Achievers").FontSize(7.5f).FontColor(Colors.Amber.Darken3).SemiBold();
                                    c.Item().Text($"{milestoneHolders} Members").FontSize(11.5f).Bold().FontColor(Colors.Amber.Darken4);
                                });
                            });

                            // Volunteer Table
                            volCol.Item().PaddingTop(2).Table(vTable =>
                            {
                                vTable.ColumnsDefinition(columns =>
                                {
                                    columns.RelativeColumn(4.2f); // Volunteer Name & ID
                                    columns.RelativeColumn(3.2f); // Event
                                    columns.RelativeColumn(4.6f); // Role(s) This Week
                                    columns.ConstantColumn(62);   // Milestone
                                    columns.ConstantColumn(62);   // Credits
                                });

                                // Table Header
                                vTable.Header(header =>
                                {
                                    header.Cell().Background(Colors.Purple.Darken3).PaddingVertical(3).PaddingHorizontal(3).Text("Volunteer").Bold().FontColor(Colors.White).FontSize(13f);
                                    header.Cell().Background(Colors.Purple.Darken3).PaddingVertical(3).PaddingHorizontal(3).Text("Event").Bold().FontColor(Colors.White).FontSize(13f);
                                    header.Cell().Background(Colors.Purple.Darken3).PaddingVertical(3).PaddingHorizontal(3).Text("Role(s) This Week").Bold().FontColor(Colors.White).FontSize(13f);
                                    header.Cell().Background(Colors.Purple.Darken3).PaddingVertical(3).PaddingHorizontal(2).AlignCenter().Text("Milestone").Bold().FontColor(Colors.White).FontSize(13f);
                                    header.Cell().Background(Colors.Purple.Darken3).PaddingVertical(3).PaddingHorizontal(2).AlignCenter().Text("Credits").Bold().FontColor(Colors.White).FontSize(13f);
                                });

                                // Table Rows
                                for (int vi = 0; vi < volunteerProfiles.Count; vi++)
                                {
                                    var vp = volunteerProfiles[vi];
                                    string rowBg = (vi % 2 == 0) ? Colors.White : Colors.Grey.Lighten5;

                                    // Name (15pt name, clickable link, no ID)
                                    string profileUrl = $"https://www.parkrun.org.uk/parkrunner/{vp.ParkrunnerId}/";
                                    vTable.Cell().Background(rowBg).PaddingVertical(1.2f).PaddingHorizontal(3).Hyperlink(profileUrl).Text(vp.ParkrunnerName).FontSize(15f).SemiBold().FontColor(Colors.Purple.Darken4);

                                    // Event Name (13pt)
                                    string cleanEvent = vp.EventName.Replace(" parkrun", "", StringComparison.OrdinalIgnoreCase).Trim();
                                    vTable.Cell().Background(rowBg).PaddingVertical(1.2f).PaddingHorizontal(3).Text(cleanEvent).FontSize(13f).FontColor(Colors.Grey.Darken3);

                                    // Role(s) This Week (13pt semi-bold)
                                    vTable.Cell().Background(rowBg).PaddingVertical(1.2f).PaddingHorizontal(3).Text(vp.RoleThisWeek).FontSize(13f).SemiBold().FontColor(Colors.Purple.Darken3);

                                    // Milestone Badge
                                    var msCell = vTable.Cell().Background(rowBg).PaddingVertical(1.2f).PaddingHorizontal(2).AlignCenter();
                                    if (!string.IsNullOrEmpty(vp.HighestMilestone) && vp.HighestMilestone != "-")
                                    {
                                        string ms = vp.HighestMilestone.ToUpperInvariant();
                                        (string badgeBg, string badgeTextColor) = ms switch
                                        {
                                            "V1000" => ("#FFE049", "#1D1D1B"),
                                            "V900"  => ("#5C8B30", "#FFFFFF"),
                                            "V800"  => ("#7F217D", "#FFFFFF"),
                                            "V700"  => ("#252E5F", "#FFFFFF"),
                                            "V600"  => ("#B1D600", "#2B233D"),
                                            "V500"  => ("#2E4DA7", "#FFFFFF"),
                                            "V400"  => ("#88002F", "#FFFFFF"),
                                            "V300"  => ("#E6612D", "#FFFFFF"),
                                            "V250"  => ("#2C504A", "#FFFFFF"),
                                            "V200"  => ("#4FA4D9", "#2B233D"),
                                            "V100"  => ("#1D1D1B", "#FFFFFF"),
                                            "V50"   => ("#C81D31", "#FFFFFF"),
                                            "V25"   => ("#6A5D9E", "#FFFFFF"),
                                            "V10"   => ("#231F20", "#FFFFFF"),
                                            _       => ("#E0E0E0", "#6A1B9A")
                                        };

                                        msCell.Container().Background(badgeBg).PaddingHorizontal(5).PaddingVertical(2).Text(ms).FontSize(11.5f).Bold().FontColor(badgeTextColor);
                                    }
                                    else
                                    {
                                        msCell.Text("-").FontSize(13f).FontColor(Colors.Grey.Lighten1);
                                    }

                                    // Total Credits (13pt bold)
                                    vTable.Cell().Background(rowBg).PaddingVertical(1.2f).PaddingHorizontal(2).AlignCenter().Text($"{vp.TotalCredits:N0}").FontSize(13f).Bold().FontColor(Colors.Purple.Darken4);
                                }
                            });

                            // Callout Thank You Banner - right after the volunteer section (13pt font)
                            volCol.Item().PaddingTop(5).Background(Colors.Purple.Lighten5).Border(1).BorderColor(Colors.Purple.Lighten3).Padding(6).Row(banner =>
                            {
                                banner.Spacing(8);
                                banner.AutoItem().Text("💜").FontSize(14);
                                banner.RelativeItem().Text(t =>
                                {
                                    t.Span("Celebrating Our Volunteers: ").FontSize(13f).Bold().FontColor(Colors.Purple.Darken3);
                                    t.Span($"Every parkrun event is 100% volunteer-led. Huge thanks to all {clubName} members who volunteered their time to support the running community this weekend! If you would like to help at an upcoming event, chat with our club team or sign up on your local parkrun roster.").FontSize(13f).FontColor(Colors.Purple.Darken4);
                                });
                            });
                        }
                        else
                        {
                            // Always show volunteering section even when 0 active volunteers detected this specific week
                            volCol.Item().Row(vr =>
                            {
                                vr.RelativeItem().Column(vc =>
                                {
                                    vc.Item().Row(r =>
                                    {
                                        r.Spacing(6);
                                        r.AutoItem().Text("💜").FontSize(10.5f);
                                        r.AutoItem().Text("Club Volunteers & Community Champions")
                                            .FontSize(11f).Bold().FontColor(Colors.Purple.Darken3);
                                    });
                                    vc.Item().PaddingTop(1).Text($"Celebrating {clubName} members who volunteer and support the parkrun community ({eventDate}).")
                                        .FontSize(8f).FontColor(Colors.Grey.Darken1);
                                });
                            });

                            volCol.Item().PaddingTop(4).Background(Colors.Purple.Lighten5).Border(1).BorderColor(Colors.Purple.Lighten3).Padding(8).Row(banner =>
                            {
                                banner.Spacing(8);
                                banner.AutoItem().Text("💜").FontSize(16);
                                banner.RelativeItem().Column(bc =>
                                {
                                    bc.Item().Text(t =>
                                    {
                                        t.Span("Celebrating Our Volunteers: ").FontSize(12.5f).Bold().FontColor(Colors.Purple.Darken3);
                                        t.Span("Every parkrun event is 100% volunteer-led! While no club members were registered on the volunteer rosters this weekend, huge thanks to all our regular volunteers and community champions who help keep parkrun happening.").FontSize(12f).FontColor(Colors.Purple.Darken4);
                                    });
                                    bc.Item().PaddingTop(3).Text(t =>
                                    {
                                        t.Span("Want to get involved? ").FontSize(11.5f).Bold().FontColor(Colors.Purple.Darken3);
                                        t.Span("If you would like to volunteer at an upcoming event (as a marshal, barcode scanner, timekeeper, or tail walker), chat with our club team or sign up on your local parkrun roster.").FontSize(11.5f).FontColor(Colors.Purple.Darken4);
                                    });
                                });
                            });
                        }
                    });

                    // =========================================================================
                    // Upcoming Milestones Section (Runners & Volunteering - End of Report)
                    // =========================================================================
                    if (upcomingMilestones != null && upcomingMilestones.Count > 0)
                    {
                        var upcomingRuns = upcomingMilestones.Where(u => u.MilestoneType == "Run").ToList();
                        var upcomingVols = upcomingMilestones.Where(u => u.MilestoneType == "Volunteer").ToList();

                        col.Item().ShowEntire().PaddingTop(8).Column(upCol =>
                        {
                            upCol.Item().Row(ur =>
                            {
                                ur.RelativeItem().Column(uc =>
                                {
                                    uc.Item().Row(r =>
                                    {
                                        r.Spacing(6);
                                        r.AutoItem().Text("🎯").FontSize(10.5f);
                                        r.AutoItem().Text("Upcoming Club Milestones Watch (Within 5 Runs / Credits)")
                                            .FontSize(11f).Bold().FontColor(Colors.Indigo.Darken3);
                                    });
                                    uc.Item().PaddingTop(1).Text("Members approaching their next official parkrun running or volunteering milestone club.")
                                        .FontSize(8f).FontColor(Colors.Grey.Darken1);
                                });
                            });

                            upCol.Item().PaddingTop(3).Row(splitRow =>
                            {
                                splitRow.Spacing(10);

                                // Left Table: Upcoming Running Milestones
                                splitRow.RelativeItem(5.5f).Column(runCol =>
                                {
                                    runCol.Item().Background(Colors.Indigo.Darken3).PaddingVertical(2.5f).PaddingHorizontal(4)
                                        .Text($"🏃 Upcoming Running Milestones ({upcomingRuns.Count})")
                                        .FontSize(9.5f).Bold().FontColor(Colors.White);

                                    if (upcomingRuns.Count > 0)
                                    {
                                        runCol.Item().Table(rTable =>
                                        {
                                            rTable.ColumnsDefinition(cols =>
                                            {
                                                cols.RelativeColumn(4.2f); // Name
                                                cols.ConstantColumn(38);   // Current
                                                cols.ConstantColumn(58);   // Milestone Club
                                                cols.ConstantColumn(48);   // To Go
                                            });

                                            rTable.Header(h =>
                                            {
                                                h.Cell().Background(Colors.Indigo.Lighten5).PaddingVertical(2).PaddingHorizontal(3).Text("Parkrunner").FontSize(8f).Bold().FontColor(Colors.Indigo.Darken4);
                                                h.Cell().Background(Colors.Indigo.Lighten5).PaddingVertical(2).PaddingHorizontal(2).AlignCenter().Text("Runs").FontSize(8f).Bold().FontColor(Colors.Indigo.Darken4);
                                                h.Cell().Background(Colors.Indigo.Lighten5).PaddingVertical(2).PaddingHorizontal(2).AlignCenter().Text("Next Club").FontSize(8f).Bold().FontColor(Colors.Indigo.Darken4);
                                                h.Cell().Background(Colors.Indigo.Lighten5).PaddingVertical(2).PaddingHorizontal(2).AlignCenter().Text("To Go").FontSize(8f).Bold().FontColor(Colors.Indigo.Darken4);
                                            });

                                            for (int ri = 0; ri < upcomingRuns.Count; ri++)
                                            {
                                                var u = upcomingRuns[ri];
                                                string rBg = (ri % 2 == 0) ? Colors.White : Colors.Grey.Lighten5;
                                                bool isNextRun = u.Remaining == 1;
                                                if (isNextRun) rBg = Colors.Pink.Lighten5;

                                                var nameCell = rTable.Cell().Background(rBg).PaddingVertical(1.5f).PaddingHorizontal(3);
                                                if (!string.IsNullOrEmpty(u.ProfileUrl))
                                                    nameCell.Hyperlink(u.ProfileUrl).Text(u.ParkrunnerName).FontSize(8.5f).SemiBold().FontColor(Colors.Indigo.Darken4);
                                                else
                                                    nameCell.Text(u.ParkrunnerName).FontSize(8.5f).SemiBold();

                                                rTable.Cell().Background(rBg).PaddingVertical(1.5f).PaddingHorizontal(2).AlignCenter()
                                                    .Text($"{u.CurrentCount}").FontSize(8.5f).Bold().FontColor(Colors.Grey.Darken3);

                                                rTable.Cell().Background(rBg).PaddingVertical(1.5f).PaddingHorizontal(2).AlignCenter()
                                                    .Container().Background(u.BadgeBgColor).PaddingHorizontal(4).PaddingVertical(1)
                                                    .Text(u.MilestoneClub).FontSize(7.5f).Bold().FontColor(u.BadgeTextColor);

                                                rTable.Cell().Background(rBg).PaddingVertical(1.5f).PaddingHorizontal(2).AlignCenter()
                                                    .Text($"{u.Remaining} away").FontSize(8f).Bold().FontColor(isNextRun ? Colors.Pink.Darken3 : Colors.Indigo.Darken2);
                                            }
                                        });
                                    }
                                    else
                                    {
                                        runCol.Item().Background(Colors.Grey.Lighten5).Padding(6).Text("No runners within 5 runs of a milestone.").FontSize(8f).FontColor(Colors.Grey.Darken1);
                                    }
                                });

                                // Right Table: Upcoming Volunteering Milestones
                                splitRow.RelativeItem(4.5f).Column(vCol =>
                                {
                                    vCol.Item().Background(Colors.Purple.Darken3).PaddingVertical(2.5f).PaddingHorizontal(4)
                                        .Text($"💜 Upcoming Volunteer Milestones ({upcomingVols.Count})")
                                        .FontSize(9.5f).Bold().FontColor(Colors.White);

                                    if (upcomingVols.Count > 0)
                                    {
                                        vCol.Item().Table(vTable =>
                                        {
                                            vTable.ColumnsDefinition(cols =>
                                            {
                                                cols.RelativeColumn(4.2f); // Name
                                                cols.ConstantColumn(38);   // Credits
                                                cols.ConstantColumn(54);   // Target Club
                                                cols.ConstantColumn(46);   // To Go
                                            });

                                            vTable.Header(h =>
                                            {
                                                h.Cell().Background(Colors.Purple.Lighten5).PaddingVertical(2).PaddingHorizontal(3).Text("Volunteer").FontSize(8f).Bold().FontColor(Colors.Purple.Darken4);
                                                h.Cell().Background(Colors.Purple.Lighten5).PaddingVertical(2).PaddingHorizontal(2).AlignCenter().Text("Credits").FontSize(8f).Bold().FontColor(Colors.Purple.Darken4);
                                                h.Cell().Background(Colors.Purple.Lighten5).PaddingVertical(2).PaddingHorizontal(2).AlignCenter().Text("Next Club").FontSize(8f).Bold().FontColor(Colors.Purple.Darken4);
                                                h.Cell().Background(Colors.Purple.Lighten5).PaddingVertical(2).PaddingHorizontal(2).AlignCenter().Text("To Go").FontSize(8f).Bold().FontColor(Colors.Purple.Darken4);
                                            });

                                            for (int vi = 0; vi < upcomingVols.Count; vi++)
                                            {
                                                var u = upcomingVols[vi];
                                                string vBg = (vi % 2 == 0) ? Colors.White : Colors.Grey.Lighten5;
                                                bool isNextVol = u.Remaining == 1;
                                                if (isNextVol) vBg = Colors.Pink.Lighten5;

                                                var nameCell = vTable.Cell().Background(vBg).PaddingVertical(1.5f).PaddingHorizontal(3);
                                                if (!string.IsNullOrEmpty(u.ProfileUrl))
                                                    nameCell.Hyperlink(u.ProfileUrl).Text(u.ParkrunnerName).FontSize(8.5f).SemiBold().FontColor(Colors.Purple.Darken4);
                                                else
                                                    nameCell.Text(u.ParkrunnerName).FontSize(8.5f).SemiBold();

                                                vTable.Cell().Background(vBg).PaddingVertical(1.5f).PaddingHorizontal(2).AlignCenter()
                                                    .Text($"{u.CurrentCount}").FontSize(8.5f).Bold().FontColor(Colors.Grey.Darken3);

                                                vTable.Cell().Background(vBg).PaddingVertical(1.5f).PaddingHorizontal(2).AlignCenter()
                                                    .Container().Background(u.BadgeBgColor).PaddingHorizontal(4).PaddingVertical(1)
                                                    .Text(u.MilestoneClub).FontSize(7.5f).Bold().FontColor(u.BadgeTextColor);

                                                vTable.Cell().Background(vBg).PaddingVertical(1.5f).PaddingHorizontal(2).AlignCenter()
                                                    .Text($"{u.Remaining} away").FontSize(8f).Bold().FontColor(isNextVol ? Colors.Pink.Darken3 : Colors.Purple.Darken2);
                                            }
                                        });
                                    }
                                    else
                                    {
                                        vCol.Item().Background(Colors.Grey.Lighten5).Padding(6).Text("No volunteers within 5 credits of a milestone.").FontSize(8f).FontColor(Colors.Grey.Darken1);
                                    }
                                });
                            });
                        });
                    }
                });

                // Footer
                page.Footer().Column(footer =>
                {
                    footer.Item().LineHorizontal(0.5f).LineColor(Colors.Grey.Lighten2);
                    footer.Item().PaddingTop(4).Row(row =>
                    {
                        row.RelativeItem();
                        row.AutoItem().Text(text =>
                        {
                            text.Span("Page ").FontSize(7.5f).FontColor(Colors.Grey.Medium);
                            text.CurrentPageNumber().FontSize(7.5f).FontColor(Colors.Grey.Darken2).Bold();
                            text.Span(" of ").FontSize(7.5f).FontColor(Colors.Grey.Medium);
                            text.TotalPages().FontSize(7.5f).FontColor(Colors.Grey.Darken2).Bold();
                        });
                    });
                });
            });
        });

        doc.GeneratePdf(fullPath);

        Console.WriteLine($"[Success] Successfully generated PDF report ({records.Count:N0} records) at: {fullPath}");
    }
}
