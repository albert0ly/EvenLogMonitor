# AI rule drafting (Configurator 1.2)

The Rule Builder can turn a plain-English request into one or more monitoring rules using a **local** language model.
Nothing is trained, nothing leaves the machine, and the monitoring service itself is unchanged: it never talks to a model.

> Example request:
> *I have ASP.NET application 'Claims' in local IIS. I want to watch all critical errors and send a notification when only an IIS reset can help.*

The chat assistant can inspect rules already in the Notification Rules list, answer follow-up questions, draft new rules and propose edits to existing rules. **Nothing is written to `appsettings.json` until you click Save configuration**.

---

## 1. One-time setup

1. Install **Ollama** for Windows from <https://ollama.com> (it runs as a local service on `http://localhost:11434`).
2. Download a model. For this task a 7–8B model follows the schema reliably:
   ```
   ollama pull qwen2.5:7b
   ```
3. Start `EventLogMonitor.Configurator.exe` (as Administrator, as before), click **AI settings…**, and press **Test connection**.
   It should report *Connected. The model is installed.*

Hardware: a 7B model at 4-bit quantisation needs roughly 5–6 GB of free RAM and answers in a few seconds to a minute on CPU
(the first request after start-up is slower while the model loads). A GPU makes it much faster. If the machine is small,
use `qwen2.5:3b` and expect less reliable results. You can run Ollama on a different, stronger machine: set its address in AI settings.

## 2. Using it

1. Type your request in the box at the top of the right-hand pane.
2. Click **Open AI chat**, then press **Send** (Enter sends; Shift+Enter inserts a line break).
3. Ask follow-up questions or reply to clarification requests in the same chat. For example, “Do you see the last rule?” reads the final item in the Notification Rules list; “show the selected rule” uses the selected item; “list rules” shows names numbered #1, #2, etc. Rule details and proposed changes are formatted as indented JSON.
4. To create or modify a rule, review the proposed fields in the transcript and click **Apply draft** or **Apply edit**. Closing the chat without applying leaves the rules unchanged. An applied change updates only the in-memory list.
5. Review each rule (log, sources, IDs, levels, pools, channels, cooldown). Click **Save configuration** and restart the `EventLogMonitor` service, exactly as before. **Apply draft** does not save to disk. Closing or reloading with unsaved changes prompts you before discarding them.

The chat sees a snapshot of rules already in the Notification Rules list at the time it opens, including the selection. Unsaved typing in the rule editor is not included; click **Save rule** first if those edits should be visible to the assistant. If the request is ambiguous, or names an IIS pool that does not exist, answer the AI's question in the same chat. For ASP.NET Core applications launched by Visual Studio with IIS Express (for example `https://localhost:7166/`), no IIS application pool is required: the assistant creates application/.NET crash and error rules. IIS Express is not full IIS, so the monitor can detect failures but cannot start, stop, reset, or restart the IIS Express process.

More examples:

| Request | Result |
|---|---|
| *Text me when someone fails to log on to this server* | Custom rule: Security log, event 4625, SMS only |
| *Email me if the Billing site hangs* | `IisWorkerUnresponsive` for pool `Billing` |
| *Notify me when a console app crashes with an unhandled exception* | `DotNetRuntimeCrash` (.NET Runtime 1026) |
| *Alert on w3wp crashes, SMS too* | `W3wpCrash` with email and SMS |

## 3. How it works

```
 your text ──► RuleDrafter ──► local model (Ollama, JSON-schema constrained)
                  │                       │
                  │   intent only: scenario, pools, levels, channels, cooldown
                  ▼
        validation + expansion ──► EventRule objects ──► rule list / editor ──► (you) Save configuration
```

Design points:

* **The model picks intent, not event IDs.** For known situations it chooses a *scenario* from `Ai/ScenarioCatalog.cs`; the catalog supplies the log, provider names and Event IDs. A small model cannot invent IDs for those.
* **Constrained output.** The request carries a JSON schema (allowed scenarios and levels, required fields) so the reply always has the right shape. Temperature is 0.
* **Real context in the prompt.** The IIS application pools of the local machine (read with the same `IisApplicationPoolCatalog` the rest of the tool uses) are given to the model, with a few worked examples.
* **Validation in code, after the model answers:**
  * every pool must exist in IIS (a single unambiguous partial match such as `Claims` → `ClaimsAppPool` is accepted; anything else triggers a question). If IIS cannot be read, pool names are accepted but flagged as unverified;
  * levels are normalised (`info` → `Information`);
  * custom rules must name a log that exists on this machine and must be specific (not "every event");
  * rule names are made unique; cooldown is clamped to 0–86400; at least one channel is always on.
* **Human review.** Drafts and edits require **Apply** and live only in memory until **Save configuration**. Custom rules are flagged because their IDs came from the model. Edits are limited to supported rule fields and validated before they can be applied.

### What is sent to the model

The chat transcript, a snapshot of the Notification Rules list (including each rule's settings and selected index), scenario descriptions, and IIS application pool names. No event log content is sent. The configured Ollama endpoint can be a different machine, so use a local endpoint if the rule names and filters must stay on this machine.

## 4. Scenario catalog

Defined in `src/EventLogMonitor.Configurator/Ai/ScenarioCatalog.cs`.

| Scenario | Log | Provider(s) | Event IDs | Default levels |
|---|---|---|---|---|
| `IisPoolFailure` | System | Microsoft-Windows-WAS | 5002, 5057, 5059 | Error |
| `IisWorkerUnresponsive` | System | Microsoft-Windows-WAS | 5010, 5011 | Error, Warning |
| `AspNetUnhandledException` | Application | ASP.NET 4.0.30319.0, ASP.NET 2.0.50727.0 | 1309 | Error, Warning |
| `DotNetRuntimeCrash` | Application | .NET Runtime | 1026 | Error |
| `W3wpCrash` | Application | any | 1000, 1001 + message contains `w3wp.exe` | any |
| `Custom` | model-supplied (validated) | | | |

Notes on accuracy, please read:

* No Windows event says "an IIS reset is needed". `IisPoolFailure` and `IisWorkerUnresponsive` are a **heuristic**: situations where the pool is disabled or a worker process stops responding, which normally stay broken until the pool or IIS is restarted.
* 5002 (pool disabled after a series of failures / rapid-fail protection), 5057 and 5059 (pool disabled, worker process could not be started) and 5011 (fatal communication error with WAS) are documented WAS events. 5010 (worker process did not respond to a ping) is included from general IIS knowledge; **confirm it on your servers** with *Load recent events*.
* Provider names vary by Windows/IIS/.NET installation. Use *Load recent events* and *Preview matches* to confirm a rule matches real events, then adjust the catalog if your servers differ.
* If a pool filter is set, events that cannot be attributed to a pool do not match (see the main README).
* The existing **Add IIS/.NET templates** button is unchanged and does not use the catalog.

### Adding a scenario

1. Add a value to the `Scenario` enum and an entry in `ScenarioCatalog.Items` (description + one `CatalogEntry` per log).
2. Nothing else: the schema, the prompt and the expansion read from the catalog. A clear description matters most, because it is what the model uses to choose.

## 5. Files and settings

| File | Purpose |
|---|---|
| `Ai/AiSettings.cs` | Endpoint, model, timeout. Saved as `ai-settings.json` next to the Configurator executable (not in the service's `appsettings.json`). |
| `Ai/ScenarioCatalog.cs` | Scenario → log / providers / Event IDs. |
| `Ai/RuleDraft.cs` | The JSON shape returned by the model. |
| `Ai/PromptBuilder.cs` | System prompt, examples and JSON schema. |
| `Ai/OllamaClient.cs` | Minimal HTTP client for Ollama (`/api/tags`, `/api/chat`). No NuGet packages. |
| `Ai/RuleDrafter.cs` | Validates and expands new-rule drafts to `EventRule`. |
| `Ai/RuleChatContext.cs`, `Ai/RuleChatService.cs`, `Ai/AiChatDialog.cs` | Rule-list snapshot, contextual chat and explicit Apply actions. |
| `Ai/AiSettingsDialog.cs` | Settings dialog with *Test connection*. |
| `MainForm.cs` | AI chat and settings buttons, rule-list updates after Apply. |

Default `ai-settings.json`:

```json
{ "Endpoint": "http://localhost:11434", "Model": "qwen2.5:7b", "TimeoutSeconds": 180 }
```

To use another runtime later (for example a cloud model), replace `OllamaClient` behind `RuleDrafter`; the rest is unaffected.

## 6. Troubleshooting

| Symptom | Cause / fix |
|---|---|
| *Cannot reach Ollama* | Ollama is not running or the endpoint is wrong. Start it, or fix **AI settings**. |
| *Model … is not installed* | Run `ollama pull <model>` (shown in the message). |
| Timeout on the first request | The model is loading into memory. Retry, increase the timeout, or use a smaller model. |
| *IIS application pool(s) not found* | The pool name does not exist on this machine; the message lists the available pools. |
| Draft matches 0 events in preview | Normal if that situation has not happened recently. Check *Load recent events* for the real provider names and IDs. |
| Wrong or odd rules | Rephrase with the application/pool name and the situation. You can always edit the rule or use the manual builder. |

## 7. Limitations

* The model can still misread a request. That is why drafts are reviewed and previewed before saving.
* One request produces at most the scenarios in the catalog plus custom rules; complex conditions (regular expressions, several phrases) still need the manual editor.
* Event subscriptions and cooldowns work as described in the main README; AI drafting does not change them.
