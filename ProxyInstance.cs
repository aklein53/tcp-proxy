using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

namespace TcpProxy;

/// <summary>
/// One listening proxy. Accepts TCP connections and pipes them to the target,
/// delaying each chunk by the configured latency. Latency, jitter and target
/// can be changed while running; listen port changes require a restart.
/// </summary>
public sealed class ProxyInstance
{
    private const int BufferSize = 64 * 1024;

    /// <summary>Caps the retransmit backoff so a near-100% loss rate can't stall a chunk forever.</summary>
    private const int MaxRetransmits = 8;

    private volatile ProxyConfig _config;
    private readonly ILogger _log;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;

    private int _activeConnections;
    private long _totalConnections;
    private long _bytesUp;   // client -> target
    private long _bytesDown; // target -> client
    private long _idleClosed;
    private long _retransmits;

    /// <summary>Last-activity clock shared by both directions of one connection.</summary>
    private sealed class ConnectionState
    {
        public long LastActivityTicks = Stopwatch.GetTimestamp();

        /// <summary>Set once the flow has been dropped: bytes are read but no longer forwarded.</summary>
        public volatile bool Evicted;

        public void Touch() => Volatile.Write(ref LastActivityTicks, Stopwatch.GetTimestamp());
    }

    public ProxyInstance(ProxyConfig config, ILogger log)
    {
        _config = config;
        _log = log;
    }

    public ProxyConfig Config => _config;
    public bool Running => _listener is not null;
    public string? LastError { get; private set; }

    public void UpdateConfig(ProxyConfig config) => _config = config;

    public ProxyView View() => new(
        _config.Id, _config.Name, _config.ListenPort, _config.TargetHost, _config.TargetPort,
        _config.LatencyMs, _config.JitterMs, _config.LossPercent, _config.IdleTimeoutSeconds,
        _config.IdleAction, _config.Enabled, Running, LastError,
        Volatile.Read(ref _activeConnections),
        Interlocked.Read(ref _totalConnections),
        Interlocked.Read(ref _bytesUp),
        Interlocked.Read(ref _bytesDown),
        Interlocked.Read(ref _idleClosed),
        Interlocked.Read(ref _retransmits));

    /// <summary>Starts listening. Returns false (with LastError set) if the port can't be bound.</summary>
    public bool Start()
    {
        if (_listener is not null)
            return true;
        try
        {
            var listener = new TcpListener(IPAddress.Any, _config.ListenPort);
            listener.Start();
            _listener = listener;
            LastError = null;
            _cts = new CancellationTokenSource();
            _acceptLoop = AcceptLoopAsync(listener, _cts.Token);
            _log.LogInformation("Proxy {Name}: listening on :{Port} -> {Host}:{TargetPort}",
                _config.Name, _config.ListenPort, _config.TargetHost, _config.TargetPort);
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _listener = null;
            _log.LogWarning("Proxy {Name}: failed to listen on :{Port}: {Error}",
                _config.Name, _config.ListenPort, ex.Message);
            return false;
        }
    }

    public async Task StopAsync()
    {
        var cts = _cts;
        var listener = _listener;
        var loop = _acceptLoop;
        _listener = null;
        _cts = null;
        _acceptLoop = null;

        cts?.Cancel();
        listener?.Stop();
        if (loop is not null)
            try { await loop; } catch { /* shutdown */ }
        cts?.Dispose();
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                _ = HandleConnectionAsync(client, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ct.IsCancellationRequested || ex is ObjectDisposedException or SocketException) { }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _log.LogError(ex, "Proxy {Name}: accept loop failed", _config.Name);
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken ct)
    {
        Interlocked.Increment(ref _totalConnections);
        Interlocked.Increment(ref _activeConnections);
        using var _ = client;
        using var target = new TcpClient();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            client.NoDelay = true;
            var cfg = _config;
            await target.ConnectAsync(cfg.TargetHost, cfg.TargetPort, linked.Token);
            target.NoDelay = true;

            var state = new ConnectionState();
            var watchdog = IdleWatchdogAsync(state, linked, client.Client, target.Client);
            try
            {
                // Each pump swallows its own errors but cancels the other direction on failure,
                // so a reset on either side tears the pair down. A clean EOF only half-closes.
                await Task.WhenAll(
                    RunPumpAsync(client.Client, target.Client, state, n => Interlocked.Add(ref _bytesUp, n), linked),
                    RunPumpAsync(target.Client, client.Client, state, n => Interlocked.Add(ref _bytesDown, n), linked));
            }
            finally
            {
                linked.Cancel(); // retire the watchdog
                await watchdog;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogDebug("Proxy {Name}: connection error: {Error}", _config.Name, ex.Message);
        }
        finally
        {
            Interlocked.Decrement(ref _activeConnections);
        }
    }

    private async Task RunPumpAsync(
        Socket source, Socket dest, ConnectionState state, Action<int> onBytes, CancellationTokenSource linked)
    {
        try
        {
            await PumpAsync(source, dest, state, onBytes, linked.Token);
        }
        catch
        {
            linked.Cancel();
        }
    }

    /// <summary>
    /// Cancels the connection once it has been idle in both directions for IdleTimeoutSeconds.
    /// The timeout is re-read each tick, so changing it in the UI applies to live connections.
    /// </summary>
    private async Task IdleWatchdogAsync(
        ConnectionState state, CancellationTokenSource linked, params Socket[] sockets)
    {
        var ct = linked.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int timeoutSec = _config.IdleTimeoutSeconds;
                if (timeoutSec <= 0)
                {
                    await Task.Delay(1000, ct); // disabled for now; it may be enabled later
                    continue;
                }
                long idleMs = (Stopwatch.GetTimestamp() - Volatile.Read(ref state.LastActivityTicks))
                              * 1000 / Stopwatch.Frequency;
                long remainingMs = timeoutSec * 1000L - idleMs;
                if (remainingMs <= 0)
                {
                    var action = _config.IdleAction;
                    Interlocked.Increment(ref _idleClosed);
                    _log.LogDebug("Proxy {Name}: connection idle for {Timeout}s -> {Action}",
                        _config.Name, timeoutSec, action);

                    if (action is IdleAction.ResetOnUse or IdleAction.Blackhole)
                    {
                        // Drop the flow the way a stateful firewall does: stop forwarding, but
                        // leave both sockets open so neither end notices anything yet.
                        state.Evicted = true;
                        if (action == IdleAction.Blackhole)
                            return; // never answer again; the peer hangs until it gives up

                        // Reset only once a peer actually uses the connection, which is when a
                        // firewall sees a packet for a flow it no longer has state for.
                        long evictedAt = Volatile.Read(ref state.LastActivityTicks);
                        while (Volatile.Read(ref state.LastActivityTicks) == evictedAt)
                            await Task.Delay(25, ct);
                    }

                    if (action != IdleAction.Close)
                    {
                        // SO_LINGER with a zero timeout makes close() emit a RST rather than a FIN.
                        // Close here rather than leaving it to the pumps' teardown, so the reset is
                        // what reaches the peer instead of a FIN from the ordinary close path.
                        foreach (var socket in sockets)
                        {
                            try
                            {
                                socket.LingerState = new LingerOption(true, 0);
                                socket.Dispose();
                            }
                            catch (Exception ex)
                            {
                                _log.LogDebug("Proxy {Name}: reset failed: {Error}", _config.Name, ex.Message);
                            }
                        }
                    }
                    await linked.CancelAsync();
                    return;
                }
                await Task.Delay((int)Math.Min(remainingMs, 1000), ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Extra delay modelling packet loss. A dropped segment is not lost data — TCP retransmits
    /// it after a timeout, doubling that timeout for each successive drop — so a "dropped" chunk
    /// is held back rather than discarded, keeping the byte stream intact. Since chunks leave in
    /// order, holding one back also stalls those behind it, reproducing head-of-line blocking.
    /// </summary>
    private long LossDelayMs(ProxyConfig cfg)
    {
        if (cfg.LossPercent <= 0)
            return 0;
        double probability = cfg.LossPercent / 100.0;
        // Linux clamps the retransmission timeout to 200ms (TCP_RTO_MIN); above that it
        // tracks the round trip, which here is twice the configured one-way latency.
        double rtoMs = Math.Max(200, 2.0 * cfg.LatencyMs);
        long delayMs = 0;
        for (int attempt = 0; attempt < MaxRetransmits && Random.Shared.NextDouble() < probability; attempt++)
        {
            delayMs += (long)rtoMs;
            rtoMs *= 2;
            Interlocked.Increment(ref _retransmits);
        }
        return delayMs;
    }

    /// <summary>
    /// Copies source -> dest, releasing each chunk only after the configured delay.
    /// Reads keep running while chunks wait, so latency is added without capping
    /// throughput (a bounded read + sleep loop would conflate the two).
    /// </summary>
    private async Task PumpAsync(
        Socket source, Socket dest, ConnectionState state, Action<int> onBytes, CancellationToken ct)
    {
        var channel = Channel.CreateUnbounded<(byte[] Buf, int Len, long Due)>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

        var readTask = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var buf = ArrayPool<byte>.Shared.Rent(BufferSize);
                    int n = await source.ReceiveAsync(buf, SocketFlags.None, ct);
                    if (n == 0)
                    {
                        ArrayPool<byte>.Shared.Return(buf);
                        break;
                    }
                    state.Touch();
                    if (state.Evicted)
                    {
                        // The flow is gone: swallow the bytes rather than forwarding them.
                        // The watchdog decides whether to reset the peer or stay silent.
                        ArrayPool<byte>.Shared.Return(buf);
                        continue;
                    }
                    var cfg = _config;
                    long delayMs = cfg.LatencyMs;
                    if (cfg.JitterMs > 0)
                        delayMs += Random.Shared.Next(cfg.JitterMs + 1);
                    delayMs += LossDelayMs(cfg);
                    long due = Stopwatch.GetTimestamp() + delayMs * Stopwatch.Frequency / 1000;
                    await channel.Writer.WriteAsync((buf, n, due), ct);
                }
                channel.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                channel.Writer.TryComplete(ex);
            }
        }, ct);

        try
        {
            await foreach (var (buf, len, due) in channel.Reader.ReadAllAsync(ct))
            {
                long remainingTicks = due - Stopwatch.GetTimestamp();
                if (remainingTicks > 0)
                    await Task.Delay(TimeSpan.FromSeconds((double)remainingTicks / Stopwatch.Frequency), ct);
                await dest.SendAsync(buf.AsMemory(0, len), SocketFlags.None, ct);
                onBytes(len);
                ArrayPool<byte>.Shared.Return(buf);
            }
            // Clean EOF from source: propagate the half-close, let the other direction drain.
            try { dest.Shutdown(SocketShutdown.Send); } catch { }
        }
        finally
        {
            await readTask;
            while (channel.Reader.TryRead(out var item))
                ArrayPool<byte>.Shared.Return(item.Buf);
        }
    }
}
