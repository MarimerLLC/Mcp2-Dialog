# Evidence: Tasks demo (`book_freight`)

Real output captured on 14 October 2026 on the development machine (RockyMini, Windows 11, .NET SDK 10.0.401), branch
`demo/tasks`. Each transcript starts with the command, the server settings and a timestamp, and ends with the exit
code and wall-clock time. Nothing here was written by hand. Where a step was done by a script (killing a process,
sending Ctrl+C), the transcript has a `# >>>` line saying what was done and when.

The `.txt` transcripts were captured with stdout redirected (not a TTY), so Spectre renders them at 80 columns without
colour, and the live status panel is replaced by one status line per change. The server ran on port 5299 (5297/5298
for the restart and TTL runs) instead of 5199, because another checkout on the same machine was using 5199.

| File | Acceptance criterion | What it shows |
|---|---|---|
| `run1-build-and-quote-unchanged.txt` | 1 | `dotnet build`: 0 warnings, 0 errors. `quote --auto --wire` still takes 4 `tools/call` requests with 3 `input_required`. Its `tools/list` panel now also lists `book_freight`; that's the only visible change. |
| `run2-book-auto-wire.txt` | 2, 6 | Capability in `_meta`, `resultType: "task"`, polls with changing `statusMessage`, `input_required` with the elicitation, `tasks/update`, `completed` with the result. 41 s wall clock at normal speed. |
| `run3-kill-and-resume.txt` | 3, 6 | `book --wire` hard-killed (Stop-Process -Force) during stage 2, the state file it left, then `book --resume --auto --wire` on the same task id: **tools/call requests: 1 (across 2 agent runs)**. |
| `run3b-ctrl-c-detach-and-resume.txt` | 3 | The same with a real Ctrl+C (CTRL_C_EVENT): "Detached from task …", exit code 130, then resume to `completed`, still 1 `tools/call`. |
| `run4-no-tasks-fallback.txt` | 4 | `--no-tasks`: no `extensions` in `_meta`, an ordinary result (`resultType: complete`) with the guidance text, no task. |
| `run5-unknown-and-expired-task.txt` | 5 | An unknown id (-32602 "Unknown task"), no state file, and a task past its TTL (-32602 "has expired"). Each gives a clean message and exit code 2, with no stack trace. The server log shows the expired task's work stopping quietly. |
| `run6-cancel.txt` | (cancel option) | `book --cancel`: `tasks/cancel` ack, status `cancelled` with a reason; a later `--resume` reports it cleanly. |
| `run7-decline-question-and-raw-protocol.txt` | (decline, protocol) | Raw JSON-RPC with curl: `server/discover` advertises `io.modelcontextprotocol/tasks`; declining the question ends the task `cancelled` ("the new pickup window was not accepted"); `tasks/get` without the capability gets -32021; `tasks/cancel` on an unknown id gets -32602. |
| `run8-server-restart-file-store.txt` | (file store) | `Tasks:Store=file`: the **server** is killed mid-task and restarted; the agent shows "server unreachable, retrying…" and continues; the server resumes the task at the stage it was in; 1 `tools/call`. |
| `run9-trace-of-run3.txt` | 7 | The trace of run 3, fetched from an Aspire dashboard's telemetry API: both agent runs (two service instances) and the server in one trace, with `mcp2dialog.task.id` on the task spans and `mcp2dialog.task.resumed=true` on the resumed run. |
| `aspire-trace-kill-and-resume.png` | 7 | Screenshot of a kill-and-resume trace in the AppHost's Aspire dashboard, with the resumed run's span selected. |
| `aspire-trace-apphost-book-resource.png` | 7 | Screenshot of the trace of the `mcp2dialog-agent-book` resource run, with a `task input received` span and its `mcp2dialog.task.id` tag. |
| `aspire-resources-after-book-run.png`, `apphost-agent-book-console.txt` | AppHost | The `mcp2dialog-agent-book` resource started with the dashboard's Start button, and its console log (`completed`, 1 `tools/call`). |

The AppHost screenshots and console log come from a run where the AppHost and server launch-profile ports were
temporarily moved (dashboard 18899, OTLP 4337, server 5399), because another session held the defaults. That change
was reverted and isn't committed.
