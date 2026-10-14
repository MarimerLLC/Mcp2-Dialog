using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// The scale-out demo: two copies of the same stateless server behind a round-robin gateway with no sticky
/// sessions. The agents talk only to the gateway.
/// </summary>
internal static class ScaleOutDemo
{
    public static void Add(IDistributedApplicationBuilder builder)
    {
        // The one real prerequisite: both instances read and write the same Data Protection key ring, so either
        // one can decrypt the requestState the other minted. It's a folder at the repo root (git-ignored).
        var keyRing = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "..", ".keyring"));
        Directory.CreateDirectory(keyRing);

        // --IsolatedKeys server-b starts that instance on a private key ring. Its dashboard command flips it later.
        var isolatedAtStart = builder.Configuration["IsolatedKeys"];

        var serverA = AddServer(builder, "server-a", 5199, keyRing, isolatedAtStart == "server-a");

        // B starts after A, so A has created the shared key before B first reads the key ring.
        var serverB = AddServer(builder, "server-b", 5200, keyRing, isolatedAtStart == "server-b").WaitFor(serverA);

        var gateway = builder.AddProject<Projects.Mcp2Dialog_Gateway>("mcp2dialog-gateway", launchProfileName: null)
            .WithHttpEndpoint(port: 5300, isProxied: false)
            .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
            .WithEnvironment("ReverseProxy__Clusters__mcp-servers__Destinations__server-a__Address", AddressOf(serverA))
            .WithEnvironment("ReverseProxy__Clusters__mcp-servers__Destinations__server-b__Address", AddressOf(serverB))
            .WaitFor(serverA)
            .WaitFor(serverB);

        AddAgent(builder, "mcp2dialog-agent", gateway);
        AddAgent(builder, "mcp2dialog-agent-kill", gateway, "--kill-after-round", "1");
    }

    private static IResourceBuilder<ProjectResource> AddServer(
        IDistributedApplicationBuilder builder, string instance, int port, string keyRing, bool isolatedKeys)
    {
        var isolated = new IsolatedKeysSwitch(isolatedKeys);

        return builder.AddProject<Projects.Mcp2Dialog_Server>($"mcp2dialog-{instance}", launchProfileName: null)
            // Not proxied: Kestrel owns the port, so a killed instance refuses connections (instead of an
            // Aspire proxy accepting them and then failing), which is what the gateway's retry relies on.
            .WithHttpEndpoint(port: port, isProxied: false)
            .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
            .WithEnvironment("Instance__Name", instance)
            .WithEnvironment("Instance__ExposePid", "true")
            .WithEnvironment("DataProtection__KeyRingPath", keyRing)
            .WithEnvironment("Caching__ToolsListTtl", "00:05:00")
            // Evaluated on every (re)start, so the dashboard command below takes effect on the restart it triggers.
            .WithEnvironment(context => context.EnvironmentVariables["Instance__IsolatedKeys"] = isolated.On ? "true" : "false")
            .WithCommand("toggle-isolated-keys", "Toggle isolated keys", async context =>
            {
                isolated.On = !isolated.On;
                var commands = context.Services.GetRequiredService<ResourceCommandService>();
                var restart = await commands.ExecuteCommandAsync(context.ResourceName, KnownResourceCommands.RestartCommand, context.CancellationToken);
                return restart.Success
                    ? CommandResults.Success($"{instance} restarted with {(isolated.On ? "its own private key ring" : "the shared key ring")}.")
                    : CommandResults.Failure($"Restart failed: {restart.Message}");
            }, new CommandOptions { Description = "Restart this instance on a private key ring (or back on the shared one)." });
    }

    private static void AddAgent(
        IDistributedApplicationBuilder builder, string name, IResourceBuilder<ProjectResource> gateway, params string[] extraArgs) =>
        builder.AddProject<Projects.Mcp2Dialog_Agent>(name)
            .WithArgs(context =>
            {
                context.Args.Add("quote");
                context.Args.Add("--auto");
                context.Args.Add("--wire");
                context.Args.Add("--scaleout");
                foreach (var arg in extraArgs)
                {
                    context.Args.Add(arg);
                }

                context.Args.Add("--server");
                context.Args.Add(ReferenceExpression.Create($"{gateway.GetEndpoint("http")}/mcp"));
            })
            .WaitFor(gateway)
            .WithExplicitStart();

    /// <summary>127.0.0.1 rather than localhost, so a refused connection isn't tried twice (IPv6, then IPv4).</summary>
    private static ReferenceExpression AddressOf(IResourceBuilder<ProjectResource> server) =>
        ReferenceExpression.Create($"http://127.0.0.1:{server.GetEndpoint("http").Property(EndpointProperty.Port)}");

    private sealed class IsolatedKeysSwitch(bool on)
    {
        public bool On { get; set; } = on;
    }
}
