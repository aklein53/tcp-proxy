using System.Globalization;
using System.Text.Json;

namespace TcpProxy;

/// <summary>Owns all proxy instances and persists their configs to a JSON file.</summary>
public sealed class ProxyManager
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly Dictionary<Guid, ProxyInstance> _proxies = new();
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private readonly string _configPath;
    private readonly ILogger<ProxyManager> _log;

    public ProxyManager(ILogger<ProxyManager> log)
    {
        _log = log;
        _configPath = Environment.GetEnvironmentVariable("CONFIG_PATH") ?? "config.json";
    }

    public async Task LoadAndStartAsync()
    {
        if (!File.Exists(_configPath))
        {
            // First boot (no saved config yet): seed from PROXY_* env vars.
            // Once a config file exists, it wins — UI edits survive restarts.
            await SeedFromEnvironmentAsync();
            return;
        }
        List<ProxyConfig>? configs = null;
        try
        {
            await using var stream = File.OpenRead(_configPath);
            configs = await JsonSerializer.DeserializeAsync<List<ProxyConfig>>(stream, JsonOptions);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to load config from {Path}; starting empty", _configPath);
        }
        foreach (var cfg in configs ?? [])
        {
            var instance = new ProxyInstance(cfg, _log);
            _proxies[cfg.Id] = instance;
            if (cfg.Enabled)
                instance.Start();
        }
    }

    /// <summary>
    /// Creates proxies from env vars of the form
    /// PROXY_&lt;NAME&gt;=&lt;listenPort&gt;:&lt;targetHost&gt;:&lt;targetPort&gt;[:tuning...]
    /// e.g. PROXY_POSTGRES=15432:db:5432:100 or PROXY_REDIS=16379:redis:6379:idle=60
    /// </summary>
    private async Task SeedFromEnvironmentAsync()
    {
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is not string key
                || !key.StartsWith("PROXY_", StringComparison.Ordinal)
                || entry.Value is not string value)
                continue;

            var name = key["PROXY_".Length..].ToLowerInvariant().Replace('_', '-');
            if (!TryParseSeed(name, value, out var upsert, out var parseError))
            {
                _log.LogWarning("Ignoring {Key}=\"{Value}\": {Error}", key, value, parseError);
                continue;
            }

            var (_, error) = await CreateAsync(upsert);
            if (error is not null)
                _log.LogWarning("Ignoring {Key}: {Error}", key, error);
            else
                _log.LogInformation("Seeded proxy \"{Name}\" from {Key}", name, key);
        }
    }

    /// <summary>The optional tuning fields, in the order they may be given positionally.</summary>
    private static readonly string[] SeedFields = ["latency", "jitter", "loss", "idle"];

    /// <summary>
    /// Parses a PROXY_* value: listenPort:targetHost:targetPort, then any of the tuning fields
    /// either positionally (latency:jitter:loss:idle) or by name (loss=5:idle=60), so a later
    /// field can be set without padding the earlier ones with zeroes.
    /// </summary>
    private static bool TryParseSeed(string name, string value, out ProxyUpsert upsert, out string error)
    {
        upsert = null!;
        var parts = value.Split(':');
        if (parts.Length < 3)
        {
            error = "expected <listenPort>:<targetHost>:<targetPort>[:latency[:jitter[:loss[:idle]]]]";
            return false;
        }
        if (!int.TryParse(parts[0], out int listenPort) || !int.TryParse(parts[2], out int targetPort))
        {
            error = "listen port and target port must be numbers";
            return false;
        }

        var fields = new Dictionary<string, double>();
        int nextPositional = 0;
        foreach (var part in parts.Skip(3))
        {
            string field, raw;
            int eq = part.IndexOf('=');
            if (eq >= 0)
            {
                field = part[..eq].Trim().ToLowerInvariant();
                raw = part[(eq + 1)..];
            }
            else if (nextPositional < SeedFields.Length)
            {
                field = SeedFields[nextPositional];
                raw = part;
            }
            else
            {
                error = $"too many fields (expected at most {SeedFields.Length} after the target)";
                return false;
            }

            int index = Array.IndexOf(SeedFields, field);
            if (index < 0)
            {
                error = $"unknown field \"{field}\" (expected one of {string.Join(", ", SeedFields)})";
                return false;
            }
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
            {
                error = $"\"{raw}\" is not a number";
                return false;
            }
            fields[field] = parsed;
            nextPositional = index + 1;
        }

        upsert = new ProxyUpsert(
            name, listenPort, parts[1], targetPort,
            (int)fields.GetValueOrDefault("latency"),
            (int)fields.GetValueOrDefault("jitter"),
            fields.GetValueOrDefault("loss"),
            (int)fields.GetValueOrDefault("idle"),
            Enabled: true);
        error = "";
        return true;
    }

    public async Task<List<ProxyView>> ListAsync()
    {
        await _mutex.WaitAsync();
        try
        {
            return _proxies.Values
                .OrderBy(p => p.Config.ListenPort)
                .Select(p => p.View())
                .ToList();
        }
        finally { _mutex.Release(); }
    }

    public async Task<(ProxyView? View, string? Error)> CreateAsync(ProxyUpsert dto)
    {
        await _mutex.WaitAsync();
        try
        {
            if (Validate(dto, exceptId: null) is { } error)
                return (null, error);

            var cfg = new ProxyConfig
            {
                Name = string.IsNullOrWhiteSpace(dto.Name) ? $"{dto.TargetHost}:{dto.TargetPort}" : dto.Name.Trim(),
                ListenPort = dto.ListenPort,
                TargetHost = dto.TargetHost.Trim(),
                TargetPort = dto.TargetPort,
                LatencyMs = dto.LatencyMs,
                JitterMs = dto.JitterMs,
                LossPercent = dto.LossPercent,
                IdleTimeoutSeconds = dto.IdleTimeoutSeconds,
                Enabled = dto.Enabled,
            };
            var instance = new ProxyInstance(cfg, _log);
            if (cfg.Enabled && !instance.Start())
                return (null, $"Cannot listen on port {cfg.ListenPort}: {instance.LastError}");

            _proxies[cfg.Id] = instance;
            await SaveAsync();
            return (instance.View(), null);
        }
        finally { _mutex.Release(); }
    }

    public async Task<(ProxyView? View, string? Error)> UpdateAsync(Guid id, ProxyUpsert dto)
    {
        await _mutex.WaitAsync();
        try
        {
            if (!_proxies.TryGetValue(id, out var instance))
                return (null, null); // not found

            if (Validate(dto, exceptId: id) is { } error)
                return (null, error);

            var old = instance.Config;
            var cfg = old with
            {
                Name = string.IsNullOrWhiteSpace(dto.Name) ? $"{dto.TargetHost}:{dto.TargetPort}" : dto.Name.Trim(),
                ListenPort = dto.ListenPort,
                TargetHost = dto.TargetHost.Trim(),
                TargetPort = dto.TargetPort,
                LatencyMs = dto.LatencyMs,
                JitterMs = dto.JitterMs,
                LossPercent = dto.LossPercent,
                IdleTimeoutSeconds = dto.IdleTimeoutSeconds,
                Enabled = dto.Enabled,
            };
            instance.UpdateConfig(cfg);

            // Latency/jitter/target apply live; only the listen socket needs a restart.
            bool needsRestart = cfg.ListenPort != old.ListenPort || cfg.Enabled != instance.Running;
            if (needsRestart)
            {
                await instance.StopAsync();
                if (cfg.Enabled)
                    instance.Start(); // failure shows up as Error in the view
            }

            await SaveAsync();
            return (instance.View(), null);
        }
        finally { _mutex.Release(); }
    }

    public async Task<bool> DeleteAsync(Guid id)
    {
        await _mutex.WaitAsync();
        try
        {
            if (!_proxies.Remove(id, out var instance))
                return false;
            await instance.StopAsync();
            await SaveAsync();
            return true;
        }
        finally { _mutex.Release(); }
    }

    private string? Validate(ProxyUpsert dto, Guid? exceptId)
    {
        if (dto.ListenPort is < 1 or > 65535)
            return "Listen port must be 1-65535.";
        if (dto.TargetPort is < 1 or > 65535)
            return "Target port must be 1-65535.";
        if (string.IsNullOrWhiteSpace(dto.TargetHost))
            return "Target host is required.";
        if (dto.LatencyMs is < 0 or > 600_000)
            return "Latency must be 0-600000 ms.";
        if (dto.JitterMs is < 0 or > 60_000)
            return "Jitter must be 0-60000 ms.";
        // NaN fails every comparison, so reject it explicitly rather than letting it through.
        if (double.IsNaN(dto.LossPercent) || dto.LossPercent is < 0 or > 100)
            return "Loss must be 0-100 %.";
        if (dto.IdleTimeoutSeconds is < 0 or > 86_400)
            return "Idle timeout must be 0-86400 s (0 disables).";
        if (_proxies.Values.Any(p => p.Config.Id != exceptId && p.Config.ListenPort == dto.ListenPort))
            return $"Another proxy already uses listen port {dto.ListenPort}.";
        return null;
    }

    private async Task SaveAsync()
    {
        var configs = _proxies.Values.Select(p => p.Config).OrderBy(c => c.ListenPort).ToList();
        var dir = Path.GetDirectoryName(Path.GetFullPath(_configPath));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var tmp = _configPath + ".tmp";
        await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(configs, JsonOptions));
        File.Move(tmp, _configPath, overwrite: true);
    }
}
