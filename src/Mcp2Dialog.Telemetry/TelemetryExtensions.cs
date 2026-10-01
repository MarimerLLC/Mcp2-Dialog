using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
    public static IHostApplicationBuilder AddDialogTelemetry(
        this IHostApplicationBuilder builder,
        string serviceName,
        LoggingLevelSwitch? consoleLevel = null,
        Action<TracerProviderBuilder>? configureTracing = null,
        Action<MeterProviderBuilder>? configureMetrics = null)
    {
        var otlpEnabled = builder.Configuration.GetValue("Otlp:Enabled", true);
        var otlpEndpoint = builder.Configuration["Otlp:Endpoint"] ?? "http://localhost:4317";

        // Serilog's OTLP sink and the OpenTelemetry SDK each describe the resource. Give them the same
        // service.instance.id so a dashboard shows this process's logs and traces as one resource.
        var serviceInstanceId = Guid.NewGuid().ToString();

        builder.Services.AddSerilog((_, logger) =>
        {
            logger
                .ReadFrom.Configuration(builder.Configuration)
                .Enrich.FromLogContext()
                .WriteTo.Console(outputTemplate: ConsoleTemplate, levelSwitch: consoleLevel);

            if (otlpEnabled)
            {
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
                    tracing.AddOtlpExporter(options => options.Endpoint = new Uri(otlpEndpoint));
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
                    metrics.AddOtlpExporter(options => options.Endpoint = new Uri(otlpEndpoint));
                }
            });

        return builder;
    }
}
