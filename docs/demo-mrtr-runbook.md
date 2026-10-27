# Runbook: MRTR dialog demo (the base sample)

About three and a half minutes of stage time. A tool that can't answer until it knows enough asks the user for what's missing, over several round trips, on a server that keeps nothing in memory between them. `requestState` carries the dialog.

The machine timings and all the output quoted here come from runs on the dev laptop (Windows 11, .NET SDK 10.0.402, Aspire 13.6.0). The narration timings are estimates: they haven't been rehearsed out loud. The interactive prompts couldn't be driven from the test harness, so their beat is based on the code and the `--auto` runs. Check it during rehearsal (see "Not verified").

## Ports

| Port | What | Notes |
|---|---|---|
| 18888 | Aspire dashboard | from the AppHost launch profile |
| 4317 | Dashboard OTLP (gRPC) | terminal-launched agents report here |
| 20144 | Aspire resource service | internal |
| 5199 | `mcp2dialog-server` (`/mcp`) | the agent's default `--server` |

The other two demos use their own ports (see the README's Branches section), so they can run at the same time as this one. If the README's standalone dashboard container (`mcp2dialog-dashboard`) is running, it also binds 18888 and 4317. Stop it first (`docker stop mcp2dialog-dashboard`).

## Before the talk (5 minutes, not on stage)

From the repo root, on branch `main`:

```powershell
dotnet build Mcp2Dialog.slnx                                         # must say 0 errors
dotnet run --project src/Mcp2Dialog.AppHost
```

- The build shows 2 NuGet audit warnings for `MessagePack` (pulled in by Aspire). They're expected and don't affect the demo.
- The dashboard is at http://localhost:18888. **Resources** should list `mcp2dialog-server` as Running and `mcp2dialog-agent` as Not started. Startup took about 5 s on the dev laptop.
- The very first AppHost start on a machine downloads `aspire.cli`, which took about 2.5 minutes and can hit Aspire's 120-second timeout. Do one full start the day before, and don't clear the NuGet cache afterwards.
- In a **second terminal** (this is the one the audience reads), make the font big and the window at least 80 columns wide. All the output fits in 80 columns.
- Warm everything up with one dialog:

  ```powershell
  dotnet run --project src/Mcp2Dialog.Agent --no-build -- quote --auto
  ```

  It should end with the quote `$305.80 including $45.00 heavy overnight surcharge` and a summary of 4 requests, 3 `input_required` results, and 4 questions answered. It took 1.0 s, including startup.
- In the dashboard, open **Console** for `mcp2dialog-server` in a tab, so you can switch to it.

## On stage: the beats

### 0:00–0:30: "The tool doesn't have enough to go on"

**Say:** "One tool, `get_shipping_quote`. Every argument is optional. When something's missing, the server doesn't fail and it doesn't guess: it asks. That's multi round-trip requests in the 2026-07-28 spec."

### 0:30–1:30: the interactive dialog

**Run** (second terminal):

```powershell
dotnet run --project src/Mcp2Dialog.Agent --no-build -- quote
```

**Answer:** `Oslo`, then `42`, then **Overnight**, then **Yes** to the surcharge.

**Point at:**

- The **Connected** panel: `Session id (none: stateless, so requestState correlates the rounds)`. "No `initialize`, no `Mcp-Session-Id`. The server doesn't remember me between calls."
- Round 1 asks two questions at once (destination and weight). The yellow panel headers show the dialog id and the round, for example `dialog ...8f5683f2 · round 1`.
- Round 2's menu is priced from the earlier answers: Standard (5-7 days) $81.50, Express (2 days) $146.70, Overnight (next day) $260.80. "The server asks different questions based on what it already knows."
- Round 3 only exists because the package is heavy *and* overnight: `Overnight packages over 30 kg have a $45.00 surcharge. Accept it? (No switches to express.)`
- The answer and the summary: **`tools/call HTTP requests 4`**, **`input_required results 3`**, **`questions answered 4`**. "One `CallToolAsync` in my code. The SDK did the four round trips."

Answering **No** to the surcharge gives express instead: `$146.70`, still 4 rounds.

### 1:30–2:30: what's on the wire

**Run:**

```powershell
dotnet run --project src/Mcp2Dialog.Agent --no-build -- quote --auto --wire
```

It answers Oslo / 42 / overnight / yes by itself, in about a second. Scroll back up to the first `tools/call`.

**Point at:**

- The first request is `server/discover`, not `initialize`. Every request carries `MCP-Protocol-Version: 2026-07-28`, `Mcp-Method`, and (on `tools/call`) `Mcp-Name: get_shipping_quote`. There's no `Mcp-Session-Id` header anywhere.
- The yellow response header `← 200 tools/call resultType: input_required`. Inside it, `inputRequests` is keyed by name (`destination`, `weight`), and each one is an ordinary `elicitation/create` with a JSON schema.
- **`requestState: "CfDJ8… (368 chars, encrypted by the server)"`**. "That's the whole dialog so far, encrypted and time-limited by the server. I can't read it or change it. I just send it back."
- The blue `→ tools/call (retry with inputResponses + requestState)`: the same call again, with `inputResponses` (`"action": "accept"`, with the content) and the echoed `requestState`.
- The last response is `resultType: complete`.

### 2:30–3:00: it only asks for what's missing

**Run:**

```powershell
dotnet run --project src/Mcp2Dialog.Agent --no-build -- quote -d Rome -w 3 -s standard
```

One round, no questions: `$13.25 … completed in 1 round(s)`, and the summary shows `input_required results 0`.

```powershell
dotnet run --project src/Mcp2Dialog.Agent --no-build -- quote -d Lisbon -w 500 -s express --auto
```

`500 kg isn't something we can ship. Enter a weight between 0.1 and 70 kg.` The server re-asks just that one question, and `--auto` answers 42: `$146.70 … completed in 2 round(s)`.

(Interactively, the agent's prompt already rejects 500, because the schema says `maximum: 70`. That's why this beat passes 500 on the command line.)

### 3:00–3:30: one trace for the whole dialog

**Show:** dashboard → **Traces** → the latest `mcp2dialog-agent…` trace (or paste the trace id printed in the agent's summary).

**Point at:** `agent get_shipping_quote` contains every round: the agent's `tools/call`, the server's `dialog round N` span (tagged with `input_keys` and `outcome`), and the agent's `elicitation` spans in between. All of it is tagged `mcp2dialog.dialog.id`.

Optionally switch to the server's **Console** tab:

```
Dialog 01a17dbd… round 1: new request (protocol 2026-07-28, MRTR supported: True)
Dialog 01a17dbd… round 1: returning input_required for ["destination", "weight"]
Dialog 01a17dbd… round 2: retry with requestState (protocol 2026-07-28, MRTR supported: True)
Dialog 01a17dbd…: input 'destination' came back as accept {"destination": "\"Oslo\""}
…
Dialog 01a17dbd… finished as completed after 4 round(s) in 00:00:00.0458552
```

**Close:** "Each round is a complete, independent request. The server kept nothing between them, so any instance could have answered any round. That's the scale-out demo."

## Q&A extras

### "What if the user says no?"

Run `quote` interactively and press **Enter** on an empty answer. A menu offers **Back to the question**, **Decline to answer**, or **Cancel**. On the menus (speed, surcharge), Decline and Cancel are at the bottom. Either one ends the dialog: `Quote cancelled: the user chose to decline the 'destination' question (dialog …, round 2).` The round is the retry that carried the refusal. This message comes from the code and hasn't been run interactively.

"No" to the surcharge is *not* a refusal: it's an answer, and it switches to express.

### "What if someone tampers with `requestState`?" (about 30 s, raw HTTP)

```powershell
$meta = @{ 'io.modelcontextprotocol/protocolVersion' = '2026-07-28'
           'io.modelcontextprotocol/clientInfo' = @{ name = 'pwsh'; version = '1' }
           'io.modelcontextprotocol/clientCapabilities' = @{ elicitation = @{ form = @{} } } }
$body = @{ jsonrpc = '2.0'; id = 1; method = 'tools/call'
           params = @{ name = 'get_shipping_quote'; arguments = @{}; _meta = $meta; requestState = 'CfDJ8-not-a-real-state' } } | ConvertTo-Json -Depth 10
(Invoke-WebRequest http://localhost:5199/mcp -Method Post -ContentType 'application/json' -Body $body -UseBasicParsing -Headers @{
  'MCP-Protocol-Version' = '2026-07-28'; 'Mcp-Method' = 'tools/call'; 'Mcp-Name' = 'get_shipping_quote'
  Accept = 'application/json, text/event-stream' }).Content
```

The result has `"isError": true` and the text `The dialog state is invalid or has expired. Start a new quote.` The server console shows a red `"get_shipping_quote" threw an unhandled exception` with the stack trace. That's expected. The same happens if more than 10 minutes pass between two rounds, because the protector is time-limited.

### "What about a client that can't do elicitation?"

Run the same request with `'io.modelcontextprotocol/clientCapabilities' = @{}`, and without the `requestState` entry (otherwise you get the tampered-state error again). The server doesn't ask. It completes in one round with guidance: `More information is needed (destination, weight). Call again with destination, weightKg, and speed, or use a client that supports MCP 2026-07-28 multi round-trip requests with elicitation.`

### Run it from the dashboard instead

Press **Start** (▶) on `mcp2dialog-agent` in Resources. It runs `quote --auto --wire` once and exits. Its console log shows the same wire panels.

## Reset between rehearsals

There's nothing to reset: the server keeps no dialog state. To start clean, stop the AppHost with Ctrl+C in its terminal and start it again. The dashboard's traces and logs are cleared when the AppHost stops.

## If it goes wrong

| What you see | What to say or do |
|---|---|
| `The dialog state is invalid or has expired` in the interactive beat | More than 10 minutes passed at a prompt. "It's time-limited, by design." Run `quote` again. |
| `Quote cancelled: the user chose to …` | A blank answer, followed by Decline or Cancel. Run it again. |
| Agent can't connect to `localhost:5199` | The AppHost isn't running, or isn't up yet. Check Resources. |
| Port already in use at startup | Something else holds 18888, 4317, or 5199. Find it with `Get-NetTCPConnection -LocalPort 5199 -State Listen` and stop it. The other two demos use different ports, so it isn't them. |
| AppHost fails with `DNX could not restore or run aspire.cli` | The one-time `aspire.cli` download didn't finish in time. Run the AppHost again, or use the no-Aspire fallback. |
| Aspire won't start | Use the no-Aspire fallback below. |

### No-Aspire fallback (two terminals)

```powershell
dotnet run --project src/Mcp2Dialog.Server -- --Otlp:Enabled=false
dotnet run --project src/Mcp2Dialog.Agent -- quote --no-otlp
```

There's no dashboard, so skip the trace beat. The server's console shows the same `Dialog … round N` lines.

## Not verified

These weren't checked in development, so check them during rehearsal:

- The interactive prompts: typing the answers, the speed and surcharge menus, and the blank-answer menu with Decline and Cancel. The harness can't drive Spectre's prompts, so the 0:30–1:30 beat is based on the code and the `--auto` runs. The questions, prices, and final quote match the `--auto` run.
- The dashboard clicks: the Traces view and the Start button on `mcp2dialog-agent`.
- Readability at 1080p on a projector.
