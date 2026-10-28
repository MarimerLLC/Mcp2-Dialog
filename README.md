# MCP 2 dialog sample: multi round-trip requests

This sample shows an MCP server saying "I need more information before I can answer." The client supplies the missing details, and the server finishes the job. It uses the **multi round-trip requests (MRTR)** feature of the MCP `2026-07-28` specification, as implemented by the [MCP C# SDK](https://github.com/modelcontextprotocol/csharp-sdk) 2.x.

| Project | What it is |
|---|---|
| `src/Mcp2Dialog.Server` | ASP.NET Core MCP server (stateless Streamable HTTP) with one tool, `get_shipping_quote` |
| `src/Mcp2Dialog.AgentUi` | Blazor Server (InteractiveServer) agent: calls the tool, shows the server's questions as forms, shows the wire traffic, and can act as an **MCP Apps** host |
| `src/Mcp2Dialog.Agent` | The original Spectre.Console CLI agent. The AppHost no longer runs it, but it still works from a terminal |
| `src/Mcp2Dialog.Telemetry` | Shared Serilog and OpenTelemetry setup, plus shared span and meta names |
| `src/Mcp2Dialog.AppHost` | Aspire AppHost that runs the server, the web agent, and the dashboard |

Everything targets .NET 10 (`global.json` pins SDK 10.0.401 with `latestFeature` roll-forward).

## Branches

This repo has four branches. Each one is a different demo built on the same sample, and each branch's README covers its own demo. You're reading the README for `demo/mcp-apps`.

| Branch | Demo |
|---|---|
| [`main`](https://github.com/MarimerLLC/Mcp2-Dialog/tree/main) | The base MRTR dialog (`get_shipping_quote`) with the CLI agent |
| [`demo/scaleout`](https://github.com/MarimerLLC/Mcp2-Dialog/tree/demo/scaleout) | Two server instances behind a YARP round-robin gateway with no sticky sessions, showing a dialog that survives switching instances (and losing one) because all its state is in `requestState` |
| [`demo/tasks`](https://github.com/MarimerLLC/Mcp2-Dialog/tree/demo/tasks) | Adds a `book_freight` tool that uses the MCP Tasks extension (`io.modelcontextprotocol/tasks`): the server returns a durable task handle and the client polls for the result |
| [`demo/mcp-apps`](https://github.com/MarimerLLC/Mcp2-Dialog/tree/demo/mcp-apps) (this branch) | Replaces the CLI agent with a Blazor web agent that shows the questions as forms and the wire traffic in a panel, and can act as an MCP Apps host (`io.modelcontextprotocol/ui`) for a quote card the server ships. See [MCP Apps](#mcp-apps-optional) |

To switch demos, check out the branch, for example `git switch main`.

Each demo uses its own ports, so all four can run at the same time. To do that, give each branch its own folder with `git worktree add ../Mcp2-Dialog-tasks demo/tasks` (and the same for the others).

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

This starts the Aspire Dashboard at http://localhost:18918 and the MCP server at `http://localhost:5499/mcp`. The **Resources** page lists both apps:

- **mcp2dialog-server** runs right away. Its console output, structured logs, traces, and metrics are linked from its row.
- **mcp2dialog-agent** is the web agent. It starts after the server. Open it with the **Agent UI** link on its row (http://localhost:5498). Its second URL, http://localhost:5497, is the MCP Apps sandbox proxy, so it's listed only in the resource's details.

The AppHost's `http` launch profile puts the dashboard's OTLP endpoint on `http://localhost:4347` and turns off dashboard login and the OTLP API key. That's for a local sample only. It means an agent you run from your own terminal (step 2) also reports to this dashboard, and appears under Traces and Structured logs as `mcp2dialog-agent-<id>`. The Resources page only lists apps the AppHost started.

Aspire passes the standard `OTEL_*` variables (endpoint, headers, service name, and instance id) to the apps it starts. `TelemetryExtensions` defers to them, so their telemetry is linked to their rows on the Resources page.

**Option B: standalone dashboard container and server**

```bash
docker run -d --rm --name mcp2dialog-dashboard-mcp-apps -p 18918:18888 -p 4347:18889   -e ASPIRE_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS=true   mcr.microsoft.com/dotnet/aspire-dashboard:latest

dotnet run --project src/Mcp2Dialog.Server
```

The standalone dashboard only receives telemetry (Traces, Structured logs, Metrics). Its **Resources page is empty** because no AppHost is reporting to it. It never lists Docker containers either.

Both apps export OTLP/gRPC to `http://localhost:4347` by default. You can change that with `Otlp:Endpoint` in `appsettings.json`. Without any collector, set `Otlp:Enabled` to `false` on the server, or pass `--no-otlp` to the agent.

### 2. Use the agent

**Web agent (http://localhost:5498)**

The AppHost starts it. To run it on its own, use `dotnet run --project src/Mcp2Dialog.AgentUi`.

- **Destination / Weight / Speed** are the tool arguments the agent passes up front. Leave them as "ask" and the server asks for them.
- **Get shipping quote** makes one `tools/call`. Each `input_required` round shows up as question cards (all of a round's questions at once, because the SDK resolves them together). **Decline** and **Cancel** send those elicitation actions.
- **Auto-answer** answers Oslo / 42 kg / overnight / accept the surcharge (4 rounds), without typing.
- **MCP Apps UI** declares the `io.modelcontextprotocol/ui` extension and renders tool UIs. See [MCP Apps](#mcp-apps-optional).
- **List tools** shows each tool's `_meta.ui`.
- The **Wire** panel shows every JSON-RPC message: HTTP to the server (blue requests, amber `input_required`, green results) and postMessage to and from MCP Apps (purple).
- Each answer ends with the counts and the **trace id**, which links to the trace in the dashboard.

Each browser tab is its own agent, with its own MCP client and conversation, because the agent is a scoped service per Blazor circuit.

**CLI agent**

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

The stage script for this demo, with the narration beats, Q&A extras, and fallbacks, is in [`docs/demo-mrtr-runbook.md`](docs/demo-mrtr-runbook.md).

## MCP Apps (optional)

[MCP Apps](https://modelcontextprotocol.io/seps/1865-mcp-apps-interactive-user-interfaces-for-mcp) (`io.modelcontextprotocol/ui`) is an MCP extension that lets a server ship an interactive UI for a tool. The UI isn't fragments merged into the agent's page. It's a self-contained HTML app that the host runs in a sandboxed iframe, and the data reaches it through ordinary tool calls. Here the web agent is the host. Turn on **MCP Apps UI** to try it.

```
Server                         Agent (host, :5498)                  Sandbox proxy (:5497)     App (srcdoc iframe)
  │ tools/list: get_shipping_quote  _meta.ui.resourceUri = ui://mcp2dialog/quote-card.html          │
  │ ◄── resources/read ui://…    │                                     │                          │
  │                              │ creates the proxy iframe ──────────►│                          │
  │                              │ ◄──────────── sandbox-proxy-ready ──│                          │
  │                              │ ── sandbox-resource-ready (HTML) ──►│ ── srcdoc ──────────────►│
  │                              │ ◄──────────────────────────────────────────── ui/initialize ───│
  │ ◄── tools/call (MRTR rounds) │ ── tool-input, then tool-result ──────────────────────────────►│
  │ ◄── tools/call (proxied) ◄───│ ◄──────────────────────────────── tools/call (Switch button) ──│
  │                              │ ◄────────────────────────── ui/message, ui/update-model-context │
```

- **Negotiation.** With the option on, the client adds `extensions: { "io.modelcontextprotocol/ui": { mimeTypes: ["text/html;profile=mcp-app"] } }` to its capabilities. Under `2026-07-28` that rides in every request's `_meta`, so the stateless server can check it per call. Its log shows `MCP Apps supported: True`. With the option off, the agent shows the text result, as any client without MCP Apps would.
- **Server side** (`ModelContextProtocol.Extensions.Apps` 2.2.0, experimental, `MCPEXP003`):
  - `[McpAppUi(ResourceUri = ...)]` on `get_shipping_quote` and `.WithMcpApps()` produce the tool's `_meta.ui`.
  - `QuoteCardResource` serves the HTML as a `ui://` resource.
  - The tool now returns a `CallToolResult` with the same text as before plus `structuredContent` for the card. Hosts don't give `structuredContent` to a model.
- **Sandbox.** A web host must load apps through a proxy on a different origin. Here the agent also listens on port 5497, which serves only `/sandbox` and puts the resource's `_meta.ui.csp` in a `Content-Security-Policy` header. With no CSP declared, that's the spec's default: no network access. The app runs in a `srcdoc` iframe without `allow-same-origin`, so it gets an opaque origin and can't touch the agent page, its cookies, or the Blazor circuit.
- **Host side** (`McpAppInstance`):
  - It answers `ui/initialize` with host info, capabilities, and context (theme variables, `toolInfo`).
  - It sends `tool-input` once the app is initialized, then `tool-result` (or `tool-cancelled`).
  - It proxies the app's `tools/call` after checking the tool's `visibility` includes `app`.
  - It handles `ui/message` and `ui/update-model-context`. This agent has no model, so it only shows them.
- **A detail worth showing.** The card's **Switch** buttons call `get_shipping_quote` through the host with all three arguments, so that usually takes one round. Switching a heavy package to overnight still makes the server ask about the surcharge. That question appears in the agent's conversation, not in the app, because elicitation belongs to the host.

## What to look at

**Wire panel (web agent) or console (CLI agent, `--wire`):** blue panels are requests and yellow panels are `input_required` responses. Request headers include `MCP-Protocol-Version: 2026-07-28`, `Mcp-Method`, and `Mcp-Name`, and there's no `Mcp-Session-Id`. Retries are labeled "retry with inputResponses + requestState".

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
| `src/Mcp2Dialog.Server/QuoteCardResource.cs`, `AppUi/quote-card.html` | The MCP App: a `ui://` resource and a self-contained HTML page with a small JSON-RPC client (no SDK) |
| `src/Mcp2Dialog.AgentUi/Agent/AgentSession.cs` | `McpClient` with an `ElicitationHandler` (all the client code MRTR needs), the MCP Apps capability, and loading an app alongside the call |
| `src/Mcp2Dialog.AgentUi/Agent/PendingQuestion.cs`, `Components/Agent/QuestionCard.razor` | Turning an elicitation into a form the handler awaits |
| `src/Mcp2Dialog.AgentUi/Agent/WireLoggingHandler.cs` | `DelegatingHandler` that records the raw JSON-RPC traffic for the wire panel |
| `src/Mcp2Dialog.AgentUi/Apps/McpAppInstance.cs` | The MCP Apps host: `ui/initialize`, tool input and result, proxied `tools/call` |
| `src/Mcp2Dialog.AgentUi/Apps/SandboxProxy.cs`, `sandbox-proxy.html`, `Components/Agent/McpAppFrame.razor(.js)` | The double-iframe sandbox: separate origin, CSP header, message relay |
| `src/Mcp2Dialog.Agent/AgentSession.cs`, `ElicitationPrompter.cs` | The same client code in the CLI agent, with Spectre prompts |
| `src/Mcp2Dialog.Telemetry/TelemetryExtensions.cs` | Serilog (console + OTLP) and OpenTelemetry (traces + metrics) setup, respecting `OTEL_*` variables from a launcher |
| `src/Mcp2Dialog.AppHost/AppHost.cs` | Aspire resources: the server, and the web agent pointed at the server's endpoint and its own sandbox endpoint |

## Packages

- `ModelContextProtocol` / `ModelContextProtocol.AspNetCore` / `ModelContextProtocol.Extensions.Apps` 2.2.0
- ASP.NET Core Blazor (.NET 10, InteractiveServer). The web agent sets `RequiresAspNetWebAssets` so `_framework/blazor.web.js` is restored even when the project is restored without its `.razor` files (as in a container build)
- `Microsoft.Extensions.Hosting` / `Microsoft.Extensions.Http` 10.0.x
- `Serilog.AspNetCore`, `Serilog.Extensions.Hosting`, `Serilog.Sinks.Console`, `Serilog.Sinks.OpenTelemetry`
- `OpenTelemetry.Extensions.Hosting`, OTLP exporter, ASP.NET Core / HttpClient / runtime instrumentation
- `Spectre.Console`, `Spectre.Console.Cli`, `Spectre.Console.Json`
- `Aspire.AppHost.Sdk` 13.6.0

Versions are managed centrally in `Directory.Packages.props`.
