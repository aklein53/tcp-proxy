using System.Text.Json.Serialization;

namespace TcpProxy;

/// <summary>
/// How a connection is torn down when the idle timeout fires. The choice is visible to the
/// application: a FIN surfaces as a clean end-of-file, a RST as a read error. Oracle clients,
/// for instance, report the first as ORA-03113 and the second as ORA-12570.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<IdleAction>))]
public enum IdleAction
{
    /// <summary>Graceful close: the peer's next read returns end-of-file. (Oracle: ORA-03113.)</summary>
    Close,

    /// <summary>
    /// Abortive close immediately (SO_LINGER 0). The peer learns while still idle, so its next
    /// write is what fails, with EPIPE. (Oracle: ORA-12571, packet writer failure.)
    /// </summary>
    Reset,

    /// <summary>
    /// Drop the flow silently, then reset when traffic next arrives — what a stateful firewall
    /// does when it evicts a connection and later sees a packet it has no state for. The peer's
    /// write succeeds and its read fails with ECONNRESET. (Oracle: ORA-12570, packet reader failure.)
    /// </summary>
    ResetOnUse,

    /// <summary>
    /// Drop the flow silently and never respond again, holding both sockets open. The peer hangs
    /// until its own timeout expires. This is the "black hole" firewall.
    /// </summary>
    Blackhole,
}

/// <summary>Persisted configuration for one proxy. Immutable; updates swap the whole record.</summary>
public sealed record ProxyConfig
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "";
    public int ListenPort { get; init; }
    public string TargetHost { get; init; } = "";
    public int TargetPort { get; init; }

    /// <summary>One-way delay added to every chunk, in each direction (like tc netem delay).</summary>
    public int LatencyMs { get; init; }

    /// <summary>Random 0..JitterMs added on top of LatencyMs per chunk. Ordering is preserved.</summary>
    public int JitterMs { get; init; }

    /// <summary>
    /// Percentage (0-100) of chunks treated as a dropped segment: the chunk waits out a
    /// retransmit timeout instead of being discarded, so the byte stream stays intact.
    /// </summary>
    public double LossPercent { get; init; }

    /// <summary>Close connections with no traffic in either direction for this long. 0 disables.</summary>
    public int IdleTimeoutSeconds { get; init; }

    /// <summary>Whether an idle close sends a FIN or a RST.</summary>
    public IdleAction IdleAction { get; init; } = IdleAction.Close;

    public bool Enabled { get; init; } = true;
}

/// <summary>Request body for create/update.</summary>
public sealed record ProxyUpsert(
    string? Name,
    int ListenPort,
    string TargetHost,
    int TargetPort,
    int LatencyMs,
    int JitterMs,
    double LossPercent,
    int IdleTimeoutSeconds,
    IdleAction IdleAction,
    bool Enabled);

/// <summary>API response: config plus live state.</summary>
public sealed record ProxyView(
    Guid Id,
    string Name,
    int ListenPort,
    string TargetHost,
    int TargetPort,
    int LatencyMs,
    int JitterMs,
    double LossPercent,
    int IdleTimeoutSeconds,
    IdleAction IdleAction,
    bool Enabled,
    bool Running,
    string? Error,
    int ActiveConnections,
    long TotalConnections,
    long BytesUp,
    long BytesDown,
    long IdleClosed,
    long Retransmits);
