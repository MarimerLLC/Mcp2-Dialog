var builder = DistributedApplication.CreateBuilder(args);

// The MCP server. Aspire uses its "http" launch profile, so it's still reachable at http://localhost:5199/mcp
// (through Aspire's proxy). Aspire also passes the OTEL_* variables that point its telemetry at this dashboard.
var server = builder.AddProject<Projects.Mcp2Dialog_Server>("mcp2dialog-server");

// The agent: a Blazor Server app at http://localhost:5198. Open it from its row on the Resources page. Its "http"
// launch profile has two URLs: the agent's pages, and the MCP Apps sandbox proxy on its own origin (port 5197).
var agent = builder.AddProject<Projects.Mcp2Dialog_AgentUi>("mcp2dialog-agent");
agent
    .WithEnvironment("Agent__Server", ReferenceExpression.Create($"{server.GetEndpoint("http")}/mcp"))
    .WithEnvironment("Agent__SandboxUrl", agent.GetEndpoint("http2"))
    .WithUrlForEndpoint("http", url => url.DisplayText = "Agent UI")
    .WithUrlForEndpoint("http2", url => url.DisplayLocation = UrlDisplayLocation.DetailsOnly)
    .WaitFor(server);

builder.Build().Run();
