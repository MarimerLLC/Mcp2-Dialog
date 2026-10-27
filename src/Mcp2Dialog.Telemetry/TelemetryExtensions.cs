using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Core;
using Serilog.Sinks.OpenTelemetry;

namespace Mcp2Dialog.Telemetry;

public static class TelemetryExtensions
{
    public const string ConsoleTemplate =
        "[{Timestamp:HH:mm:ss.fff} {Level:u3}] {Message:lj} <{SourceContext}> {TraceId}{NewLine}{Exception}";

    /// <summary>
    /// Configures Serilog (console + OTLP) and OpenTelemetry tracing and metrics (OTLP) the same way for both apps.
    /// </summary>
    /// <param name="builder">The host builder.</param>
    /// <param name="serviceName">The OpenTelemetry <c>service.name</c>.</param>
    /// <param name="consoleLevel">An optional switch that controls only the console sink's level.</param>
    /// <param name="configureTracing">Adds app-specific trace instrumentation.</param>
    /// <param name="configureMetrics">Adds app-specific metric instrumentation.</param>
    /// <param name="configureLogging">Adds app-specific Serilog configuration, such as enrichers.</param>
    /// <param name="consoleTemplate">The console sink's output template. Defaults to <see cref="ConsoleTemplate"/>.</param>
    public static IHostApplicationBuilder AddDialogTelemetry(
        this IHostApplicationBuilder builder,
        string serviceName,
        LoggingLevelSwitch? consoleLevel = null,
        Action<TracerProviderBuilder>? configureTracing = null,
        Action<MeterProviderBuilder>? configureMetrics = null,
        Action<LoggerConfiguration>? configureLogging = null,
        string consoleTemplate = ConsoleTemplate)
    {
        var configuration = builder.Configuration;
        var otlpEnabled = configuration.GetValue("Otlp:Enabled", true);

        // When a launcher such as the Aspire AppHost starts the app, it passes the standard OTEL_* variables
        // (endpoint, headers with the dashboard API key, protocol, service name, and instance id). Those win.
        // Otherwise the endpoint comes from Otlp:Endpoint in appsettings.json.
        var launcherProvidesEndpoint = !string.IsNullOrEmpty(configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);
        var otlpEndpoint = configuration["Otlp:Endpoint"] ?? "http://localhost:4327";
        serviceName = configuration["OTEL_SERVICE_NAME"] ?? serviceName;

        // Serilog's OTLP sink and the OpenTelemetry SDK each describe the resource. Give them the same
        // service.instance.id so a dashboard shows this process's logs and traces as one resource.
        var serviceInstanceId = ResourceAttribute(configuration["OTEL_RESOURCE_ATTRIBUTES"], "service.instance.id")
            ?? Guid.NewGuid().ToString();

        builder.Services.AddSerilog((_, logger) =>
        {
            logger
                .ReadFrom.Configuration(builder.Configuration)
                .Enrich.FromLogContext()
                .WriteTo.Console(outputTemplate: consoleTemplate, levelSwitch: consoleLevel);
            configureLogging?.Invoke(logger);

            if (otlpEnabled)
            {
                // Defaults here. Unless ignoreEnvironment is set, the sink then overrides them with any OTEL_* variables.
                logger.WriteTo.OpenTelemetry(options =>
                {
                    options.Endpoint = otlpEndpoint;
                    options.Protocol = OtlpProtocol.Grpc;
                    options.ResourceAttributes = new Dictionary<string, object>
                    {
                        ["service.name"] = serviceName,
                        ["service.instance.id"] = serviceInstanceId,
                    };
                });
            }
        });

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName, serviceInstanceId: serviceInstanceId))
            .WithTracing(tracing =>
            {
                tracing
                    .AddSource(DialogDiagnostics.SourceName, DialogDiagnostics.McpSdkSourceName)
                    .AddHttpClientInstrumentation();
                configureTracing?.Invoke(tracing);
                if (otlpEnabled)
                {
                    tracing.AddOtlpExporter(options => UseConfiguredEndpoint(options, launcherProvidesEndpoint, otlpEndpoint));
                }
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddMeter(DialogDiagnostics.SourceName, DialogDiagnostics.McpSdkSourceName)
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();
                configureMetrics?.Invoke(metrics);
                if (otlpEnabled)
                {
                    metrics.AddOtlpExporter(options => UseConfiguredEndpoint(options, launcherProvidesEndpoint, otlpEndpoint));
                }
            });

        return builder;
    }

    /// <summary>
    /// The exporter reads OTEL_EXPORTER_OTLP_* on its own, but setting an endpoint in code overrides that.
    /// So only set the endpoint when no launcher supplied one.
    /// </summary>
    private static void UseConfiguredEndpoint(OtlpExporterOptions options, bool launcherProvidesEndpoint, string endpoint)
    {
        if (!launcherProvidesEndpoint)
        {
            options.Endpoint = new Uri(endpoint);
            options.Protocol = OtlpExportProtocol.Grpc;
        }
    }

    /// <summary>Reads one key from an OTEL_RESOURCE_ATTRIBUTES value (<c>key1=value1,key2=value2</c>).</summary>
    private static string? ResourceAttribute(string? attributes, string key) =>
        attributes?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(pair => pair.Split('=', 2))
            .Where(parts => parts.Length == 2 && parts[0] == key)
            .Select(parts => parts[1])
            .FirstOrDefault();
}
