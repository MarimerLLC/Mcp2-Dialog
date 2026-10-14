using System.Diagnostics;
using Mcp2Dialog.Telemetry;

namespace Mcp2Dialog.Server;

/// <summary>
/// Which copy of the server this is, for the scale-out demo (config section <c>Instance</c>).
/// Leave <see cref="Name"/> unset for the original single-server demo: nothing is stamped then.
/// </summary>
public sealed class ServerInstance
{
    public const string Section = "Instance";

    /// <summary>For example <c>server-a</c> or <c>server-b</c>.</summary>
    public string? Name { get; set; }

    /// <summary>
    /// Use a private, in-memory key ring instead of the shared one. requestState minted by the other
    /// instance can't be decrypted here, which shows why the shared key ring matters.
    /// </summary>
    public bool IsolatedKeys { get; set; }

    /// <summary>Demo only: add this process's id to every response so the agent's --kill-after-round can kill it.</summary>
    public bool ExposePid { get; set; }
}

public static class ServerInstanceExtensions
{
    public const string ConsoleTemplate =
        "[{Timestamp:HH:mm:ss.fff} {Level:u3}] {Instance} {Message:lj} <{SourceContext}> {TraceId}{NewLine}{Exception}";

    /// <summary>
    /// Instance stamping: every response says which instance served it (X-Served-By), and the request's span
    /// is tagged with it. Log events get the same name from the Serilog enricher set up in Program.cs.
    /// </summary>
    public static IApplicationBuilder UseInstanceStamp(this IApplicationBuilder app, ServerInstance instance)
    {
        if (instance.Name is not { } name)
        {
            return app;
        }

        var pid = Environment.ProcessId.ToString();
        return app.Use((context, next) =>
        {
            context.Response.Headers[DialogDiagnostics.Headers.ServedBy] = name;
            if (instance.ExposePid)
            {
                context.Response.Headers[DialogDiagnostics.Headers.ServedByPid] = pid;
            }

            Activity.Current?.SetTag(DialogDiagnostics.Tags.Instance, name);
            return next(context);
        });
    }
}
