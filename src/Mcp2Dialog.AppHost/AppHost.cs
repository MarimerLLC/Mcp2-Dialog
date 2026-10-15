var builder = DistributedApplication.CreateBuilder(args);

// The MCP server. Aspire uses its "http" launch profile, so it's still reachable at http://localhost:5199/mcp
// (through Aspire's proxy). Aspire also passes the OTEL_* variables that point its telemetry at this dashboard.
var server = builder.AddProject<Projects.Mcp2Dialog_Server>("mcp2dialog-server");

// Tasks demo switches, passed through to the server only when given, for example:
//   dotnet run --project src/Mcp2Dialog.AppHost -- --Freight:Fast=true --Tasks:Store=file
foreach (var key in new[] { "Freight:Fast", "Tasks:Store" })
{
    if (builder.Configuration[key] is { } value)
    {
        server.WithEnvironment(key.Replace(":", "__"), value);
    }
}

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

// Tasks extension demo: one unattended book_freight run (--auto) that returns a task handle, polls tasks/get,
// answers the mid-flight question with tasks/update, and prints the result. Also explicit-start. For the
// kill-and-resume part of the demo, run the agent from your own terminal (see docs/demo-tasks-runbook.md).
builder.AddProject<Projects.Mcp2Dialog_Agent>("mcp2dialog-agent-book")
    .WithArgs(context =>
    {
        context.Args.Add("book");
        context.Args.Add("--auto");
        context.Args.Add("--wire");
        context.Args.Add("--server");
        context.Args.Add(ReferenceExpression.Create($"{server.GetEndpoint("http")}/mcp"));
    })
    .WaitFor(server)
    .WithExplicitStart();

builder.Build().Run();
