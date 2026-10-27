namespace Mcp2Dialog.AgentUi.Agent;

/// <summary>Which wire a message crossed: HTTP to the MCP server, or postMessage to an MCP App's iframe.</summary>
public enum WireChannel
{
    Http,
    PostMessage,
}

/// <summary>How a message is colored in the wire panel.</summary>
public enum WireKind
{
    Request,
    InputRequired,
    Response,
    Error,
    AppToHost,
    HostToApp,
}

public sealed record WireEntry(
    long Sequence,
    DateTimeOffset At,
    WireChannel Channel,
    WireKind Kind,
    string Title,
    IReadOnlyList<string> Headers,
    string Body);

/// <summary>The messages one agent has sent and received, for the wire panel.</summary>
public sealed class WireLog
{
    private const int MaxEntries = 400;

    private readonly Lock _lock = new();
    private readonly List<WireEntry> _entries = [];
    private long _sequence;

    public event Action? Changed;

    public void Add(WireChannel channel, WireKind kind, string title, IReadOnlyList<string> headers, string body)
    {
        lock (_lock)
        {
            _entries.Add(new WireEntry(++_sequence, DateTimeOffset.Now, channel, kind, title, headers, body));
            if (_entries.Count > MaxEntries)
            {
                _entries.RemoveRange(0, _entries.Count - MaxEntries);
            }
        }

        Changed?.Invoke();
    }

    public IReadOnlyList<WireEntry> Snapshot()
    {
        lock (_lock)
        {
            return [.. _entries];
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
        }

        Changed?.Invoke();
    }
}
