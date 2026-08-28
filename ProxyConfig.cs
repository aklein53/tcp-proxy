namespace TcpProxy;

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
    bool Enabled,
    bool Running,
    string? Error,
    int ActiveConnections,
    long TotalConnections,
    long BytesUp,
    long BytesDown);
