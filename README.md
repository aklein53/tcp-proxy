# TCP Proxy

A general-purpose TCP proxy that simulates adverse network conditions — latency,
jitter, packet loss, and idle disconnects — configured through a web UI.
Built with ASP.NET Core (.NET 10); ships as a single Docker image.

## Run

Multi-arch images (amd64/arm64) are published to GitHub Container Registry on
every push to `main`:

```yaml
# docker-compose.yml
services:
  tcp-proxy:
    image: ghcr.io/aklein53/tcp-proxy:latest
    ports:
      - "8080:8080"
      - "10000-10100:10000-10100"
    volumes:
      - tcp-proxy-data:/data
volumes:
  tcp-proxy-data:
```

Or build and run from source:

```sh
docker build -t tcp-proxy .
docker run -d --name tcp-proxy \
  -p 8080:8080 \
  -p 10000-10100:10000-10100 \
  -v tcp-proxy-data:/data \
  tcp-proxy
```

Open http://localhost:8080 to add proxies. Each proxy listens on a port inside
the container, so **listen ports must be within a published range** (the
`-p 10000-10100` mapping above). Alternatively run with `--network host`
(Linux only) to use any port. The `/data` volume persists proxy configs across
restarts.

## What it does

- **Proxying** — each proxy forwards `listen port → target host:port` at the
  byte level, so any TCP protocol works (HTTP, Postgres, Redis, ...).
- **Latency** — `latencyMs` is a one-way delay applied to every chunk in each
  direction (like `tc netem delay`), so a request/response exchange gains
  roughly 2× latency. Chunks are delayed via a queue while reads continue, so
  latency does not throttle throughput. `jitterMs` adds a random 0..N ms on
  top per chunk; ordering is always preserved.
- **Packet loss** — `lossPercent` is the chance each chunk is treated as a
  dropped segment. Loss on a TCP connection never destroys data: the segment is
  retransmitted, and the timeout doubles for each successive drop. So a "lost"
  chunk is held back for a retransmit timeout (`max(200ms, 2 × latency)`,
  matching Linux's `TCP_RTO_MIN`) rather than discarded, and because chunks
  leave in order, the ones behind it stall too — real head-of-line blocking.
  The byte stream always arrives complete and in order; what degrades is
  latency and throughput, which is what loss actually does to an application.
- **Idle disconnects** — `idleTimeoutSeconds` closes connections with no
  traffic in *either* direction for that long (0 disables), for exercising
  reconnect logic and connection-pool eviction.
- **Live updates** — latency, jitter, loss, and the idle timeout apply
  immediately, even to connections that are already open (target changes
  affect new connections). Changing the listen port or toggling enabled
  restarts the listener.
- **Stats** — active/total connections, bytes each way, simulated retransmits,
  and idle closures, live in the UI.

## HTTP API

The UI is a thin client over this API:

```
GET    /api/proxies          list proxies with live stats
POST   /api/proxies          create
PUT    /api/proxies/{id}     update (same body)
DELETE /api/proxies/{id}     remove
```

Create/update body:

```jsonc
{
  "name": "postgres",          // optional; defaults to "host:port"
  "listenPort": 15432,
  "targetHost": "db",
  "targetPort": 5432,
  "latencyMs": 100,            // one-way delay per direction
  "jitterMs": 10,              // random 0..N added per chunk
  "lossPercent": 5,            // 0-100, chance of a retransmit stall
  "idleTimeoutSeconds": 60,    // 0 disables
  "enabled": true
}
```

## Configuration

| Env var       | Default             | Purpose                          |
|---------------|---------------------|----------------------------------|
| `CONFIG_PATH` | `/data/config.json` | Where proxy configs are persisted |
| `ASPNETCORE_URLS` | `http://+:8080` | Web UI / API bind address        |
| `PROXY_<NAME>` | —                  | Seed a proxy on first boot (see below) |

### Initial proxies from environment

`PROXY_<NAME>` variables define proxies to create on first boot:

```
PROXY_<NAME>=<listenPort>:<targetHost>:<targetPort>[:tuning...]
```

The tuning fields are `latency`, `jitter`, `loss`, and `idle`. Give them
positionally in that order, or by name in any order — so setting just the idle
timeout doesn't mean padding the others with zeroes:

```yaml
services:
  tcp-proxy:
    image: ghcr.io/aklein53/tcp-proxy:latest
    environment:
      - PROXY_POSTGRES=15432:db:5432:100          # 100ms latency to service "db"
      - PROXY_REDIS=16379:redis:6379:40:10        # 40ms ± 10ms jitter
      - PROXY_API=18080:api:8080:loss=5:idle=30   # 5% loss, close after 30s idle
    ports:
      - "8080:8080"
      - "15432:15432"
      - "16379:16379"
      - "18080:18080"
    volumes:
      - tcp-proxy-data:/data
```

A malformed `PROXY_*` value is logged and skipped rather than failing startup.
Because the fields are colon-separated, IPv6 literal targets aren't supported
here — use a hostname, or add the proxy in the UI.

Seeding is **first-boot only**: it runs when `CONFIG_PATH` doesn't exist yet.
After that, the saved config wins, so changes made in the UI survive restarts
and env vars won't overwrite or resurrect anything. To re-seed from the
environment, remove the config file (e.g. `docker compose down -v`).

## Local development

```sh
dotnet run          # UI on the port shown in the console (or set ASPNETCORE_URLS)
```

## Notes

- Delayed chunks are buffered in memory (the bandwidth-delay product), so very
  high latency × high throughput uses memory accordingly. Loss adds to this:
  a stalled chunk holds its buffer, and the ones behind it queue up.
- Retransmit backoff is capped at 8 doublings, so a near-100% loss rate can't
  stall a chunk indefinitely.
- The web UI has no authentication — don't expose port 8080 beyond your
  trusted network.
