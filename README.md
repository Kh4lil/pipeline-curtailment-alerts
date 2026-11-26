# Pipeline Curtailment Alerts

A C# console app that scans Sabine Pipe Line's public bulletin for recent disruptions near Louisiana or Henry Hub and sends matching notices to Slack. Each alert shows the rule match, affected segment and quoted volume when available, and a link to the full notice.

## Run it

Install the [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0), then run these commands from the repo root.

```sh
dotnet run --project src/PipelineAlerts -- --demo
dotnet run --project src/PipelineAlerts -- --demo-synthetic
dotnet run --project src/PipelineAlerts -- --dry-run
dotnet test
```

Start the review with `--demo`. It replays Sabine notice 440 as of June 20, 2026. The parser reads a PDF excerpt from the notice and finds a planned outage with a 350,000 Dth/day delivery limit at Bridgeline East. The reported volume is the exercise's **extraction bonus**. The console prints the Slack message marked **HISTORICAL REPLAY**, with the original posting date and full notice link. [Original Sabine notice](https://www.gasnom.com/ip/SABINE/notice_details_new.cfm?id=440&type=3)

Use `--demo-synthetic` to see the original three sample notices and the matching Henry Hub alert. Both replays use a local Slack receiver, ignore live configuration, and work offline after the first build. Add `--no-build` to run without rebuilding. Use `--dry-run` to check the live bulletin and see which alerts would qualify today.

## Send alerts to Slack

1. Create or open a Slack app in your workspace. Enable **Incoming Webhooks**, select **Add New Webhook**, and authorize it for a test channel. [Slack app settings](https://api.slack.com/apps) · [Slack's setup guide](https://docs.slack.dev/messaging/sending-messages-using-incoming-webhooks/)
2. From the repo root, copy the template and set `SLACK_WEBHOOK_URL` in `.env` to your own webhook URL. Git ignores `.env`.

   ```sh
   test -e .env || cp .env.example .env
   chmod 600 .env
   ```

3. In Bash or Zsh, load the file into the process environment for this run.

   ```sh
   (
     set -a
     . ./.env
     set +a
     dotnet run --project src/PipelineAlerts
   )
   ```

A configured run posts one message per qualifying live notice. If nothing qualifies, the console reports zero signals. Use `--demo` for a repeatable replay with simulated Slack delivery.

## Optional stretch goals

### Configurable bulletin URL

Set `PIPELINE_BULLETIN_URL` in `.env` to an HTTPS bulletin base URL, with an optional trailing slash. The default is Sabine's public bulletin. An override must use Sabine-format listings, details, and PDF links, and publish timestamps in U.S. Central time. `PIPELINE_NAME` sets the label in the console and Slack, and defaults to Sabine Pipe Line. [Sabine bulletin](https://www.gasnom.com/ip/SABINE/)

```sh
PIPELINE_BULLETIN_URL=https://www.gasnom.com/ip/SABINE/
PIPELINE_NAME="Sabine Pipe Line"
```

### Retry and failure handling

The app retries transient bulletin reads once after a short delay. It attempts each Slack post once per run. The app reports unresolved bulletin or Slack failures in the console and exits with a nonzero status.

### Run on a schedule

Configure `SLACK_WEBHOOK_URL` first. Publish once, then rehearse the scheduled run against the live bulletin. Qualifying notices can post to the configured channel.

```sh
dotnet publish src/PipelineAlerts -c Release -o src/PipelineAlerts/bin/Release/publish
sh scripts/run-scheduled.sh
```

To run every 15 minutes, replace the example path with the absolute path to your checkout and add the line to your user crontab. Set the crontab's `PATH` to include the directory containing `dotnet`.

```cron
*/15 * * * * /bin/sh "/absolute/path/to/pipeline-curtailment-alerts/scripts/run-scheduled.sh"
```

The runner loads `.env` when present and uses `--scheduled .pipeline-alert-deliveries`. Scheduled runs require a webhook and store each accepted notice revision in that state file. Later runs skip those revisions. Use a different path with `--scheduled <state-file>` for a separate feed or channel. If Slack accepts the message but the app times out or crashes before recording delivery, a later check may repeat the alert.

## How it works

`Program` coordinates the notice source, signal detector, and Slack sender. Each component can be tested offline.

- **Fetch.** `SabineNoticeSource` reads all five public notice tables with `HttpClient` and Html Agility Pack. It interprets timestamps as U.S. Central time and checks the posting window before opening each detail page. It reads HTML and text PDFs, capturing cities and states, labeled meter tables, and side-by-side `name (Loc ID)` entries. [Sabine bulletin](https://www.gasnom.com/ip/SABINE/)
- **Match.** `SignalDetector` requires a posting in the last 72 hours, a force majeure, outage, or curtailment keyword in the title or body, and location evidence of Louisiana, Henry Hub, or a listed connection such as Bridgeline East or `SONAT - HH`. It recognizes postal addresses such as `Lake Charles, LA` and looks for location evidence in the title, segment, and body. It captures the first MMBtu or Dth quantity it finds, including a daily unit when present. [FERC Energy Markets Primer, figure 1-8](https://www.ferc.gov/sites/default/files/2024-01/24_Energy-Markets-Primer_0117_DIGITAL_0.pdf) · [ONEOK Henry Hub connections](https://www.oneok.com/sabinehubservices)
- **Report.** `Program` prints the notice and match counts with a preview of each match. `SlackNotifier` sends the alert and reports delivery errors. A failed fetch or send returns a nonzero exit code.

### Technical choices

- **Run one check at a time.** The C# app fetches, matches, and sends in a single invocation. Manual runs and cron use the same entry point, which keeps polling cadence outside the app.
- **Keep bulletin parsing separate from trading rules.** `SabineNoticeSource` uses Html Agility Pack and PdfPig to turn bulletin HTML and PDF text into a `Notice`. It checks the posting time on the listing first to avoid downloading details outside the 72-hour window. `SignalDetector` takes a notice and a reference time, so historical replays and date-boundary tests exercise the same rules as a live run.
- **Show why an alert matters.** The hardcoded rules require both disruption and market evidence. The Slack alert shows the match and links to the exact notice so a trader can check the affected segment and see what the reported volume represents.
- **Limit delivery attempts.** A transient bulletin read can be retried once. Each Slack send gets one attempt per run because a timeout can leave delivery uncertain. Scheduled checks record confirmed deliveries by notice revision so overlapping 72-hour windows can skip alerts already sent.

The keyword rules skip direct denials and explicit resolutions such as “force majeure lifted.” A denial in the notice body can override the headline. For example, “no outages” dismisses “outage” in the headline. Geography checks ignore standalone Sabine letterhead and phrases such as “Henry Hub deliveries are unaffected.” The checks use specific phrases, so wording such as “flow restricted” still needs a qualifying keyword. A quoted quantity can be remaining capacity or the size of a cut. The full notice provides that context.

## From proof of concept to production

Add a parser for each additional bulletin format, and configure the market names for each feed. For multiple workers, move delivery history into a shared database and coordinate scheduled runs. Monitor bulletin format changes and alert on persistent source or delivery failures.

As feeds are added, keep bulletin URLs and market rules in a configuration file so operators can update them without rebuilding.
