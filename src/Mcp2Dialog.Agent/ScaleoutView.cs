using System.Diagnostics;
using System.Text.Json.Nodes;
using Mcp2Dialog.Telemetry;
using Spectre.Console;

namespace Mcp2Dialog.Agent;

/// <summary>
/// What --scaleout adds to the console: which instance served each request, the headers that show there's no
/// session, cache hints, and a per-instance summary. Everything shown comes from the real HTTP traffic.
/// </summary>
internal static class ScaleoutView
{
    /// <summary>One colour per instance, so the bouncing is visible from the back of the room.</summary>
    public static Color ColorOf(string? instance) => instance switch
    {
        "server-a" => Color.DeepSkyBlue1,
        "server-b" => Color.HotPink,
        null => Color.Grey,
        _ => Color.Orange1,
    };

    /// <summary>"server-a" becomes "A". The gateway stamps its own rejections (a 429) as "gateway".</summary>
    public static string LetterOf(string? instance) => instance switch
    {
        null => "?",
        "gateway" => "GW",
        _ => instance[(instance.LastIndexOf('-') + 1)..].ToUpperInvariant(),
    };

    public static string Badge(string? instance) =>
        $"[black on {ColorOf(instance).ToMarkup()}] {Markup.Escape(instance ?? "no X-Served-By")} [/]";

    /// <summary>Shown in each request panel: the session header that isn't there.</summary>
    public static string NoSessionLine(bool sessionIdSent) =>
        sessionIdSent ? "[red]Mcp-Session-Id: sent![/]" : "[green]Mcp-Session-Id: none[/]";

    /// <summary>Shown at the top of each response panel: who served it, plus ttlMs/cacheScope on cacheable results.</summary>
    public static List<string> ResponseLines(Hop hop, JsonNode? responseJson)
    {
        var lines = new List<string> { $"[grey]{DialogDiagnostics.Headers.ServedBy}:[/] {Badge(hop.ServedBy)}" };
        if (CacheHint(responseJson) is { Length: > 0 } hint)
        {
            lines.Add($"[grey]caching hint:[/]{hint}");
        }

        return lines;
    }

    /// <summary>One compact line per request when --wire is off.</summary>
    public static void WriteHopLine(Hop hop, JsonNode? responseJson)
    {
        var what = hop.Round is { } round ? $"{hop.Method} round {round}" : hop.Method;
        AnsiConsole.MarkupLine(
            $"  [grey]→[/] {Markup.Escape(what),-18} {Badge(hop.ServedBy)} " +
            $"[grey]{hop.StatusCode} {Markup.Escape(hop.ResultType ?? "")}[/]{CacheHint(responseJson)}");
    }

    public static void WriteSummary(DialogStats stats)
    {
        var hops = stats.Hops;

        var sequence = new Table().Border(TableBorder.Rounded).Title("[bold]Which instance served each request[/]");
        foreach (var hop in hops)
        {
            sequence.AddColumn(new TableColumn(hop.Round is { } round ? $"round {round}" : hop.Method.Replace("server/", "").Replace("tools/", ""))
                .Centered());
        }

        sequence.AddRow(hops.Select(h => new Markup($"[bold black on {ColorOf(h.ServedBy).ToMarkup()}] {LetterOf(h.ServedBy)} [/]")));
        AnsiConsole.Write(sequence);

        var counts = hops.GroupBy(h => h.ServedBy).OrderBy(g => g.Key)
            .Select(g => $"{Badge(g.Key)} [bold]{g.Count()}[/]");
        var arrows = string.Join(" [grey]→[/] ", hops.Select(h => $"[{ColorOf(h.ServedBy).ToMarkup()}]{LetterOf(h.ServedBy)}[/]"));
        var protocols = hops.GroupBy(h => h.ProtocolVersion ?? "(missing)")
            .Select(g => $"{Markup.Escape(g.Key)} on {g.Count()} of {hops.Count}");

        var facts = new Table().HideHeaders().Border(TableBorder.Rounded).AddColumn("").AddColumn("");
        facts.AddRow("requests per instance", string.Join("   ", counts));
        facts.AddRow("sequence", arrows);
        facts.AddRow("initialize sent", Count(hops.Count(h => h.Method == "initialize"), "never"));
        facts.AddRow("Mcp-Session-Id", hops.Any(h => h.SessionIdSent || h.SessionIdReceived)
            ? $"[red]sent {hops.Count(h => h.SessionIdSent)}, received {hops.Count(h => h.SessionIdReceived)}[/]"
            : "[green]none sent, none received[/]");
        facts.AddRow("MCP-Protocol-Version", string.Join(", ", protocols));
        AnsiConsole.Write(facts);
    }

    /// <summary>
    /// Demo helper for --kill-after-round: kills the instance whose process id came back in X-Served-By-Pid.
    /// It's a hard kill, like a crash, so that instance's last few unexported spans and logs are lost.
    /// </summary>
    public static void KillInstance(Hop hop, string? pidHeader)
    {
        if (!int.TryParse(pidHeader, out var pid))
        {
            AnsiConsole.MarkupLine($"[red]Can't kill {Markup.Escape(hop.ServedBy ?? "the instance")}: no X-Served-By-Pid header " +
                                   "(start the server with Instance:ExposePid=true).[/]");
            return;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill();
            process.WaitForExit(TimeSpan.FromSeconds(5));
            AnsiConsole.Write(new Panel(new Markup(
                    $"Killed {Badge(hop.ServedBy)} (pid {pid}) right after it answered round {hop.Round}. " +
                    "The next rounds have to be served by whatever is left.")).Header("[red bold]Instance killed[/]")
                .BorderColor(Color.Red).Expand());
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            AnsiConsole.MarkupLine($"[red]Couldn't kill pid {pid}: {Markup.Escape(ex.Message)}[/]");
        }
    }

    private static string CacheHint(JsonNode? responseJson) =>
        responseJson?["result"]?["ttlMs"] is { } ttl
            ? $" [grey]ttlMs=[/]{ttl.ToJsonString()} [grey]cacheScope=[/]{Markup.Escape(responseJson["result"]?["cacheScope"]?.GetValue<string>() ?? "(none)")}"
            : "";

    private static string Count(int count, string zero) => count == 0 ? $"[green]{zero}[/]" : $"[red]{count}[/]";
}
