# Runbook: Tasks extension demo (`book_freight`)

Techorama NL, 28 October 2026, "Model Context Protocol (MCP) version 2". About 3 minutes live, with a recorded fallback.

What the audience sees: a slow `book_freight` call comes back as a **task handle**. The agent polls `tasks/get`. You
**kill the agent** part-way through, start it again with `--resume`, and it carries on from the same task id
**without a second `tools/call`**. The server asks one question mid-flight, the agent answers with `tasks/update`,
and the task completes. The Aspire dashboard shows the whole thing, both agent runs included, as **one trace**.

All commands run from the repository root in PowerShell.

## Before you go on stage

1. Windows Terminal with **two tabs** (or a split): **A** for the AppHost, **B** for the agent. Use a font size of
   about 20 or larger, and make the window at least 110 columns wide so the panels don't wrap.
2. Build once so nothing compiles on stage:

   ```powershell
   dotnet build Mcp2Dialog.slnx
   ```

3. Reset any state from a rehearsal:

   ```powershell
   Remove-Item -Recurse -Force .demo, src/Mcp2Dialog.Server/.demo, src/Mcp2Dialog.Agent/.demo -ErrorAction SilentlyContinue
   ```

4. Tab A: start the AppHost (server on http://localhost:5199/mcp, dashboard on http://localhost:18888):

   ```powershell
   dotnet run --project src/Mcp2Dialog.AppHost
   ```

   Open the dashboard in the browser on the **Traces** page. Nothing else may be using ports 5199, 18888 or 4317.
   During development, a second checkout of this repo on the same machine took those ports and broke the AppHost.
5. Tab B: warm up once with `dotnet run --project src/Mcp2Dialog.Agent -- tools` (it should list `book_freight`), then
   `cls`.

## The 3 minutes

| Time | Do | Point at / say |
|---|---|---|
| 0:00 | Tab B: `dotnet run --project src/Mcp2Dialog.Agent -- book --wire` | "One tool call. The tool takes about 40 seconds." |
| 0:10 | Scroll to the blue **→ tools/call** panel | `_meta` → `io.modelcontextprotocol/clientCapabilities` → `extensions` → `io.modelcontextprotocol/tasks`. "The client opts in on *this request*. There's no per-call flag; the server decides." |
| 0:20 | The magenta **← tools/call resultType: task** panel and the "Server returned a task handle" box | `taskId`, `status: working`, `ttlMs`, `pollIntervalMs`. "Not a result: a handle. And it's already saved to disk." |
| 0:30 | The `tasks/get` polls | `Mcp-Name` is the task id (so a load balancer can route it). `statusMessage` changes: *Validating…*, *Reserving carrier capacity…*. Unchanged polls collapse to a grey `↔` line. |
| ~0:40 | When it says **Reserving carrier capacity (2/5)**: press **Ctrl+C** | "I've just killed the client. The server doesn't care; the work carries on." The agent prints *Detached from task … Re-attach with book --resume*. |
| 0:50 | `dotnet run --project src/Mcp2Dialog.Agent -- book --resume --wire` | Rule: *Resuming task … (no new tools/call)*. The first poll shows the task has **moved on** while we were gone. |
| ~1:10 | The yellow **status: input_required** panel | `inputRequests` holds an elicitation: "the carrier moved the pickup window to 18:00-20:00". "The server asks a question *inside the task*." |
| 1:20 | Answer **Yes** | The blue **→ tasks/update** panel: `inputResponses` keyed by the same id. The ack is an empty result. |
| 1:30 | Polls: *Assigning pickup slot*, *Issuing waybill* | "Back to working." |
| ~2:00 | Green **status: completed** and the *Task completed* panel | `result` inside `tasks/get` is exactly what `tools/call` would have returned. |
| 2:10 | The summary table | **tools/call requests: 1 (across 2 agent runs)**. Status transitions. Copy the **trace id**. |
| 2:20 | Dashboard → Traces → paste the trace id | One trace: `agent book_freight` (the killed run) → `tools/call` → server `task created`, `freight stage 1…5`, `task input_required`… and `agent book_freight (resumed)` with `mcp2dialog.task.resumed = true`. Click a `task …` span to show the `mcp2dialog.task.id` tag. |
| 3:00 | Done | |

The timings assume normal speed: the work takes about 40 s, the question comes about 20 s after the call, and the
server suggests a poll every 2 s. If you're slower to resume, the question is simply waiting when you get back. That
is the point: `input_required` holds until someone answers.

### Other ways to "kill" the agent

- **Ctrl+C** (above): the agent detaches cleanly and exits with code 130. Verified with a real console Ctrl+C event in
  `--wire` mode. Rehearse it on the stage laptop anyway (see *Not verified* below).
- **Hard kill**, the `kill -9` equivalent, from another tab:
  `Get-Process mcp2dialog-agent | Stop-Process -Force`.
  Nothing runs on the way out. The state file was written before the first poll, so `--resume` still works. This is
  the one the evidence transcripts use.

### Optional extras (only if there's time)

- **Without the extension:** `dotnet run --project src/Mcp2Dialog.Agent -- book --no-tasks --wire` (hidden flag). The
  request has no `extensions`, so the server must not return a task. It returns ordinary guidance text instead.
- **Cancel:** start `book`, Ctrl+C, then `book --cancel`. The `tasks/cancel` ack is empty, and the status is now
  `cancelled` with a reason.
- **Restart the server too:** run the server on its own with the file store
  (`dotnet run --project src/Mcp2Dialog.Server -- --Tasks:Store=file`), start `book --wire`, then kill the server
  (`Get-Process Mcp2Dialog.Server | Stop-Process -Force`, which is what was tested; a graceful Ctrl+C wasn't) and
  start it again with the same command. The agent shows *server unreachable, retrying…*
  (it keeps trying for 60 s) and then carries on. The server picks the task up from disk at the stage it was in. This
  was verified with the standalone server. Restarting `mcp2dialog-server` from the dashboard (AppHost started with
  `-- --Tasks:Store=file`) should behave the same but **wasn't tested**, because Aspire's proxy sits in between.
- **Unattended run:** press **Start** on `mcp2dialog-agent-book` in the dashboard's Resources page (`book --auto --wire`).

## Rehearsal (fast) mode

The whole task takes about 10 s and the poll interval is 1 s:

```powershell
dotnet run --project src/Mcp2Dialog.AppHost -- --Freight:Fast=true
# or without Aspire:
dotnet run --project src/Mcp2Dialog.Server -- --fast
```

Fast mode doesn't give you enough time to kill and resume calmly. Use it to check the setup, not to rehearse the
timing.

## Reset

```powershell
Remove-Item -Recurse -Force .demo, src/Mcp2Dialog.Server/.demo, src/Mcp2Dialog.Agent/.demo -ErrorAction SilentlyContinue
```

`.demo/last-task.json` is the agent's saved task. `src/Mcp2Dialog.Server/.demo/server-tasks/` only exists with
`Tasks:Store=file`. The in-memory store is cleared by restarting the server.

## If something goes wrong

| Symptom | Say | Do |
|---|---|---|
| `--resume` says **Task not found** | "That's the server forgetting: it restarted with an in-memory store, or the TTL ran out. The handle is only as durable as the server's store, which is why the spec has `ttlMs`." | `book --wire` again, or switch to the clip. |
| Ctrl+C doesn't stop the agent | "Let's be more brutal." | Other tab: `Get-Process mcp2dialog-agent \| Stop-Process -Force`, then `book --resume --wire`. |
| The question was already answered or never shows | Nothing to say; carry on | `--resume` simply continues; the summary still shows 1 tools/call. |
| `Connection refused` / the agent can't connect | "The server isn't up. Here's one I made earlier." | Check tab A; otherwise play the clip. |
| The wire view scrolls too fast | "The protocol detail is in the clip; here's the story view." | Run without `--wire`: a live status panel (task, status, message, elapsed, polls) instead of panels. |
| The trace is missing in the dashboard | "Telemetry is batched; give it a second." | Refresh Traces; search for `mcp2dialog.task.id`. Or show the screenshot `docs/evidence/tasks/aspire-trace-kill-and-resume.png`. |
| AppHost won't start (port in use) | — | Close whatever holds 5199/18888/4317 and restart, or run the server on its own (`dotnet run --project src/Mcp2Dialog.Server`) and skip the dashboard. |

## Recording the fallback clip

1. Reset, start the AppHost (normal speed), set the terminal font to the stage size, and size the window to 1920×1080.
2. Record the screen with **OBS Studio** (Display Capture, 1920×1080, 30 fps, MP4) or **Xbox Game Bar** (Win+Alt+R,
   records the active window only, so keep the browser and terminal in one window or use OBS).
3. Do the 3 minutes exactly as above, at normal speed, including Ctrl+C and `--resume`, then switch to the dashboard
   and open the trace.
4. Keep a 30-second still of the final summary and the dashboard trace at the end, so you can talk over it.
5. Copy the clip onto the stage laptop **and** a USB stick. Test playback full-screen with no network.

## Not verified

These weren't checked in development, so check them during rehearsal:

- **Ctrl+C during the live status panel** (non-`--wire` mode) and **Ctrl+C while the question prompt is showing.**
  The code path is the same as the verified `--wire` case, but the test harness couldn't confirm it.
- Readability at 1080p on a projector. It was only judged from 80- and 110-column transcripts.
- Restarting the server from the Aspire dashboard with the file store (verified only with the standalone server).
- The AppHost on its default ports (18888/4317/5199). It was verified on alternative ports because another session on
  the development machine was using the defaults.
