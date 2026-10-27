// Host side of the MCP Apps double-iframe sandbox. The agent page creates the outer iframe (the sandbox proxy, on
// its own origin), gives it the app's HTML once the proxy says it's ready, and relays every other JSON-RPC message
// between the app and .NET, where McpAppInstance answers it.

export function mount(container, proxyUrl, html, cspJson, permissionsJson, dotnet) {
  const proxyOrigin = new URL(proxyUrl).origin;
  const iframe = document.createElement("iframe");
  // The spec requires allow-scripts and allow-same-origin on the proxy. Its own origin keeps it away from this page.
  iframe.setAttribute("sandbox", "allow-scripts allow-same-origin allow-forms");
  iframe.title = "MCP App";
  iframe.src = `${proxyUrl}?csp=${encodeURIComponent(cspJson)}`;
  iframe.style.height = "140px";
  container.appendChild(iframe);

  let resourceSent = false;

  async function onMessage(event) {
    // Only the proxy this component created, on the origin it was loaded from.
    if (event.source !== iframe.contentWindow || event.origin !== proxyOrigin) return;
    const msg = event.data;
    if (!msg || msg.jsonrpc !== "2.0") return;

    if (msg.method === "ui/notifications/sandbox-proxy-ready") {
      // Send the app once. A proxy that reloads (and so could have a different CSP) doesn't get it again.
      if (resourceSent) return;
      resourceSent = true;
      iframe.contentWindow.postMessage({
        jsonrpc: "2.0",
        method: "ui/notifications/sandbox-resource-ready",
        params: { html, csp: JSON.parse(cspJson), permissions: JSON.parse(permissionsJson) },
      }, proxyOrigin);
      await dotnet.invokeMethodAsync("OnSandboxReady", proxyOrigin, html.length);
      return;
    }

    if (msg.method === "ui/notifications/size-changed" && msg.params?.height) {
      iframe.style.height = `${Math.min(Math.ceil(msg.params.height), 900)}px`;
      // If the conversation is following its newest items (see js/follow.js), keep it there as the app grows.
      const column = container.closest("[data-follow]");
      if (column?.dataset.follow === "1") column.scrollTop = column.scrollHeight;
    }

    const response = await dotnet.invokeMethodAsync("OnMessage", JSON.stringify(msg));
    if (response) iframe.contentWindow?.postMessage(JSON.parse(response), proxyOrigin);
  }

  window.addEventListener("message", onMessage);

  return {
    post(json) {
      iframe.contentWindow?.postMessage(JSON.parse(json), proxyOrigin);
    },
    unmount() {
      window.removeEventListener("message", onMessage);
      iframe.remove();
    },
  };
}
