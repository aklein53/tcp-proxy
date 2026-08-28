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

    private volatile ProxyConfig _config;
    private readonly ILogger _log;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;

    private int _activeConnections;
    private long _totalConnections;
    private long _bytesUp;   // client -> target
    private long _bytesDown; // target -> client

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
        _config.LatencyMs, _config.JitterMs, _config.Enabled,
        Running, LastError,
        Volatile.Read(ref _activeConnections),
        Interlocked.Read(ref _totalConnections),
        Interlocked.Read(ref _bytesUp),
        Interlocked.Read(ref _bytesDown));

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

            // Each pump swallows its own errors but cancels the other direction on failure,
            // so a reset on either side tears the pair down. A clean EOF only half-closes.
            await Task.WhenAll(
                RunPumpAsync(client.Client, target.Client, n => Interlocked.Add(ref _bytesUp, n), linked),
                RunPumpAsync(target.Client, client.Client, n => Interlocked.Add(ref _bytesDown, n), linked));
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

    private async Task RunPumpAsync(Socket source, Socket dest, Action<int> onBytes, CancellationTokenSource linked)
    {
        try
        {
            await PumpAsync(source, dest, onBytes, linked.Token);
        }
        catch
        {
            linked.Cancel();
        }
    }

    /// <summary>
    /// Copies source -> dest, releasing each chunk only after the configured delay.
    /// Reads keep running while chunks wait, so latency is added without capping
    /// throughput (a bounded read + sleep loop would conflate the two).
    /// </summary>
    private async Task PumpAsync(Socket source, Socket dest, Action<int> onBytes, CancellationToken ct)
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
                    var cfg = _config;
                    long delayMs = cfg.LatencyMs;
                    if (cfg.JitterMs > 0)
                        delayMs += Random.Shared.Next(cfg.JitterMs + 1);
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
