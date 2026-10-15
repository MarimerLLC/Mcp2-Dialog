# Tasks extension: what the MCP C# SDK 2.2.0 supports

Step 0 findings for the `book_freight` demo, written before the demo code (checked on 14 October 2026).

**Decision: Path A.** The SDK ships the Tasks extension as its own package,
`ModelContextProtocol.Extensions.Tasks`. Version 2.2.0 matches the `ModelContextProtocol` 2.2.0 pin, so the demo uses
the SDK's Tasks API. It fills three small gaps itself, listed under [What the demo adds](#what-the-demo-adds-on-top-of-the-sdk).

## Sources checked

| Source | What it told us |
|---|---|
| NuGet flat-container index for `ModelContextProtocol.Core` | Latest version is **2.2.0** (2.0.0, 2.1.0, 2.2.0 after the 1.x line). There is no newer stable or preview release, so there's nothing to bump. |
| NuGet search for `ModelContextProtocol tasks` | `ModelContextProtocol.Extensions.Tasks` **2.2.0**. It depends on `ModelContextProtocol` **[2.2.0]** (exact). |
| GitHub releases v1.4.0 … v2.2.0 (`modelcontextprotocol/csharp-sdk`) | v2.0.0-preview.1 implements SEP-2663 (Tasks) and SEP-2133 (extensions). v2.0.0-preview.3 (#1693) moves Tasks into `ModelContextProtocol.Extensions.Tasks`. v2.0.0 (GA, 28 July 2026) ships it, with "complete Tasks extension conformance coverage" (#1741). v2.1.0 and v2.2.0 make no Tasks changes. The experimental 1.3/1.4 Tasks API is gone and isn't wire-compatible. |
| SDK source at tag `v2.2.0`: `src/ModelContextProtocol.Extensions.Tasks/**`, `docs/concepts/tasks/tasks.md`, `samples/TasksExtension` | The details below |
| XML docs of the restored 2.2.0 packages (`ModelContextProtocol.Core.xml`, `ModelContextProtocol.Extensions.Tasks.xml`) | Public surface listed below |
| Spec: modelcontextprotocol.io/extensions/tasks/overview, and `modelcontextprotocol/ext-tasks` `specification/2026-07-28/tasks.md` (commit `899c1bb`, 9 October 2026) | Compared with the list in the brief; see [Spec differences](#spec-text-vs-the-briefs-list) |

## What SDK 2.2.0 provides (and the demo uses)

Server (`McpTasksBuilderExtensions.WithTasks(store, options)`):

- Registers `tasks/get`, `tasks/update` and `tasks/cancel` handlers backed by an `IMcpTaskStore`. There is **no**
  `tasks/list` or `tasks/result`.
- **Advertises `io.modelcontextprotocol/tasks` in `server/discover`** (`ServerCapabilities.Extensions`). Core 2.2.0 has
  `server/discover` (`RequestMethods.ServerDiscover`, `DiscoverResult`). Seen on the wire:
  `"capabilities":{"logging":{},"tools":{},"extensions":{"io.modelcontextprotocol/tasks":{}}}`.
- Decides per call whether a tools/call becomes a task, using `McpTasksOptions.ExecutionModeSelector`
  (`Synchronous` / `Optional` / `Required`). A task is created **only** when the request is `2026-07-28` **and**
  declares the extension in `_meta` → `io.modelcontextprotocol/clientCapabilities` → `extensions`, so the SDK enforces
  "never return a task to a client that didn't declare it".
- Calls `IMcpTaskStore.CreateTaskAsync` **before** it returns `resultType: "task"` (`CreateTaskResult`: `taskId`,
  `status`, `statusMessage`, `createdAt`, `lastUpdatedAt`, `ttlMs`, `pollIntervalMs`). It then runs the tool in the
  background with its own DI scope.
- Inside the task, `McpServer.ElicitAsync` / `SampleAsync` don't send a request. They become an entry in the task's
  `inputRequests` (status `input_required`) and wait for the store's `InputResponseReceived` event, which a
  `tasks/update` raises.
- `tasks/cancel` calls `store.SetCancelledAsync` and cancels the tool's `CancellationToken` (cooperative).
- Maps the tool outcome to `completed` (including `isError: true` results), `failed` (JSON-RPC errors only), or
  `cancelled`.
- Unknown task id on `tasks/get` returns **-32602** `"Unknown task: '<id>'"`. Non-declaring clients calling `tasks/*`
  get **-32021**.
- Sets `Mcp-Name: <taskId>` on `tasks/*` requests (client) through `JsonRpcMessageContext.RoutingName`.

Client (`McpTasksClientExtensions`):

- `CallToolAsTaskAsync` adds the extension to that request's `_meta` and returns `ResultOrCreatedTask<CallToolResult>`,
  so it handles **both result shapes**.
- `GetTaskAsync` returns a typed `WorkingTaskResult`, `InputRequiredTaskResult`, `CompletedTaskResult`,
  `FailedTaskResult` or `CancelledTaskResult`. `UpdateTaskAsync` and `CancelTaskAsync` exist too. All of them declare
  the extension per request and need a `2026-07-28` connection.
- `CallToolWithPollingAsync` does the whole loop. The demo **doesn't** use it, because it has to save the task id
  before the first poll, show each poll, and resume.

Store: `IMcpTaskStore` plus `InMemoryMcpTaskStore` (TTL, compare-and-swap). The docs say the store must be a
singleton under stateless HTTP.

## What the demo adds on top of the SDK

These are small, local, and labelled in the code:

1. **`statusMessage` updates.** `IMcpTaskStore` has no method to change a task's status message, and
   `InMemoryMcpTaskStore` never sets one. The demo's `ITaskStore : IMcpTaskStore` adds `SetStatusMessageAsync`
   (`src/Mcp2Dialog.Server/Tasks/`), and the SDK serves whatever the store returns.
2. **The tool knowing its task id.** The SDK doesn't pass the task id to a task-backed tool. It sets it on an internal
   interceptor server. `CurrentTask` adds a tool-call filter that runs **before** the Tasks filter and puts an empty
   holder in an `AsyncLocal`. The store fills it in when the SDK creates the task, and the background work inherits it.
   This uses `McpRequestFilters.CallToolWithAlternateFilters`, which is `[Experimental("MCPEXP002")]`. The release
   notes say that seam may change.
3. **Server restart with a file-backed store.** The SDK's background runner only exists for tasks created in the
   current process. With `Tasks:Store=file`, `FreightResumeService` picks unfinished bookings up from their saved
   stage after a restart. It asks the mid-flight question through the store, the same way the SDK's interceptor does.
4. **Guidance instead of an error for non-declaring clients.** With `ExecutionModeSelector = Required`, the SDK
   answers -32021. The demo uses `Optional`, and `book_freight` returns a guidance text when it isn't running as a
   task (the same pattern as `get_shipping_quote`).
5. **`-32602` for `tasks/cancel` / `tasks/update` on an unknown or expired id** (spec: SHOULD). The SDK ignores the
   store's `false` from `SetCancelledAsync`, so the demo store throws instead. An expired id on `tasks/get` says
   "has expired" rather than "Unknown task".

## Limits found

- **MRTR inside a task isn't supported.** A tool that throws `InputRequiredException` while running as a task fails
  with "MRTR and tasks cannot be composed via [McpServerTool] yet". So `book_freight` asks with `ElicitAsync`, and
  `get_shipping_quote` is pinned to `Synchronous` with the execution-mode selector, which leaves the MRTR demo unchanged.
- **No server push.** `notifications/tasks` over `subscriptions/listen` isn't implemented ("Known limitations: …
  not yet implemented; clients rely on polling exclusively"). The demo polls, as planned.
- The SDK's notification constant is `TasksProtocol.NotificationTaskStatus = "notifications/tasks/status"`, but the
  spec says `notifications/tasks`. This doesn't matter here, because nothing sends notifications.
- `ServerCapabilities.Extensions` values can't be round-tripped in-process (the SDK docs say so). On the wire it's fine.
- The generic host's console lifetime takes Ctrl+C in the agent (this comes from `Microsoft.Extensions.Hosting`, not
  the SDK). `book` links its loop to `ApplicationStopping` so Ctrl+C detaches cleanly. The existing `quote` command
  was left as it was.

## Spec text vs the brief's list

The brief's list matches the spec. The ext-tasks spec (`specification/2026-07-28/tasks.md`) adds detail the list
doesn't mention:

| Topic | Spec text | Demo |
|---|---|---|
| Task fields | `createdAt` and `lastUpdatedAt` (ISO 8601) are required; `statusMessage` is optional | SDK sends all of them |
| Results of `tasks/*` | `resultType` **MUST** be `"complete"` on `tasks/get`, `tasks/update` and `tasks/cancel` results | SDK does this |
| HTTP routing | Over Streamable HTTP the client **MUST** set `Mcp-Name` to `params.taskId` on `tasks/*` | SDK does this (visible with `--wire`) |
| Error codes | Unknown/expired `taskId` → **-32602** (MUST for `tasks/get`, SHOULD for update/cancel). Non-declaring client calling `tasks/*` → **-32021** | Both, see above |
| Can't serve without a task | If a server can't serve a non-declaring client without a task, it **MUST** return -32021 | The demo *can* serve it (with guidance), so it returns a normal result. This is the guidance behaviour the brief asked for. |
| `inputRequests` keys | Keys **MUST** be unique for the life of the task; clients **SHOULD** de-duplicate across polls | SDK uses a GUID per request; the agent de-duplicates |
| TTL | Servers **MAY** mark a task failed after `createdAt + ttlMs` and delete it later; `ttlMs` may change | Demo deletes it and answers -32602 "has expired" |
| Notifications | Method is `notifications/tasks`; `notifications/progress` and `notifications/message` are not supported on tasks | Not used |

Nothing in the spec contradicts the brief's list.

## Claims not verified

- That no other Tasks implementation for .NET exists outside the official SDK. Only the official packages were checked.
- Multi-instance behaviour (two servers sharing a task store). Not tested; the file store is single-machine.
- How `ModelContextProtocol.Extensions.Tasks` behaves with SDK versions other than 2.2.0 (its dependency is pinned to
  exactly 2.2.0).
- Whether the experimental filter seam (`CallToolWithAlternateFilters`, MCPEXP002) keeps the same shape in later SDK
  versions.
