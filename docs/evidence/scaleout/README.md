# Scale-out demo evidence

Real output, captured on the dev laptop (RockyMini, Windows 11 build 26220, .NET SDK 10.0.401) on 2026-10-14. Nothing here was written by hand. Each `.txt` file starts with a header that gives the capture time, the commit, and the exact commands, followed by the agent's console, the gateway log, and both server logs.

The runs used the no-Aspire setup (launch profiles, OTLP off) so all four logs could be captured into one file. The exception is run 7, which used the AppHost. Output is 80 columns wide and has no colour, because it was redirected to a file. The `GET - → server-b 405` line near the top of each gateway log is the capture script checking the gateway is up before the agent starts.

| File | Acceptance criterion | What it shows |
|---|---|---|
| `…-run2-round-robin-wire.txt` | 2 | `quote --auto --wire --scaleout` through the gateway: 4 rounds, sequence `A → B → A → B → A → B`, `initialize sent: never`, `Mcp-Session-Id: none sent, none received`, `MCP-Protocol-Version 2026-07-28 on 6 of 6`, and `Mcp-Session-Id: none` in every request panel |
| `…-run3-server-validates-mcp-headers.txt` | 3 | curl straight to server-a: matching headers get `200`. A lying `Mcp-Name`, a lying `Mcp-Method`, or missing headers get `400` with `-32020`. This is why a gateway can trust the headers. |
| `…-run4-rate-limit-on.txt` | 4 | `Gateway:RateLimit:Enabled=true`: rounds 1–3 pass, and round 4 gets a `429` from the gateway (partitioned on `Mcp-Name`). Discover and list aren't affected. |
| `…-run5-kill-after-round-1.txt` | 5 | `--kill-after-round 1`: server-a is killed after round 1. Round 3 is refused by A and retried on B (2030 ms), the dialog completes on B, and the client saw no error. |
| `…-run5b-kill-without-gateway-retry.txt` | 5 (contrast) | The same kill with `Gateway:RetryOnConnectFailure=false`: round 3 fails with `502`. This is what plain YARP does. |
| `…-run6-isolated-keys-on-server-b.txt` | 6 | `Instance:IsolatedKeys=true` on server-b: round 2 lands on B, which logs `The key {…} was not found in the key ring`. The agent gets "The dialog state is invalid or has expired. Start a new quote." |
| `…-run6-isolated-keys-off.txt` | 6 | The same setup with the switch off: all 4 rounds complete across both instances |
| `…-run7-aspire-trace-both-instances.txt` / `.json` | 7 | AppHost `scaleout` profile. One dialog's trace read back from the Aspire dashboard (`aspire otel spans`): 37 spans under the single `agent get_shipping_quote` root, from the agent, the gateway, server-a and server-b, with 12 spans tagged `mcp2dialog.instance` (`server-a`, `server-b`) |
