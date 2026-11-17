# Windows Event Log Monitor

A small .NET 10 Windows Service that watches local Windows Event Logs and sends notifications when an event matches a configured rule. It is intended for a small number of Windows servers and does not require a central monitoring server, database, Docker, or Linux.

**ZIP contents:** source code and PowerShell build/install scripts (not precompiled EXEs). Run `scripts\publish.ps1` on a Windows build computer with the .NET 10 SDK to produce self-contained Windows executables for the service and rule-builder GUI.

## What it does

- Subscribes to new events in the configured Windows Event Logs (no periodic polling).
- Filters by log name, Event ID, provider/source, level, message text, and (where event data permits) IIS application pool.
- Sends plain-text email through SMTP.
- Can call an HTTP/HTTPS SMS-provider webhook with a configurable JSON body and headers.
- Supports per-rule cooldowns to reduce repeated notifications.
- Writes service diagnostics to the Windows Application Event Log, source `EventLogMonitor`.
- Starts automatically with Windows when installed as a service.

Each server runs its own copy. For 2–10 servers, install it on each machine and use the same notification configuration. Notifications identify the originating computer. This version does not provide a central dashboard or central event collector.

## Requirements

- Windows Server 2016 or later (or Windows 10/11 for testing).
- .NET 10 SDK to build/publish. The published output is self-contained, so the target machine does not need the .NET runtime installed.
- Administrator rights to install the service.

## Configure

Edit `src\EventLogMonitor\appsettings.json` before publishing, or edit `appsettings.json` in the published folder after publishing.

### Logs to watch

```json
"Logs": [ "Application", "System", "Security", "MyCompany-Product/Operational" ]
```

Use actual channel names from Event Viewer. The service subscribes only to the logs listed here. It handles new events written after the service starts; it does not replay historical events.

### Event rules

Example:

```json
{
  "Name": "Failed logons",
  "Enabled": true,
  "LogName": "Security",
  "EventIds": [ 4625 ],
  "Providers": [ "Microsoft-Windows-Security-Auditing" ],
  "Levels": [],
  "MessageContains": "administrator",
  "MessageRegex": "",
  "ApplicationPools": [],
  "CooldownSeconds": 300,
  "NotifyEmail": true,
  "NotifySms": false
}
```

- `LogName`: exact log name, or `*` for any log listed under `Logs`.
- `EventIds`: empty means any Event ID.
- `Providers`: exact provider/source names; empty means any provider.
- `Levels`: accepted names are `Critical`, `Error`, `Warning`, `Information` (also `Informational` or `Info`), and `Verbose` (also `Debug`). Empty means any level.
- `MessageContains`: simple case-insensitive substring match against the rendered event message. Empty means any message.
- `MessageRegex`: optional advanced .NET regular expression matched against the rendered event message. Empty means any message.
- `ApplicationPools`: optional IIS pool names. Empty means no pool filter. Pool attribution uses event XML fields, explicit message references, and event process ID matched to IIS worker processes where available; not every event can be attributed to a pool.
- `CooldownSeconds`: suppresses further notifications for that rule after a successful notification. `0` disables cooldown.
- `NotifyEmail` / `NotifySms`: select the channel(s) for that rule.

The sample configuration includes IIS/.NET-focused rules. A generic non-IIS application-crash rule and the Security failed-logon rule are disabled by default. The ASP.NET Core Module rule is provider-based and may need adjusting to match the provider name on your server.

### Email

Set `Notifications:Email:Enabled` to `true` and configure:

- `SmtpHost`, `SmtpPort`
- `EnableSsl` (STARTTLS; this setting is not implicit TLS for port 465)
- `UserName`, `Password` (leave `UserName` empty for an unauthenticated relay)
- `From`, `To`

The app uses SMTP username/password authentication when `UserName` is supplied. Some mail providers disable SMTP password authentication; use a permitted SMTP relay or provider-specific app password where applicable.

Avoid storing real credentials in source control. You can override the password using a machine environment variable before starting the service:

```powershell
[Environment]::SetEnvironmentVariable('Notifications__Email__Password', 'YOUR_PASSWORD', 'Machine')
```

Restart the service after changing machine environment variables.

### SMS webhook

SMS providers have different API URLs, authentication methods, and request bodies. This project therefore uses a generic HTTP webhook rather than claiming compatibility with every SMS provider.

Set `Notifications:SmsWebhook:Enabled` to `true`, then configure `Url`, optional `Headers`, `Method`, and `BodyTemplate`. The body template is sent as the request body. Placeholders are replaced with JSON-encoded values:

- `{{message}}`, `{{subject}}`, `{{machine}}`, `{{log}}`
- `{{eventId}}` (number)
- `{{provider}}`, `{{time}}`, `{{rule}}`

For example, a provider expecting `to` and `message` fields could use:

```json
"BodyTemplate": "{\"to\":\"+972501234567\",\"message\":{{message}}}"
```

Add the authentication header required by your SMS provider in `Headers`. Check the provider's API documentation for the exact URL, body format, and authentication. An HTTP 2xx response means the webhook accepted the request; it does not guarantee final SMS delivery. Provider charges may apply.

## Build and install

1. Install the .NET 10 SDK on a build machine.
2. Edit the configuration in `src\EventLogMonitor\appsettings.json`.
3. Open PowerShell in the project folder and run:

```powershell
.\scripts\publish.ps1
```

4. Review `publish\appsettings.json` and add credentials there, or set machine environment variables.
5. From an elevated PowerShell window, run:

```powershell
.\scripts\install.ps1 -PublishDirectory .\publish
```

The service is installed as `EventLogMonitor` and runs as LocalSystem by default. LocalSystem normally has the permissions needed to read the local Security log. If you change the service account, grant it read access to every monitored log; Security log access needs particular care.

To uninstall:

```powershell
.\scripts\uninstall.ps1
```

Uninstalling removes the Windows service but leaves the published files and configuration in place.

## Service management

```powershell
Get-Service EventLogMonitor
Start-Service EventLogMonitor
Stop-Service EventLogMonitor
Restart-Service EventLogMonitor
```

Service diagnostics appear in Event Viewer under **Windows Logs → Application**, source `EventLogMonitor`.

## Test safely

Start by configuring one harmless test rule for an event you can generate in a non-production environment. Confirm that the service starts, the matching rule appears in the Application log, and the notification reaches the configured destination. Then enable the production rules.

For Security log monitoring, verify that the relevant Windows audit policy actually generates the events you expect. For example, Event ID 4625 requires failed-logon auditing to be enabled.

## Notes and limitations

- This is a lightweight per-machine alerting service, not a SIEM or a central monitoring console.
- Event subscriptions begin at service startup; old events are not replayed.
- The service keeps cooldown state in memory. A service restart resets cooldowns.
- Failed notifications are logged but are not queued for later retry. Use a reliable SMTP relay/webhook and monitor the service's own Application log.
- Regex rules use the .NET regular-expression syntax and have a one-second match timeout.
- The SMS integration is generic. The request template must match the chosen provider's API.
- The project targets `net10.0-windows` and pins Microsoft package references to `10.0.12`.

## Project layout

```text
EventLogMonitor.sln
README.md
src/EventLogMonitor.Configurator/
  EventLogMonitor.Configurator.csproj
  Program.cs
  MainForm.cs
src/EventLogMonitor.Configurator/Ai/
  AiSettings.cs, AiSettingsDialog.cs, OllamaClient.cs, PromptBuilder.cs,
  RuleDraft.cs, RuleDrafter.cs, ScenarioCatalog.cs
docs/
  AI-RULE-DRAFTING.md
src/EventLogMonitor/
  EventLogMonitor.csproj
  Program.cs
  Options.cs
  EventSnapshot.cs
  EventLogMonitorWorker.cs
  NotificationDispatcher.cs
  appsettings.json
scripts/
  publish.ps1
  install.ps1
  uninstall.ps1
```

## AI rule drafting (added in 1.2)

The Rule Builder has a **Describe the rule in plain English** box and a **Draft with AI** button. A local model (Ollama, no training, nothing leaves the machine) drafts the rules; you review and preview them, and nothing is saved until you click **Save configuration**. The monitoring service is not affected and does not use AI.

Quick start: install Ollama, run `ollama pull qwen2.5:7b`, open the Configurator, click **AI settings…** → **Test connection**, then type for example *"I have ASP.NET application 'Claims' in local IIS. Watch critical errors and notify me when only an IIS reset can help."*

Full documentation: [docs/AI-RULE-DRAFTING.md](docs/AI-RULE-DRAFTING.md).

## Rule Builder GUI (added in 1.1)

The ZIP now includes a Windows Forms utility named **EventLogMonitor.Configurator.exe**. Use it instead of hand-editing the `Rules` JSON.

### Build and launch

1. Install the .NET 10 SDK on a Windows build computer.
2. Run `scripts\publish.ps1`.
3. Open the `publish` folder and run `EventLogMonitor.Configurator.exe` (run as Administrator to read Security logs and enumerate IIS configuration reliably).
4. Select the `appsettings.json` used by the service if the configurator did not select it automatically.
5. Select an Event Log, click **Load recent events**, then check the event sources, Event IDs and levels you want.
6. Optionally check one or more IIS application pools and enter a phrase in **Message contains**.
7. Click **Save rule**, then **Save configuration**.
8. Restart the `EventLogMonitor` Windows service to load the new rules.

The builder reads up to 600 recent events from the selected channel. It uses those entries to populate provider/source names, Event IDs and levels. The Event ID field also accepts manually typed comma-separated values, so an ID does not need to exist in the most recent sample. **Preview matches** shows which of the sampled events match the current selections. Preview is not a live test and does not send notifications.

An empty selection for Providers, Event IDs, Levels or Application Pools means **Any**. The Message contains field is a simple case-insensitive text search; no regular expressions are required. Existing advanced `MessageRegex` values remain supported by the service, but are not edited by the basic GUI.

### IIS application-pool matching

The builder obtains configured pool names through the IIS `Microsoft.Web.Administration` API when IIS management components are installed. You can refresh the list or add a pool name manually.

At runtime, the service attributes an event to an application pool using, in order:

- Explicit pool fields in the event XML (for example, fields whose names contain `AppPool`).
- An explicit `application pool 'Name'` reference in the rendered event message.
- The event's process ID matched against the live IIS worker-process list (`WorkerProcess.ProcessId` → `WorkerProcess.AppPoolName`). The live map refreshes every five seconds.

This allows many WAS events and events produced by an in-process `w3wp.exe` worker to be filtered by pool. It is not possible to attribute every Windows event to an application pool. In particular, an event that contains neither a pool name nor a process ID cannot be attributed. A process that has already exited may no longer be in the live worker-process map. With ASP.NET Core **out-of-process** hosting, the app runs in a child `dotnet.exe` process, so its PID is not necessarily the IIS `w3wp.exe` PID; do not assume that every such .NET event can be mapped to the pool. If a pool filter is selected, un-attributable events do not match that rule.

### IIS/.NET starter rules

The configurator has an **Add IIS/.NET templates** button. It adds non-duplicate starter rules for:

- WAS application-pool failures in the System log (Event IDs 5002, 5011 and 5059).
- ASP.NET application errors in the Application log (Event ID 1309).
- .NET Runtime unhandled exceptions (Event ID 1026).
- Application crashes mentioning `w3wp.exe` (Event IDs 1000 and 1001).
- ASP.NET Core Module errors in the Application log, selected by provider name.

Treat these as starting points, not a universal IIS event catalogue. Event sources and available channels vary by Windows/IIS/.NET installation. Load recent events on each target server and adjust the templates to its actual provider names and events. Microsoft documents WAS Event ID 5011 and the Application Error / .NET Runtime crash patterns in its IIS troubleshooting guidance: [IIS application-pool process crash troubleshooting](https://learn.microsoft.com/troubleshoot/developer/webapps/iis/site-behavior-performance/process-termination-crash). Microsoft also documents the Application Event Log sources used by ASP.NET Core Module: [Troubleshoot ASP.NET Core on IIS](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/iis/troubleshoot).

The GUI edits only the `EventLogMonitor` section. SMTP and SMS-provider credentials/settings remain in the existing `Notifications` section and are preserved when saving.
