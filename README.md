# Parkrun Consolidated Club Results Scraper (.NET 10 / C#)

A C# .NET console application that scrapes and extracts **Parkrun Consolidated Club Results** across all worldwide parkrun events for **Birmingham Swifts (Club #21925)** (or any other club) and automatically generates a styled **PDF report** directly in your `~/Downloads` folder.

---

## Features

- **Weekly Club Milestones Tracking**:
  - Automatically identifies member running and volunteering milestones across parkrun's full official milestone club tiers (`10` for juniors, `25`, `50`, `100`, `200`, `250`, `300`, `400`, `500`, `600`, `700`, `800`, `900`, `1000` for both Run/Walk and Volunteer clubs) using official parkrun badge colors.
  - Prominently showcases milestone achievers at the start of the report with celebratory cards highlighting their milestone tier, event, finish time, and PB badges.
  - Highlights milestone pills directly beside member names in the results table.
  - Persists all club milestone achievements historically in `data/milestones.json`.
  - Includes an **Upcoming Club Milestones Watch** section at the end of the report tracking members within 5 runs or volunteer credits of their next official milestone club.
- **Weekly Trend Tracking & Graphing**:
  - Automatically records historical weekly stats in `data/history.json`.
  - Dynamically renders an embedded multi-week participation and attendance trend chart using `ScottPlot` with ample vertical height (170pt) and generous top headroom to ensure all data labels are clear and never touch the top border.
- **Weekly Club Volunteers & Event Rosters**:
  - Automatically scrapes event-level volunteer rosters across all attended parkruns to capture both running and non-running volunteers (e.g. Run Directors, Marshals, Timekeepers).
  - Displays exclusively the active volunteers for that specific weekend.
  - Details their assigned role(s) that week, event attended, official milestone badges (`V25`, `V50`, `V100`, `V250`), and lifetime volunteer credits.
  - Caches profiles in `data/volunteers.json` for fast, rate-limit-resilient generation.
- **Clean 2-Page Standard Report**:
  - **Page 1**: Weekly Milestones Section (when milestones occur), followed by Club Runners Table with enlarged typography (13pt headers, 13pt pos, 13pt runner, 13pt time, 15pt finishers, 11.5pt event) and milestone badges. Interactive hyperlinks on runner names.
  - **Page 2**: Taller Weekly Trends Graph, Weekly Volunteers Table (13pt/15pt typography), and Celebrating Our Volunteers appreciation banner (13pt).
- **Resilient, Resumable Scraping**:
  - Per-event results pages sit behind AWS WAF, which serves a CAPTCHA challenge (returned as HTTP `405`) when it detects a burst of automated requests. The scraper spaces requests out (configurable, default **5s**) and retries with backoff to stay under that threshold.
  - Every successfully fetched page is cached under `data/events_<date>/`, so interrupted or partially-blocked runs are **resumable**: re-running the same command re-reads cached events instantly and only re-fetches the ones still missing.
- **Fast & Modern**: Built on .NET 10, `HtmlAgilityPack`, `QuestPDF`, and `ScottPlot`.

---

## Requirements

- **.NET 10 SDK** (on macOS: `brew install dotnet`). The project targets `net10.0`.

## Quick Start

Run with no arguments to use the defaults from [appsettings.json](appsettings.json), or combine any of the flags below. Everything after `--` is passed to the app:

```bash
dotnet run --project ParkrunScraper.csproj -- --club 50750 --date 2026-10-31 --delay 5 --output ~/Downloads/report.pdf
```

All flags are optional:

| Flag | Alias | Description | Default |
| --- | --- | --- | --- |
| `--club` | `-c` | Club numerical ID or full consolidated-club URL | `DefaultClubNum` from config (`50750`) |
| `--date` | `-d` | Event date (`YYYY-MM-DD`) | Most recent Saturday |
| `--delay` | `-w` | Spacing between per-event requests, in seconds (accepts decimals) — raise it if you hit `405` CAPTCHA blocks | `5` |
| `--output` | `-o` | Explicit output PDF path | `~/Downloads/parkrun_<club>_<date>.pdf` |

The request spacing can also be set via the `PARKRUN_REQUEST_DELAY_MS` environment variable (in **milliseconds**); the `--delay` flag takes precedence. The active spacing is printed at startup.

> **Note:** The PDF filename is deterministic for a given club and date, so re-running **overwrites** the previous PDF. Pass `--output` (or move the file first) to keep an earlier copy. The underlying HTML cache in `data/events_<date>/` is never overwritten, so re-runs only fetch events still missing.

---

## Configuration (`appsettings.json`)

Pre-configured for Birmingham Swifts in [appsettings.json](appsettings.json):
```json
{
  "DefaultClubNum": "50750",
  "DefaultClubName": "Flag & Pole Runners",
  "DownloadFolder": "~/Downloads",
  "OutputFilenamePattern": "parkrun_{0}_{1}.pdf",
  "SingleOutputFilename": "parkrun_club_results.pdf",
  "OverwriteSingleFile": false
}
```
