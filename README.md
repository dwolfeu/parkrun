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
  - **CAPTCHA delegation (token):** when the WAF does serve a CAPTCHA, you can solve it once in a browser and hand the scraper the resulting `aws-waf-token` cookie — see [Getting past the CAPTCHA](#getting-past-the-captcha-aws-waf-tokens) below. Tokens are optional; the polite-backoff + cache behavior still runs without them.
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
| `--token-file` | | Path to the JSON file of pre-seeded WAF tokens | `data/waf-tokens.json` |
| `--token` | | Pre-seed one host's token inline, as `<host>=<token>` (repeatable) | — |
| `--user-agent` | | User-Agent your tokens were solved under (overrides the default/file) | built-in Chrome UA |

The request spacing can also be set via the `PARKRUN_REQUEST_DELAY_MS` environment variable (in **milliseconds**); the `--delay` flag takes precedence. The active spacing is printed at startup.

See [Getting past the CAPTCHA](#getting-past-the-captcha-aws-waf-tokens) for how the token flags work.

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

---

## Getting past the CAPTCHA (AWS WAF tokens)

parkrun sits behind an AWS WAF that intermittently serves a **"Human Verification" CAPTCHA** (an HTTP `405` / `awswaf` challenge page) instead of real results. A plain HTTP client has no JavaScript engine, so it can't clear the challenge on its own. The fix is **CAPTCHA delegation**: you solve it once in a browser and hand the scraper the resulting `aws-waf-token` cookie.

This is a **backup** — the scraper's default path is still polite request spacing + caching + retries, and most runs never need a token. You only supply one when a host actually blocks.

### How it works

1. **Token-first on a block.** When a request comes back as a WAF CAPTCHA, the scraper asks for (or reuses) a token for that host and retries with it. Only genuine CAPTCHA blocks trigger this; transient/network errors still fall through to the normal backoff.
2. **Per-host, lazy prompt.** Tokens are **per domain** — `www.parkrun.com` (the club report) and `www.parkrun.org.uk` (event/profile pages) are separate cookies, and overseas events (`parkrun.com.au`, `parkrun.co.nz`, …) are separate again. If a host blocks and you haven't pre-seeded its token, the scraper prints the blocked URL and prompts you to paste one. It asks **at most once per host per run**.
3. **UA + IP binding.** The token is bound to the **IP** and **User-Agent** of the browser that solved it, so solve it on the **same machine** and make sure the scraper sends the **same UA** (the first prompt also asks for your UA; or pre-seed it with `--user-agent`).
4. **Written back + reused.** Any token you paste at a prompt is saved to the token file (default `data/waf-tokens.json`, git-ignored), so later runs reuse it until it expires.
5. **Unattended-safe.** If no one responds to a prompt within **300s**, the run continues without tokens (blocked events are simply left for the next run, which only re-fetches what's still missing). Non-interactive runs (piped stdin) never prompt.

### Getting a token from your browser

1. Open the blocked URL (the scraper prints it) in a browser and solve the CAPTCHA until the page loads.
2. In that tab's DevTools Console, copy the token to your clipboard:
   ```js
   copy(document.cookie.split('; ').find(c => c.startsWith('aws-waf-token=')))
   ```
3. Paste it at the prompt — or pre-seed it (see below).

### Pre-seeding tokens

Inline on the CLI (repeatable; split on the first `=`, so paste the full `aws-waf-token=...` after the host):

```bash
dotnet run --project ParkrunScraper.csproj -- \
  --user-agent 'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36' \
  --token 'www.parkrun.com=aws-waf-token=...' \
  --token 'www.parkrun.org.uk=aws-waf-token=...'
```

Or via a JSON file (`data/waf-tokens.json` by default, or point `--token-file` elsewhere):

```json
{
  "userAgent": "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36",
  "tokens": {
    "www.parkrun.com": "aws-waf-token=...",
    "www.parkrun.org.uk": "aws-waf-token=..."
  }
}
```

The token value may be the bare token, the `aws-waf-token=...` pair (what the console one-liner yields), or a full `name=value; ...` Cookie header. CLI `--token`/`--user-agent` entries override the file.
