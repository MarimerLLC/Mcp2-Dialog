# Scale-out demo: what MCP C# SDK 2.2.0 provides

Step 0 of the scale-out demo: before building anything, find out which of the slide's claims the SDK covers and which ones the demo has to build itself.

**How this was checked:**

- I read the XML API docs shipped in the `ModelContextProtocol`, `ModelContextProtocol.Core`, and `ModelContextProtocol.AspNetCore` 2.2.0 packages.
- I read the release notes for v2.0.0 through v2.2.0 (`github.com/modelcontextprotocol/csharp-sdk/releases`).
- I read the YARP v2.3.0 source (`HttpForwarder`, the load-balancing and health middleware).
- Most importantly, I ran the sample and looked at the traffic.

"Observed" below means I saw it in a run. The transcripts are in [`evidence/scaleout/`](evidence/scaleout/).

## Summary

| Slide claim | Status | Where it comes from |
|---|---|---|
| No `initialize`, no `Mcp-Session-Id` | **SDK-provided, observed** | Default in 2.x: the client sends `server/discover`, and HTTP servers are stateless (`HttpServerTransportOptions.Stateless = true` since 2.0.0) |
| Version and capabilities ride in `_meta` on every request | **SDK-provided, observed** | `io.modelcontextprotocol/protocolVersion`, `clientInfo`, and `clientCapabilities` in each request's `params._meta`, plus the `MCP-Protocol-Version` header |
| `server/discover` is visible in `--wire` | **SDK-provided, observed** | It's the agent's first request, and the original `--wire` view already showed it |
| `Mcp-Method` / `Mcp-Name` headers | **SDK-provided, observed** | The client sends them (SEP-2243, #1553), and the server rejects a request whose headers don't match its body |
| A gateway can route or rate-limit on those headers without parsing the body | **Hand-rolled (gateway), observed** | YARP plus ASP.NET Core rate limiting. The policy and the log line are this sample's code |
| `ttlMs` + `cacheScope` on `tools/list` | **SDK-provided on the wire, observed.** The values are set by this sample | The SDK emits `ttlMs: 0` and `cacheScope: "private"` by default. A list-tools request filter sets `300000` / `public` |
| "Clients stop polling" | **Not provided by the SDK** | The client SDK parses the hints but never caches. Honouring them is the application's or gateway's job, and this demo doesn't implement a cache |
| Cross-call state is a server-minted handle (`requestState`) | **SDK-provided mechanism, observed.** The token format is this sample's | MRTR (`InputRequiredException`, `RequestState`) is in the SDK. The encrypted `DialogState` is the sample's code |
| Two instances, round robin, no stickiness | **Hand-rolled demo topology, observed** | Instance stamping, the shared key ring config, the gateway, the AppHost profile |
| A stopped instance doesn't fail requests | **Hand-rolled (gateway), observed** | YARP has no retry. The demo adds a retry for connection failures only, plus a passive health policy |

## 1. `server/discover`

- **Client:** `McpClient.CreateAsync` with `McpClientOptions.ProtocolVersion = null` (the default) prefers `2026-07-28`. It "probes with `server/discover` and automatically falls back to the `initialize` handshake" if the server doesn't support that revision. The probe is bounded by `McpClientOptions.DiscoverProbeTimeout`, which defaults to 5 s. Pinning `ProtocolVersion = "2026-07-28"` disables the fallback.
- **Server:** the SDK answers `server/discover` on its own, with no application code. Observed result:

  ```json
  { "supportedVersions": ["2026-07-28"], "capabilities": { "logging": {}, "tools": {} },
    "ttlMs": 0, "cacheScope": "private", "resultType": "complete",
    "_meta": { "io.modelcontextprotocol/serverInfo": { "name": "mcp2dialog-server", "version": "1.0.0" } } }
  ```

- **Visible in `--wire`:** yes, and it was before this demo. It's the first panel, and `--scaleout` adds which instance answered it.
- **TTL on discover:** `DiscoverResult` implements `ICacheableResult`. The SDK fills in a safe default (`TimeSpan.Zero`, `Private`) when the app sets nothing. The request-filter builder has `AddListToolsFilter`, `AddListPromptsFilter`, and so on, but **I found no discover filter or option** to set a non-zero discover TTL. The demo leaves discover at `0` / `private`, and the agent shows exactly that.

## 2. `ttlMs` / `cacheScope` (SEP-2549)

- **Wire types:** `ListToolsResult`, `ListPromptsResult`, `ListResourcesResult`, `ListResourceTemplatesResult`, `ReadResourceResult`, and `DiscoverResult` carry `TimeToLive` (serialized as `ttlMs`) and `CacheScope` (`public` / `private`). This came in with #1623 and was renamed in #1644.
- **Server defaults:** for `2026-07-28` the server always emits both fields. Observed: `ttlMs: 0, cacheScope: "private"` on `tools/list` in the original sample.
- **Setting them:** `WithRequestFilters(f => f.AddListToolsFilter(...))` can set `result.TimeToLive` and `result.CacheScope`, and the SDK keeps those values. Observed: `ttlMs: 300000, cacheScope: "public"`. The demo turns this on with `Caching:ToolsListTtl`, which only the scale-out launch profiles set, so the original demo's wire output is unchanged.
- **Client:** the docs say "The SDK does not perform any internal caching of listing results; every call re-fetches all pages from the server." The convenience `ListToolsAsync()` overload doesn't surface the hints. The `ListToolsAsync(ListToolsRequestParams)` overload returns the raw result with them. The client only *warns* (`ValidateCacheableResult`) when a `2026-07-28` server leaves them out.
- **What the demo does:** it shows the hints (`ttlMs=… cacheScope=…`) and nothing more. Suggested slide wording: "`ttlMs` + `cacheScope` tell clients and gateways how long they *may* cache `tools/list`". Saying "clients stop polling" claims behaviour the SDK doesn't have.

## 3. `Mcp-Method` / `Mcp-Name` headers (SEP-2243)

- **Client:** adds `Mcp-Method` to every request, and `Mcp-Name` to named requests such as `tools/call`, but not to `server/discover` or `tools/list`. It also adds `Mcp-Param-*` for tool parameters annotated with `x-mcp-header` (this sample has none).
- **Server validation:** `StreamableHttpHandler.ValidateMcpHeaders` checks the headers against the JSON-RPC body for protocol versions that require them. Observed with curl against server-a ([run3 transcript](evidence/scaleout/)):

  | Request headers | Result |
  |---|---|
  | `Mcp-Method: tools/call`, `Mcp-Name: get_shipping_quote` (matching) | `200`, the quote |
  | `Mcp-Name: delete_everything` (body says `get_shipping_quote`) | `400`, JSON-RPC `-32020` "Header mismatch: Mcp-Name header value 'delete_everything' does not match body value 'get_shipping_quote'." |
  | `Mcp-Method: tools/list` (body says `tools/call`) | `400`, `-32020` "Header mismatch: Mcp-Method …" |
  | no `Mcp-*` headers | `400`, `-32020` "Missing required Mcp-Method header." |

  That validation is why a gateway can act on these headers without reading the body. A client that lies about them gets nothing from the server.

## 4. No session, no handshake

- **Observed in every scale-out run:** 0 `initialize` requests, 0 `Mcp-Session-Id` headers sent or received, and `MCP-Protocol-Version: 2026-07-28` on 6 of 6 requests. The agent counts these from the traffic, so they aren't hard-coded.
- 2.2.0 adds `HttpServerSessionMode` for hybrid stateful/stateless endpoints (#1796). This sample doesn't use it.

## 5. Gateway pieces (YARP 2.3.0)

- **Version:** YARP 2.3.0 (Feb 2025) is the latest stable release. It ships `net6.0`, `net7.0`, and `net8.0` assemblies, and runs on .NET 10 through the `net8.0` build (observed). I used YARP rather than a hand-written proxy because round robin, the absence of affinity, streaming, and trace propagation all come built in.
- **Round robin, no affinity:** `LoadBalancingPolicy: RoundRobin`, with `SessionAffinity.Enabled: false`. The custom proxy pipeline also leaves out `UseSessionAffinity()`. Observed: strict alternation, and no `Set-Cookie`.
- **No retry in YARP:** the forwarder reports `ForwarderError.Request` (502) when it can't reach the destination, and stops there. In YARP's `HttpForwarder.HandleRequestFailureAsync`, the request body is only touched once the connection exists. So when the failure is `HttpRequestException` with `HttpRequestError.ConnectionError`, nothing has been sent and a retry is safe even for a POST. **Hand-rolled:** `RetryOnConnectFailure` retries in exactly that case and in no other. Resets mid-request, timeouts, and 5xx responses are passed through unchanged.
- **Passive health:** YARP's built-in `TransportFailureRate` policy waits for a minimum number of requests in its window (10 by default) before acting, which a 6-request demo never reaches. **Hand-rolled:** the `ConnectFailure` policy marks a refusing instance unhealthy for the cluster's `ReactivationPeriod` (10 s).
- **Kill behaviour (observed, Windows 11):**
  1. Round 1 is served by server-a, and the agent kills it.
  2. Round 2 goes to server-b, because round robin picks B next anyway.
  3. Round 3 goes to server-a and is refused. The retry sends it to server-b, and the client sees a plain `200`. That request took **~2.0–2.05 s**: Windows takes about 2 s to report a refused connection on loopback. Linux and macOS normally refuse immediately, but I haven't measured that.
  4. Round 4 goes straight to server-b, because A is now marked unhealthy.

  **No request failed, and one request was retried by the gateway.** The retried POST reached server-b intact: B's log shows it reading round 3's `speed` answer from that request.

## 6. Other things worth knowing

- **Shared key ring is the prerequisite (observed):** with `Instance:IsolatedKeys=true` on server-b, round 2 lands on B and fails with "The key {…} was not found in the key ring". The agent gets the existing "The dialog state is invalid or has expired. Start a new quote." With it off, all 4 rounds complete.
- **On one Windows machine the default key ring is already shared.** With no `PersistKeysToFileSystem`, ASP.NET Core stores keys per user under `%LOCALAPPDATA%\ASP.NET\DataProtection-Keys`. Two local processes with the same application name therefore already share them, which hides the problem on a laptop and doesn't hold in containers or across hosts. The demo uses an explicit shared folder (`.keyring/`) so the prerequisite is visible, and uses `UseEphemeralDataProtectionProvider()` to break it on purpose.
- **First-start key race (observed once):** when both instances start at the same moment on an empty key ring, each creates its own key. In my test B still decrypted A's token. I didn't establish whether Data Protection reloads the key ring when it meets an unknown key id, or whether B simply loaded after A had written its key. The AppHost starts B after A (`WaitFor`) so this race doesn't happen.
- **Aspire endpoints are not proxied** (`isProxied: false`), so Kestrel owns 5199/5200. With Aspire's proxy in front, a killed instance would sit behind a live listener, and the gateway would see a failed request rather than a refused connection. I didn't test that variant.
- **429 from the gateway:** the SDK client surfaces it as an `HttpRequestException` ("Response status code does not indicate success: 429"), not an `McpException`, and doesn't retry it.

## Claims I couldn't verify

1. **"Clients stop polling."** No SDK client behaviour supports it. See section 2.
2. **Setting a non-zero `ttlMs` on `server/discover`.** I found no SDK API for it. I could have missed one, since I only looked at the documented filter and option surface.
3. **Behaviour with other MCP clients or servers** (Python, TypeScript SDKs) behind this gateway. I only tested the .NET agent and curl.
4. **Connection-refused timing on Linux or macOS**, and `scripts/kill-instance.sh`, which is only syntax-checked because this machine has no `lsof`.
5. **Retry behaviour for a hung instance, or a reset mid-request.** By design these aren't retried, but I didn't test what the client sees.
6. **Why B could decrypt A's token after the simultaneous first start.** See section 6.
7. **Readability on a projector at 1080p.** I only checked the output in 80-column redirected transcripts, not on a real screen.
8. **The Aspire dashboard's visual trace view.** I verified the trace through the dashboard's telemetry API (`aspire otel spans`), which reads the same store, but I didn't look at the page.
