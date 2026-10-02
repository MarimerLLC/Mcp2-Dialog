var builder = DistributedApplication.CreateBuilder(args);

// The MCP server. Aspire uses its "http" launch profile, so it's still reachable at http://localhost:5199/mcp
// (through Aspire's proxy). Aspire also passes the OTEL_* variables that point its telemetry at this dashboard.
var server = builder.AddProject<Projects.Mcp2Dialog_Server>("mcp2dialog-server");

// The agent runs one unattended dialog (--auto) and exits. It doesn't start automatically: press Start on its row
// in the Resources page to run another dialog, then look at its console log and trace. For the interactive
// prompts, run the agent from your own terminal instead (see README).
builder.AddProject<Projects.Mcp2Dialog_Agent>("mcp2dialog-agent")
    .WithArgs(context =>
    {
        context.Args.Add("quote");
        context.Args.Add("--auto");
        context.Args.Add("--wire");
        context.Args.Add("--server");
        context.Args.Add(ReferenceExpression.Create($"{server.GetEndpoint("http")}/mcp"));
    })
    .WaitFor(server)
    .WithExplicitStart();

builder.Build().Run();
