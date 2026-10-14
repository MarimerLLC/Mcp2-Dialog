# Runbook: stateless scale-out demo (two instances, no sticky sessions)

About three minutes of stage time. One multi-round dialog bounces between server A and server B behind a plain round-robin gateway. Then one instance is killed in the middle of a dialog, and the dialog still finishes on the other one.

Everything in this runbook was run on the dev laptop (Windows 11, .NET SDK 10.0.401, Aspire 13.6.0). The machine timings are measured. The narration timings are estimates: I haven't rehearsed them out loud.

The dashboard actions (Start, Stop, Toggle isolated keys) were run with the Aspire CLI, which invokes the same resource commands as the buttons: `aspire resource <name> start|stop|toggle-isolated-keys`. I didn't click them in a browser, so check the buttons once before the talk. The CLI is bundled at `%USERPROFILE%\.nuget\packages\aspire.cli.win-x64\13.6.0\tools\net10.0\win-x64\aspire.exe`.

## Ports

| Port | What | Notes |
|---|---|---|
| 18888 | Aspire dashboard | from the AppHost launch profile |
| 4317 | Dashboard OTLP (gRPC) | terminal-launched agents report here |
| 20144 | Aspire resource service | internal |
| 5199 | `mcp2dialog-server-a` | not proxied by Aspire: Kestrel listens directly |
| 5200 | `mcp2dialog-server-b` | not proxied |
| 5300 | `mcp2dialog-gateway` (`/mcp`) | the only address the agents use |

No Docker is needed. If the README's standalone dashboard container (`mcp2dialog-dashboard`) is running, it also binds 18888 and 4317. Stop it first (`docker stop mcp2dialog-dashboard`) so there's only one dashboard on stage.

## Before the talk (5 minutes, not on stage)

From the repo root, on branch `demo/scaleout`:

```powershell
dotnet build Mcp2Dialog.slnx                                         # must say 0 warnings, 0 errors
dotnet run --project src/Mcp2Dialog.AppHost --launch-profile scaleout
```

- The dashboard opens at http://localhost:18888. **Resources** should list `mcp2dialog-server-a`, `mcp2dialog-server-b`, and `mcp2dialog-gateway` as Running, and `mcp2dialog-agent` and `mcp2dialog-agent-kill` as Not started. Startup took about 5 s on the dev laptop.
- In a **second terminal** (this is the one the audience reads), make the font big and the window at least 100 columns wide. The compact view fits in 80 columns.
- Warm everything up with one dialog. This creates the shared key ring in `.keyring/` and JITs all three apps:

  ```powershell
  dotnet run --project src/Mcp2Dialog.Agent --no-build -- quote --auto --scaleout --server http://localhost:5300/mcp
  ```

  It should end with `sequence A → B → A → B → A → B` (or starting with B). It took 1.4 s, including startup.
- In the dashboard, open **Console** for `mcp2dialog-gateway` in a tab, so you can switch to it.
- Optional: start recording the fallback clip now (see the end of this runbook).

## On stage: the 3-minute beats

### 0:00–0:30: "Same server, twice, plain round robin"

**Show:** the dashboard's Resources page.

**Say:** "Two copies of the same MCP server binary. A gateway in front does plain round robin. No sticky sessions and no cookies. 2026-07-28 has no `initialize` and no `Mcp-Session-Id`, so there's nothing to stick to."

### 0:30–1:15: one dialog, both instances

**Run** (second terminal):

```powershell
dotnet run --project src/Mcp2Dialog.Agent --no-build -- quote --auto --scaleout --server http://localhost:5300/mcp
```

**Point at:**

- The coloured badges on each line: `server-a` (blue) and `server-b` (pink), alternating. Rounds 1–4 of the *same dialog* land on different instances.
- `ttlMs=300000 cacheScope=public` on `tools/list`: "the server says the list may be cached for 5 minutes, by anyone". `ttlMs=0 cacheScope=private` on `server/discover` is the SDK default.
- The final table: **`initialize sent: never`**, **`Mcp-Session-Id: none sent, none received`**, **`MCP-Protocol-Version 2026-07-28 on 6 of 6`**. "These are counted from the traffic, not printed as a slogan."
- "Each round carries `requestState`, an encrypted handle the server minted. Either instance can open it because they share one key ring. That's the only prerequisite."

### 1:15–1:45: the gateway only reads headers

**Show:** the `mcp2dialog-gateway` console in the dashboard:

```
server/discover - → server-a 200 in 2 ms
tools/list - → server-b 200 in 3 ms
tools/call get_shipping_quote → server-a 200 in 2 ms
```

**Say:** "One line per request: `Mcp-Method`, `Mcp-Name`, which instance, and the status. All from headers. The gateway never opens the JSON body. That's safe because the server rejects any request whose headers don't match its body, with a 400 and error -32020."

### 1:45–2:30: kill an instance mid-dialog

**Run:**

```powershell
dotnet run --project src/Mcp2Dialog.Agent --no-build -- quote --auto --scaleout --kill-after-round 1 --server http://localhost:5300/mcp
```

Or press **Start** on `mcp2dialog-agent-kill` and show its console.

**Point at:**

- The red **Instance killed** panel: the instance that served round 1 is hard-killed (a crash, not a graceful stop).
- **A two-second pause** on round 3. "Round robin sent round 3 to the dead instance. The connection was refused, so the request was never sent, and the gateway retried it on the survivor. On Windows, finding out takes two seconds. On Linux it's instant."
- The dialog **finishes**, and the sequence shows only the survivor after the kill, for example `A → B → A → B → B → B`.
- Gateway console: `server-a refused the connection, so tools/call get_shipping_quote was never sent. Trying another instance.` and `… → server-b 200 in 2030 ms (retried: server-a refused the connection)`.
- Resources page: `mcp2dialog-server-a` shows **Finished**.

**Bring it back:** press **Start** (▶) on `mcp2dialog-server-a`. The gateway sends it traffic again once its 10-second "unhealthy" period is over.

### 2:30–3:00: one trace, both instances

**Show:** dashboard → Traces → the latest `mcp2dialog-agent…` trace (or paste the trace id printed in the agent's summary).

**Point at:** one trace. The agent's span contains each round's gateway span, with `proxy.destination_id` set to `server-a` or `server-b`, and under it the instance's own spans tagged `mcp2dialog.instance`.

**Close:** "Stateless means any instance, any round. The only thing they share is the key ring that protects `requestState`."

## Q&A extras

### Why does the shared key ring matter? (about 30 s)

1. In the dashboard, open the `…` menu on `mcp2dialog-server-b` and choose **Toggle isolated keys**. server-b restarts on a private, in-memory key ring.
2. Run the plain dialog again. It fails on the first round that changes instance: `The dialog state is invalid or has expired. Start a new quote.` The server-b (or server-a) console says why: `Rejected requestState: The key {…} was not found in the key ring.`
3. Choose **Toggle isolated keys** again to go back to the shared key ring.

Without Aspire, add `--Instance:IsolatedKeys=true` to one server's command line.

### Header-based policy: rate limit per tool (under 30 s)

1. In `src/Mcp2Dialog.Gateway/appsettings.json`, set `Gateway:RateLimit:Enabled` to `true` and save. The gateway reloads it live (observed: about 2 s).
2. Run the plain dialog. Round 4 comes back `429`, served by `gateway` (`GW` in the sequence). The gateway console says `tools/call get_shipping_quote → rejected at the gateway, 429: over 3 calls per 0:00:10 for this Mcp-Name`. `server/discover` and `tools/list` aren't affected.
3. Set it back to `false` and save, or run `git checkout src/Mcp2Dialog.Gateway/appsettings.json`.

**Say:** "Three `tools/call` per tool name per 10 seconds, partitioned on the `Mcp-Name` header. The gateway never looked inside the request."

### "Show me `server/discover`"

```powershell
dotnet run --project src/Mcp2Dialog.Agent --no-build -- quote --wire --scaleout --server http://localhost:5300/mcp
```

Without `--auto`, it stops at the first question, so you can scroll up. The first two panels are `server/discover` and `tools/list`, each with `X-Served-By` and the caching hint. Every request panel shows `MCP-Protocol-Version`, `Mcp-Method`, `Mcp-Name`, and `Mcp-Session-Id: none`.

## Other ways to kill an instance

| How | What it does |
|---|---|
| `--kill-after-round 1` on the agent (used above) | Kills whichever instance served round 1, by the pid it reports in `X-Served-By-Pid`. Use 1 or 2: the retry shows on round N+2. |
| `mcp2dialog-agent-kill` resource | The same, started from the dashboard |
| **Stop** on the instance's row in the dashboard | Aspire stops the process. Its port refuses connections afterwards. |
| `./scripts/kill-instance.ps1 server-a` (or `server-b`, or `-Port 5199`) | Hard-kills whatever *of this sample* listens on that port, from another terminal. macOS/Linux: `./scripts/kill-instance.sh server-a` (untested). |

Bring an instance back with **Start** on its row, or `aspire resource mcp2dialog-server-a start`.

## Reset between rehearsals

1. Stop the AppHost with Ctrl+C in its terminal. This also resets any isolated-keys toggle.
2. Make sure the ports are free: `./scripts/kill-instance.ps1 -Port 5199`, then `-Port 5200` and `-Port 5300`. "Nothing is listening" is the answer you want.
3. `git status` should show `src/Mcp2Dialog.Gateway/appsettings.json` unchanged. If it doesn't, `git checkout` that file.
4. Keep `.keyring/`. Its key lasts 90 days. Delete it only if you want a fresh key, and only while nothing is running.

## If it goes wrong

| What you see | What to say or do |
|---|---|
| Sequence isn't strictly alternating (`A → A …`) | Round robin is per request across *all* clients. If a dashboard agent ran at the same moment, it took a turn. "Any round, any instance." |
| `HTTP error … 502` after the kill | No instance could be reached (both down?), or `Gateway:RetryOnConnectFailure` is `false`. "That's what you get without retry: YARP doesn't retry a POST by itself." Start the instance and rerun. |
| `The dialog state is invalid or has expired` without the Q&A toggle | An instance is still isolated. Toggle it back, or restart the AppHost. |
| `429` without the Q&A step | The rate limit was left on in `appsettings.json`. Set it to `false`. |
| `Can't kill …: no X-Served-By-Pid` | The server isn't running from the scaleout profile. Use the dashboard's Stop button instead. |
| Port already in use at startup | `./scripts/kill-instance.ps1 -Port <port>`. It refuses to kill anything that isn't part of this sample. |
| Aspire won't start | Use the no-Aspire fallback below, or play the recording. |

### No-Aspire fallback (four terminals)

```powershell
dotnet run --project src/Mcp2Dialog.Server --launch-profile server-a -- --Otlp:Enabled=false
dotnet run --project src/Mcp2Dialog.Server --launch-profile server-b -- --Otlp:Enabled=false   # start after server-a
dotnet run --project src/Mcp2Dialog.Gateway -- --Otlp:Enabled=false
dotnet run --project src/Mcp2Dialog.Agent -- quote --auto --scaleout --server http://localhost:5300/mcp --no-otlp
```

This is how the transcripts in `docs/evidence/scaleout/` were made. There's no dashboard: the server and gateway consoles show the same lines.

## Recording the fallback clip

1. Run the "Before the talk" steps. Put the terminal (big font) and the dashboard side by side at 1920×1080.
2. Record with OBS Studio (Display Capture, 1920×1080, 30 fps) or the Xbox Game Bar (Win+Alt+R records the active window only, so OBS is better for two windows).
3. Do the four on-stage beats in order, at speaking pace, with a short pause on each table. Then run the two Q&A extras as a second clip.
4. Check the clip on a projector or TV. The colours to look for are the blue and pink instance badges and the red kill panel.
