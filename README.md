# MCP 2 dialog sample: multi round-trip requests

This sample shows an MCP server saying "I need more information before I can answer." The client supplies the missing details, and the server finishes the job. It uses the **multi round-trip requests (MRTR)** feature of the MCP `2026-07-28` specification, as implemented by the [MCP C# SDK](https://github.com/modelcontextprotocol/csharp-sdk) 2.x.

| Project | What it is |
|---|---|
| `src/Mcp2Dialog.Server` | ASP.NET Core MCP server (stateless Streamable HTTP) with `get_shipping_quote` (MRTR) and `book_freight` ([Tasks](#second-demo-the-tasks-extension-book_freight)) |
| `src/Mcp2Dialog.Agent` | Spectre.Console CLI that connects to the server, calls the tool, and answers its questions |
| `src/Mcp2Dialog.Telemetry` | Shared Serilog and OpenTelemetry setup, plus shared span and meta names |
| `src/Mcp2Dialog.AppHost` | Aspire AppHost that runs the server and the dashboard, with the agent as an on-demand resource |

Everything targets .NET 10 (`global.json` pins SDK 10.0.401 with `latestFeature` roll-forward).

## Branches

This repo has four branches. Each one is a different demo built on the same sample, and each branch's README covers its own demo. You're reading the README for `demo/tasks`.

| Branch | Demo |
|---|---|
| [`main`](https://github.com/MarimerLLC/Mcp2-Dialog/tree/main) | The base MRTR dialog (`get_shipping_quote`) on its own |
| [`demo/scaleout`](https://github.com/MarimerLLC/Mcp2-Dialog/tree/demo/scaleout) | Two server instances behind a YARP round-robin gateway with no sticky sessions, showing a dialog that survives switching instances (and losing one) because all its state is in `requestState` |
| [`demo/tasks`](https://github.com/MarimerLLC/Mcp2-Dialog/tree/demo/tasks) (this branch) | Adds a `book_freight` tool that uses the MCP Tasks extension (`io.modelcontextprotocol/tasks`): the server returns a durable task handle and the client polls for the result. See [Second demo](#second-demo-the-tasks-extension-book_freight) |
| [`demo/mcp-apps`](https://github.com/MarimerLLC/Mcp2-Dialog/tree/demo/mcp-apps) | Replaces the CLI agent with a Blazor web agent that shows the questions as forms and the wire traffic in a panel, and can act as an MCP Apps host (`io.modelcontextprotocol/ui`) for a quote card the server ships |

To switch demos, check out the branch, for example `git switch main`.

Each demo uses its own ports, so all four can run at the same time. To do that, give each branch its own folder with `git worktree add ../Mcp2-Dialog-scaleout demo/scaleout` (and the same for `main`).

| Port | `main` | `demo/scaleout` | `demo/tasks` | `demo/mcp-apps` |
|---|---|---|---|---|
| Aspire dashboard | 18888 | 18898 | 18908 | 18918 |
| Dashboard OTLP (gRPC) | 4317 | 4327 | 4337 | 4347 |
| Aspire resource service | 20144 | 20154 | 20164 | 20174 |
| MCP endpoint (`/mcp`) | 5199 | 5203 (gateway) | 5399 | 5499 |
| Server instances | | 5201 (server-a), 5202 (server-b) | | |
| Web agent | | | | 5498 |
| MCP Apps sandbox proxy | | | | 5497 |

## How the dialog works

```
Agent                                                        Server (stateless)
  │  tools/call get_shipping_quote {}                           │
  │ ──────────────────────────────────────────────────────────► │  round 1: nothing known
  │  ◄── resultType: input_required                             │
  │      inputRequests: { destination: elicit, weight: elicit } │
  │      requestState:  "CfDJ8…" (encrypted DialogState)        │
  │                                                             │
  │  (elicitation handler asks the user twice)                  │
  │                                                             │
  │  tools/call get_shipping_quote {}  + inputResponses         │
  │                                    + requestState (echoed)  │
  │ ──────────────────────────────────────────────────────────► │  round 2: decrypts state, merges answers
  │  ◄── input_required { speed: elicit with prices }           │
  │  ...                                                        │  round 3: surcharge (only if heavy + overnight)
  │  ◄── resultType: complete  "Quote for a 42 kg package…"     │  round 4: done
```

1. The agent calls the tool once (`McpClient.CallToolAsync`). It passes whatever arguments it has, possibly none.
2. When the tool is missing something, it throws `InputRequiredException`. The server returns an `input_required` result instead of a final one. The result has one or more `inputRequests` (elicitations here) and a `requestState` string.
3. The SDK client sees `resultType: "input_required"`. It calls the `ElicitationHandler` for each input request, then **re-sends the same `tools/call`** with `inputResponses` and the echoed `requestState`. This happens inside the original `CallToolAsync`, so the agent code never sees the intermediate rounds.
4. The tool runs again from the top, reads `RequestState` and `InputResponses` off the request parameters, and either asks for more or returns the answer. Each round can ask different questions based on earlier answers: speed choices are priced from the weight and destination, and the surcharge question only appears for heavy overnight packages.

### What about a session or correlation id?

You need one, but in `2026-07-28` it isn't a transport session. That revision removes `Mcp-Session-Id` and the `initialize` handshake from Streamable HTTP (SEP-2567/2575), and SDK 2.x servers are stateless by default. The Connected panel in the agent shows "Session id: none".

**`requestState` is the correlation mechanism.** It's an opaque string the server mints, and the client must echo it back unchanged on the retry. In this sample:

- `DialogState` (the dialog id, round number, start time, and answers so far) is serialized to JSON and protected with ASP.NET Core Data Protection, using a time-limited protector with a 10-minute lifetime (`DialogStateProtector`). The client can't read or forge it, and it expires.
- The server keeps **nothing** in memory between rounds, so any server instance behind a load balancer can handle any round. (If you run multiple instances, they need a shared Data Protection key ring.)
- The dialog id (a v7 GUID) is also put in each elicitation's `_meta` (`mcp2dialog/dialogId`, `mcp2dialog/round`). The agent can show and log it, but the authoritative copy is the one inside `requestState`.
- Both sides tag their spans with `mcp2dialog.dialog.id`. The server adds `DialogId` and `DialogRound` to every log event through a logging scope.

An alternative is to put only a dialog id in `requestState` and keep the state in a store such as Redis or a database. That works too, but then the server is stateful again.

### Fallbacks

- **Client can't do MRTR or elicitation**: the tool checks `McpServer.IsMrtrSupported` and the per-request client capabilities. (Under `2026-07-28` these arrive in each request's `_meta` and are exposed on `context.JsonRpcRequest.Context.ClientCapabilities`. `McpServer.ClientCapabilities` is always null on a stateless server.) If either is missing, the tool returns guidance telling the caller which arguments to pass.
- **Tampered or expired `requestState`**: the tool returns an error asking the caller to start over.
- **User declines or cancels** any question: the dialog ends with a "cancelled" result.
- **Out-of-range answer** (for example, 500 kg): the server asks the same question again with an explanation.

## Running it

### 1. Start the server and dashboard

**Option A: Aspire AppHost (recommended)**

```bash
dotnet run --project src/Mcp2Dialog.AppHost
```

This starts the Aspire Dashboard at http://localhost:18908 and the MCP server at `http://localhost:5399/mcp`. The **Resources** page lists both apps:

- **mcp2dialog-server** runs right away. Its console output, structured logs, traces, and metrics are linked from its row.
- **mcp2dialog-agent** doesn't start automatically. Press **Start** (▶) on its row to run one unattended dialog (`quote --auto --wire`). It exits when the dialog is done, and you can press Start again for another run. Its console log shows the wire traffic, and its trace shows all the rounds.

The AppHost's `http` launch profile puts the dashboard's OTLP endpoint on `http://localhost:4337` and turns off dashboard login and the OTLP API key. That's for a local sample only. It means an agent you run from your own terminal (step 2) also reports to this dashboard, and appears under Traces and Structured logs as `mcp2dialog-agent-<id>`. The Resources page only lists apps the AppHost started.

Aspire passes the standard `OTEL_*` variables (endpoint, headers, service name, and instance id) to the apps it starts. `TelemetryExtensions` defers to them, so their telemetry is linked to their rows on the Resources page.

**Option B: standalone dashboard container and server**

```bash
docker run -d --rm --name mcp2dialog-dashboard-tasks -p 18908:18888 -p 4337:18889   -e ASPIRE_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS=true   mcr.microsoft.com/dotnet/aspire-dashboard:latest

dotnet run --project src/Mcp2Dialog.Server
```

The standalone dashboard only receives telemetry (Traces, Structured logs, Metrics). Its **Resources page is empty** because no AppHost is reporting to it. It never lists Docker containers either.

Both apps export OTLP/gRPC to `http://localhost:4337` by default. You can change that with `Otlp:Endpoint` in `appsettings.json`. Without any collector, set `Otlp:Enabled` to `false` on the server, or pass `--no-otlp` to the agent.

### 2. Run the agent

```bash
# Interactive: the server asks for everything
dotnet run --project src/Mcp2Dialog.Agent -- quote

# Watch every JSON-RPC message (input_required, inputResponses, requestState)
dotnet run --project src/Mcp2Dialog.Agent -- quote --wire

# Unattended: answers Oslo / 42 kg / overnight / accept surcharge (4 rounds)
dotnet run --project src/Mcp2Dialog.Agent -- quote --auto --wire

# Supply some details up front: the server only asks for what's missing
dotnet run --project src/Mcp2Dialog.Agent -- quote -d Lisbon -w 500 -s express   # 500 kg is re-asked

# Supply everything: completes in one round, no input_required
dotnet run --project src/Mcp2Dialog.Agent -- quote -d Rome -w 3 -s standard

# Other options
dotnet run --project src/Mcp2Dialog.Agent -- tools
dotnet run --project src/Mcp2Dialog.Agent -- --help
```

The agent finishes with a summary showing the number of `tools/call` HTTP requests, `input_required` results, questions answered, and the **trace id**. Paste the trace id into the dashboard's Traces page.

## What to look at

**Console (agent, `--wire`):** blue panels are requests and yellow panels are `input_required` responses. Request headers include `MCP-Protocol-Version: 2026-07-28`, `Mcp-Method`, and `Mcp-Name`, and there's no `Mcp-Session-Id`. Retries are labeled "retry with inputResponses + requestState".

**Console (server):** one line per round, for example:

```
Dialog 01a0f97d… round 1: new request (protocol 2026-07-28, MRTR supported: True)
Dialog 01a0f97d… round 1: returning input_required for ["destination", "weight"]
Dialog 01a0f97d… round 2: retry with requestState ...
Dialog 01a0f97d…: input 'destination' came back as accept {"destination": "\"Oslo\""}
...
Dialog 01a0f97d… finished as completed after 4 round(s) in 00:00:05.12
```

**Traces (dashboard):** the whole dialog is one trace. The agent's `agent get_shipping_quote` span contains each round's `tools/call` (client), the HTTP `POST`, the server's `tools/call` and `dialog round N` spans, and the agent's `elicitation` spans between rounds. Trace context reaches the server both as the HTTP `traceparent` header and in the JSON-RPC `_meta.traceparent`.

```
agent get_shipping_quote                         [agent]
  tools/call get_shipping_quote                  [agent]
    POST → POST /mcp/                            [agent → server]
    tools/call get_shipping_quote                [server]
      dialog round 1  input_keys=destination,weight outcome=input_required
  elicitation  round=1                           [agent]
  elicitation  round=1                           [agent]
  tools/call get_shipping_quote                  [agent]
    ...
      dialog round 2  input_keys=speed
  ...
      dialog round 4  outcome=completed
```

**Metrics:** `mcp2dialog.dialog.rounds` (by outcome) and `mcp2dialog.dialog.rounds_to_complete`, plus the SDK's `mcp.client.operation.duration` and `mcp.server.operation.duration`.

**More detail:** run the agent with `-v`, or raise `ModelContextProtocol` to `Debug` in `appsettings.json`, to see the SDK's own logs.

## Key code

| File | What to read it for |
|---|---|
| `src/Mcp2Dialog.Server/ShippingTools.cs` | The MRTR tool: correlating with `requestState`, reading `InputResponses`, throwing `InputRequiredException` |
| `src/Mcp2Dialog.Server/DialogStateProtector.cs` | Encrypting and time-limiting `requestState` |
| `src/Mcp2Dialog.Server/Program.cs` | Stateless MCP server (`WithHttpTransport()`; stateless is the 2.x default) |
| `src/Mcp2Dialog.Agent/AgentSession.cs` | `McpClient` with an `ElicitationHandler`, which is all the client code MRTR needs |
| `src/Mcp2Dialog.Agent/ElicitationPrompter.cs` | Turning elicitation schemas into Spectre prompts (or auto answers) |
| `src/Mcp2Dialog.Agent/WireLoggingHandler.cs` | `DelegatingHandler` that shows the raw JSON-RPC traffic |
| `src/Mcp2Dialog.Telemetry/TelemetryExtensions.cs` | Serilog (console + OTLP) and OpenTelemetry (traces + metrics) setup, respecting `OTEL_*` variables from a launcher |
| `src/Mcp2Dialog.AppHost/AppHost.cs` | Aspire resources: the server, plus the agent as an explicit-start resource pointed at the server's endpoint |

## Second demo: the Tasks extension (`book_freight`)

MRTR is for a question *before* a tool can answer. The **Tasks extension** (`io.modelcontextprotocol/tasks`) is for a
tool that takes a while: the server answers `tools/call` with a **task handle** instead of a result, and the client
polls for it. The handle is durable, so the client can go away and come back.

This uses the SDK's own Tasks support (`ModelContextProtocol.Extensions.Tasks` 2.2.0): `WithTasks(store)` on the
server, and `CallToolAsTaskAsync` / `GetTaskAsync` / `UpdateTaskAsync` / `CancelTaskAsync` on the client. The demo adds
three small things the SDK doesn't have yet: `statusMessage` updates from the tool, a way for the tool to know its
own task id, and picking tasks up again after a server restart. See [docs/tasks-sdk-findings.md](docs/tasks-sdk-findings.md).

```
Agent                                                          Server
  │  tools/call book_freight  _meta: clientCapabilities         │
  │                             .extensions["io.modelcontextprotocol/tasks"]
  │ ──────────────────────────────────────────────────────────► │  task created (stored first), work starts in the background
  │  ◄── resultType: "task"  taskId, status: working,           │
  │                          ttlMs, pollIntervalMs              │
  │  (task id saved to .demo/last-task.json)                    │
  │  tasks/get {taskId}            (every pollIntervalMs)       │
  │  ◄── working  "Reserving carrier capacity (2/5)"            │
  │  ✂ agent killed … book --resume (no new tools/call)         │  …still working
  │  tasks/get {taskId}                                         │
  │  ◄── input_required  inputRequests: { <key>: elicitation }  │  ElicitAsync inside the task
  │  tasks/update {taskId, inputResponses: { <key>: accept }}   │
  │  ◄── {} (ack)                                               │
  │  tasks/get … ◄── completed  result: { content: [...] }      │
```

- **Capability:** the client declares the extension on each request (`_meta`). The server decides per request whether
  to make a task, and never returns one to a client that didn't declare it. It advertises the extension in
  `server/discover`.
- **Task store:** `ITaskStore` (the SDK's `IMcpTaskStore` plus `statusMessage` and saved work state). By default it's
  in memory with a TTL (30 minutes). With `--Tasks:Store=file`, tasks go to `.demo/server-tasks/` under the server project and unfinished
  bookings carry on after a server restart.
- **Work:** about 40 seconds in five named stages (`--fast` makes it about 10). About halfway, the carrier "moves the
  pickup window" and the task goes to `input_required` until the client answers. Declining or cancelling ends the
  task as `cancelled`.
- **Without the extension** (`book --no-tasks`), the server returns guidance text instead of a task.
- **Errors:** an unknown or expired task id gets -32602. `tasks/*` without the capability gets -32021.
- **Telemetry:** a span per task event (`task created`, `task progress`, `task input_required`, `task input received`,
  `task completed` …) tagged `mcp2dialog.task.id`, a `TaskId` logging scope, and the metrics
  `mcp2dialog.tasks.created`, `mcp2dialog.tasks.finished` (by status) and `mcp2dialog.tasks.duration`. The agent saves
  the traceparent with the task id, so a resumed run continues **the same trace**.

```bash
# Book: one tools/call, then polls, one question, the result
dotnet run --project src/Mcp2Dialog.Agent -- book --wire

# Kill it (Ctrl+C, or Stop-Process -Force) part-way, then pick the same task up again
dotnet run --project src/Mcp2Dialog.Agent -- book --resume --wire

# Unattended, answers the question itself
dotnet run --project src/Mcp2Dialog.Agent -- book --auto --wire

# Cancel the saved task; resume a specific id; use another state file
dotnet run --project src/Mcp2Dialog.Agent -- book --cancel
dotnet run --project src/Mcp2Dialog.Agent -- book --resume 01a13c...
dotnet run --project src/Mcp2Dialog.Agent -- book --state-file .demo/other.json

# Server switches (standalone, or through the AppHost after --)
dotnet run --project src/Mcp2Dialog.Server -- --fast --Tasks:Store=file
dotnet run --project src/Mcp2Dialog.AppHost -- --Freight:Fast=true --Tasks:Store=file
```

The AppHost also has an explicit-start **mcp2dialog-agent-book** resource (`book --auto --wire`). The stage runbook,
with the narration beats and fallbacks, is [docs/demo-tasks-runbook.md](docs/demo-tasks-runbook.md). Real transcripts
and dashboard screenshots are in [docs/evidence/tasks/](docs/evidence/tasks/).

| File | What to read it for |
|---|---|
| `src/Mcp2Dialog.Server/Program.cs` | `WithTasks(store, …)`, with only `book_freight` allowed to become a task |
| `src/Mcp2Dialog.Server/Freight/FreightTools.cs` | The tool: guidance without the extension, otherwise the background booking |
| `src/Mcp2Dialog.Server/Freight/FreightBooking.cs` | The stages, `statusMessage` updates, and the mid-flight `ElicitAsync` |
| `src/Mcp2Dialog.Server/Tasks/` | `ITaskStore`, the in-memory and file stores, task telemetry, and `CurrentTask` (how the tool learns its task id) |
| `src/Mcp2Dialog.Agent/Commands/BookCommand.cs` | Declaring the extension, handling both result shapes, saving the task id, `--resume` and `--cancel` |
| `src/Mcp2Dialog.Agent/Tasks/TaskWatcher.cs` | The poll loop: `tasks/get` at `pollIntervalMs`, `inputRequests` → `tasks/update` |

## Packages

- `ModelContextProtocol` / `ModelContextProtocol.AspNetCore` / `ModelContextProtocol.Extensions.Tasks` 2.2.0
- `Microsoft.Extensions.Hosting` / `Microsoft.Extensions.Http` 10.0.x
- `Serilog.AspNetCore`, `Serilog.Extensions.Hosting`, `Serilog.Sinks.Console`, `Serilog.Sinks.OpenTelemetry`
- `OpenTelemetry.Extensions.Hosting`, OTLP exporter, ASP.NET Core / HttpClient / runtime instrumentation
- `Spectre.Console`, `Spectre.Console.Cli`, `Spectre.Console.Json`
- `Aspire.AppHost.Sdk` 13.6.0

Versions are managed centrally in `Directory.Packages.props`.
