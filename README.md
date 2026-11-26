# Pipeline Curtailment Alerts

A C# console app that scans Sabine Pipe Line's public bulletin for recent disruptions near Louisiana or Henry Hub and sends matching notices to Slack. Each alert shows the rule match, affected segment and quoted volume when available, and a link to the full notice.

## Run it

Install the [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0), then run these commands from the repo root.

```sh
dotnet run --project src/PipelineAlerts -- --demo
dotnet run --project src/PipelineAlerts -- --dry-run
dotnet test
```

Start the review with `--demo`. Three recent sample notices go through the same parser, rules, and Slack sender as a live run. A local receiver prints the one matching Henry Hub alert, including 48,000 Dth/day extracted from a PDF, then simulates Slack's acknowledgement. Use `--dry-run` to see the live bulletin and its current match count in preview mode.

After the first build, replay the demo offline with `dotnet run --no-build --project src/PipelineAlerts -- --demo`. The replay uses no Slack credentials.

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
     . ./.env
     export SLACK_WEBHOOK_URL
     dotnet run --project src/PipelineAlerts
   )
   ```

A configured run posts one message per qualifying live notice. If nothing qualifies, the console reports zero signals. Use `--demo` for a repeatable replay with simulated Slack delivery.

## How it works

- **Fetch.** `SabineNoticeSource` reads all five public notice tables with `HttpClient` and Html Agility Pack. It interprets timestamps as U.S. Central time and checks the posting window before opening each detail page. It reads HTML and text PDFs, capturing cities and states, labeled meter tables, and side-by-side `name (Loc ID)` entries. [Sabine bulletin](https://www.gasnom.com/ip/SABINE/)
- **Match.** `SignalDetector` requires a posting in the last 72 hours, a force majeure, outage, or curtailment keyword in the title or body, and location evidence of Louisiana, Henry Hub, or a listed connection such as Bridgeline East or `SONAT - HH`. It recognizes postal addresses such as `Lake Charles, LA` and looks for location evidence in the title, segment, and body. It captures the first MMBtu or Dth quantity it finds, including a daily unit when present. [FERC Energy Markets Primer, figure 1-8](https://www.ferc.gov/sites/default/files/2024-01/24_Energy-Markets-Primer_0117_DIGITAL_0.pdf) · [ONEOK Henry Hub connections](https://www.oneok.com/sabinehubservices)
- **Report.** `Program` prints the notice and match counts with a preview of each match. `SlackNotifier` sends the alert and reports delivery errors. A failed fetch or send returns a nonzero exit code.

The keyword rules skip direct denials such as “no outage” and explicit resolutions such as “force majeure lifted.” A denial in the notice body can override a matching headline. Geography checks ignore standalone Sabine letterhead and phrases such as “Henry Hub deliveries are unaffected.” The checks use specific phrases, so wording such as “flow restricted” still needs a qualifying keyword. A quoted quantity can be remaining capacity or the size of a cut. The full notice provides that context.

## From proof of concept to production

Add a parser per bulletin format and move source URLs and market names to configuration. Store delivered notice IDs and revisions so scheduled runs can distinguish new or changed notices from repeats. Once delivery is tracked, run on cron or Windows Task Scheduler, retry transient failures, and monitor source health and format changes.
