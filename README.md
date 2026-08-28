# TCP Proxy

A general-purpose TCP proxy with simulated latency, configured through a web UI.
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
- **Live updates** — latency, jitter, and target apply immediately, even to
  established connections (target changes affect new connections). Changing
  the listen port or toggling enabled restarts the listener.
- **Stats** — active/total connections and bytes in each direction, live in
  the UI.

## HTTP API

The UI is a thin client over this API:

```
GET    /api/proxies          list proxies with live stats
POST   /api/proxies          create  {name?, listenPort, targetHost, targetPort, latencyMs, jitterMs, enabled}
PUT    /api/proxies/{id}     update (same body)
DELETE /api/proxies/{id}     remove
```

## Configuration

| Env var       | Default             | Purpose                          |
|---------------|---------------------|----------------------------------|
| `CONFIG_PATH` | `/data/config.json` | Where proxy configs are persisted |
| `ASPNETCORE_URLS` | `http://+:8080` | Web UI / API bind address        |
| `PROXY_<NAME>` | —                  | Seed a proxy on first boot (see below) |

### Initial proxies from environment

`PROXY_<NAME>` variables define proxies to create on first boot, in the form
`<listenPort>:<targetHost>:<targetPort>[:<latencyMs>[:<jitterMs>]]`:

```yaml
services:
  tcp-proxy:
    image: ghcr.io/aklein53/tcp-proxy:latest
    environment:
      - PROXY_POSTGRES=15432:db:5432:100     # 100ms latency to service "db"
      - PROXY_REDIS=16379:redis:6379:40:10   # 40ms ± 10ms jitter
    ports:
      - "8080:8080"
      - "15432:15432"
      - "16379:16379"
    volumes:
      - tcp-proxy-data:/data
```

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
  high latency × high throughput uses memory accordingly.
- The web UI has no authentication — don't expose port 8080 beyond your
  trusted network.
