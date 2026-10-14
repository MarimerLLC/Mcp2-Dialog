using Microsoft.AspNetCore.DataProtection;
using Mcp2Dialog.Server;
using Mcp2Dialog.Server.Freight;
using Mcp2Dialog.Server.Tasks;
using Mcp2Dialog.Telemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using ModelContextProtocol.Extensions.Tasks;
using Serilog;

// --fast is shorthand for Freight:Fast=true (rehearsal speed for the book_freight demo).
var fast = args.Contains("--fast");
var builder = WebApplication.CreateBuilder(args.Where(a => a != "--fast").ToArray());
if (fast)
{
    builder.Configuration["Freight:Fast"] = "true";
}

builder.AddDialogTelemetry(
    "mcp2dialog-server",
    configureTracing: tracing => tracing.AddAspNetCoreInstrumentation(),
    configureMetrics: metrics => metrics.AddAspNetCoreInstrumentation());

// The server keeps no state between rounds. The dialog state goes to the client inside an encrypted,
// time-limited requestState token, and the client sends it back on the retry.
builder.Services.AddDataProtection().SetApplicationName("Mcp2Dialog");
builder.Services.AddSingleton<DialogStateProtector>();
builder.Services.AddSingleton<ShippingRates>();

// Tasks extension (book_freight): one task store for the whole process. Under stateless HTTP every request gets a
// fresh McpServer, so the store must be a singleton (or external storage) for tasks/get to find the task.
var taskStore = CreateTaskStore(builder.Configuration, builder.Environment);
builder.Services.AddSingleton<ITaskStore>(taskStore);
builder.Services.AddSingleton<FreightBooking>();
builder.Services.AddHostedService<FreightResumeService>();

builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new() { Name = "mcp2dialog-server", Version = "1.0.0" };
        CurrentTask.AddFilter(options);
    })
    // Streamable HTTP is stateless by default in SDK 2.x. Clients that negotiate 2026-07-28 get native MRTR,
    // and there is no Mcp-Session-Id: requestState is what correlates the rounds of a dialog.
    .WithHttpTransport()
    .WithTools<ShippingTools>()
    .WithTools<FreightTools>()
    // Tasks: serves tasks/get, tasks/update and tasks/cancel from the store, and advertises io.modelcontextprotocol/tasks
    // in server/discover. Only book_freight may become a task, and only when the client declares the extension on the
    // request. get_shipping_quote stays synchronous: its MRTR dialog can't run inside a task in SDK 2.2.0.
    .WithTasks(taskStore, tasks => tasks.ExecutionModeSelector = request =>
        request.Params?.Name == FreightTools.ToolName ? McpTaskExecutionMode.Optional : McpTaskExecutionMode.Synchronous);

var app = builder.Build();

app.UseSerilogRequestLogging();
app.MapMcp("/mcp");

app.Logger.LogInformation("Task store: {Store}, ttl {Ttl}, poll interval {PollIntervalMs} ms, freight speed {Speed}",
    taskStore is FileTaskStore file ? $"file ({file.DirectoryPath})" : "in-memory",
    taskStore.TimeToLive, taskStore.PollIntervalMs, app.Configuration.GetValue("Freight:Fast", false) ? "fast" : "normal");

app.Run();

// Tasks:Store = memory (default) or file. The file store keeps tasks across a server restart.
static InMemoryTaskStore CreateTaskStore(IConfiguration configuration, IHostEnvironment environment)
{
    var fast = configuration.GetValue("Freight:Fast", false);
    var ttl = TimeSpan.FromSeconds(configuration.GetValue("Tasks:TtlSeconds", 1800));
    var pollIntervalMs = configuration.GetValue("Tasks:PollIntervalMs", fast ? 1000L : 2000L);

    return configuration.GetValue("Tasks:Store", "memory") == "file"
        ? new FileTaskStore(
            Path.Combine(environment.ContentRootPath, configuration.GetValue("Tasks:Directory", ".demo/server-tasks")!),
            ttl, pollIntervalMs)
        : new InMemoryTaskStore(ttl, pollIntervalMs);
}
