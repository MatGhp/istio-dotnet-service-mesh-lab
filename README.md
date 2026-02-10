# Istio Service Mesh with .NET — A Hands-On Lab

Microservices push cross-cutting concerns — retries, mTLS, canary routing, authorization — into every service's codebase. Istio moves all of that to the infrastructure layer. In this lab we build two .NET services, deploy them to Kubernetes with Istio sidecars, and layer on traffic management, security, and observability — without changing a line of application code.

## Prerequisites

- .NET 10 SDK
- Docker
- Kind
- kubectl
- istioctl 1.23+
- PowerShell 7+

## What We're Building

```
loadgen ──► gateway-api ──► catalog-api (v1 / v2)
```

**catalog-api** — returns its identity, version, and pod name. Exposes `/api/hello`, `/api/slow?ms=NNN` (artificial delay), and `/api/fail` (always 500). The `CATALOG_VERSION` env var controls whether it reports `v1` or `v2`.

**gateway-api** — calls catalog's `/api/hello`, wraps the response in its own JSON envelope. Returns a 502 with error detail when catalog is unreachable. Propagates `x-request-id` for Istio distributed tracing.

**loadgen** — a curl-based pod that hammers gateway on a loop. Scaled to 0 by default; scale to 1 when we need continuous traffic.

---

## Step 1: catalog-api

Every mesh demo needs a backend service to route traffic to. Our catalog-api is a .NET 10 minimal API with five routes that let us trigger specific Istio behaviors on demand.

The entire service lives in `src/catalog-api/Program.cs` — no controllers, no external NuGet packages. Version and pod identity come from environment variables so the same image can run as v1 or v2:

```csharp
var version = Environment.GetEnvironmentVariable("CATALOG_VERSION") ?? "v1";
var pod = Environment.MachineName;
```

Five routes cover everything we need:

| Route | Purpose |
|-------|---------|
| `GET /api/hello` | Identity — returns service, version, pod, timestamp |
| `GET /api/slow?ms=NNN` | Artificial delay — triggers Istio timeouts |
| `GET /api/fail` | Always 500 — triggers Istio retries |
| `GET /healthz` | Liveness probe |
| `GET /readyz` | Readiness probe |

The `/api/slow` endpoint clamps the delay to 30 seconds max to avoid runaway requests:

```csharp
app.MapGet("/api/slow", async (int ms) =>
{
    var clamped = Math.Clamp(ms, 0, 30_000);
    await Task.Delay(clamped);
    return Results.Ok(new
    {
        service   = "catalog",
        version,
        pod,
        timestamp = DateTime.UtcNow.ToString("o"),
        delayed   = clamped
    });
});
```

### Verify

```powershell
dotnet run --project src/catalog-api
# In another terminal:
curl http://localhost:8080/api/hello
```

Expected:
```json
{"service":"catalog","version":"v1","pod":"YOURHOSTNAME","timestamp":"2026-02-10T..."}
```

```powershell
curl http://localhost:8080/api/fail
```

Expected: HTTP 500 with `{"service":"catalog","error":"simulated failure",...}`

---

## Step 2: gateway-api

Gateway aggregates downstream calls and gives us a single entry point for the mesh. It calls catalog's `/api/hello`, wraps the response, and propagates `x-request-id` so Istio can trace the full request chain.

The key design decision: we use `IHttpClientFactory` with a named client instead of `new HttpClient()`. This gives us connection pooling, DNS rotation, and a clean place to set the base URL:

```csharp
builder.Services.AddHttpClient("catalog", client =>
{
    client.BaseAddress = new Uri(catalogUrl);
    client.Timeout = TimeSpan.FromSeconds(10);
});
```

The `CATALOG_URL` env var defaults to `http://catalog` — the Kubernetes service name. For local dev, we override it to `http://localhost:8080`.

When catalog is down, gateway catches the exception and returns a 502 with the error detail. No crash, no hang — just a clear error response:

```csharp
catch (Exception ex)
{
    return Results.Json(new
    {
        service = "gateway",
        pod,
        timestamp = DateTime.UtcNow.ToString("o"),
        error   = "catalog unavailable",
        detail  = ex.Message
    }, statusCode: 502);
}
```

Istio needs `x-request-id` propagated for distributed tracing to work. A small helper handles this:

```csharp
static void PropagateRequestId(HttpContext ctx, HttpRequestMessage request)
{
    if (ctx.Request.Headers.TryGetValue("x-request-id", out var requestId))
        request.Headers.Add("x-request-id", requestId.ToString());
}
```

### Verify

```powershell
# Terminal 1 — start catalog
dotnet run --project src/catalog-api

# Terminal 2 — start gateway
$env:CATALOG_URL="http://localhost:8080"
dotnet run --project src/gateway-api --urls "http://0.0.0.0:5100"

# Terminal 3 — test
curl http://localhost:5100/api/aggregate
```

Expected:
```json
{
  "service": "gateway",
  "pod": "YOURHOSTNAME",
  "timestamp": "2026-02-10T...",
  "catalog": {
    "service": "catalog",
    "version": "v1",
    "pod": "YOURHOSTNAME",
    "timestamp": "2026-02-10T..."
  }
}
```

Stop catalog and hit gateway again — we get a 502 with `"error":"catalog unavailable"`.

---

## Step 3: Dockerfiles

Before we can deploy to Kubernetes, both services need container images. We use multi-stage Alpine builds with self-contained, trimmed publish — no .NET runtime in the final image.

Both Dockerfiles follow the same pattern. Here's catalog's:

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src
COPY ["catalog-api.csproj", "."]
RUN dotnet restore -r linux-musl-x64
COPY . .
RUN dotnet publish -c Release -o /app/publish \
    --no-restore -r linux-musl-x64 \
    --self-contained true \
    -p:PublishTrimmed=true -p:TrimMode=partial

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-alpine AS runtime
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["./catalog-api"]
```

The `runtime-deps` base image is just Alpine + the native libraries .NET needs. No managed runtime, no SDK — final image comes in at ~52 MB.

Port 8080 everywhere. The Dockerfile sets `ASPNETCORE_URLS`, Kubernetes manifests will use `containerPort: 8080`, and Istio services will target the same port. One number, no confusion.

### Verify

```powershell
docker build -t catalog-api:local -f src/catalog-api/Dockerfile src/catalog-api
docker build -t gateway-api:local -f src/gateway-api/Dockerfile src/gateway-api

docker run --rm -d -p 8081:8080 --name catalog-test catalog-api:local
curl http://localhost:8081/api/hello
docker stop catalog-test
```

Expected:
```json
{"service":"catalog","version":"v1","pod":"403a60583b7d","timestamp":"..."}
```

The `pod` field now shows the container ID instead of the machine hostname — that's Docker's hostname isolation in action. In Kubernetes, this becomes the pod name.
