# PLAN.md — Walking Skeleton Build Plan

> Each step produces a working state, gets validated, and committed.
> Steps are numbered to match commits: `step N: <title>`.

---

## Step 0 — Add/refresh PRD.md and PLAN.md skeleton

### Goal
PRD is actionable. PLAN lists all steps with goals, files, and expected outputs.

### Files
- `docs/PRD.md` (moved from `.prd/PRD.md` to `docs/`)
- `docs/PLAN.md` (this file, in `docs/`)
- `.github/copilot-instructions.md` (GitHub Copilot agent instructions)
- `.claude/instructions.md` (Claude agent instructions)
- `.vscode/settings.json` (workspace settings with AI config)
- `.vscode/extensions.json` (recommended extensions)

### Status: ✅ Done

---

## Step 1 — Create catalog-api

### Goal
.NET 10 minimal API with all endpoints. `dotnet run` works, all routes respond correctly.

### Files to Create
- `src/catalog-api/catalog-api.csproj`
- `src/catalog-api/Program.cs`
- `src/catalog-api/Properties/launchSettings.json`

### Endpoints
| Route | Behavior |
|-------|----------|
| `GET /api/hello` | `{"service":"catalog","version":"v1","pod":"<hostname>","timestamp":"..."}` |
| `GET /api/slow?ms=500` | Same JSON after delay |
| `GET /api/fail` | 500 + `{"service":"catalog","error":"simulated failure",...}` |
| `GET /healthz` | `OK` (200) |
| `GET /readyz` | `OK` (200) |

### Validate
```powershell
cd src/catalog-api
dotnet run
# In another terminal:
curl http://localhost:8080/api/hello
curl http://localhost:8080/api/slow?ms=200
curl http://localhost:8080/api/fail        # expect 500
curl http://localhost:8080/healthz
curl http://localhost:8080/readyz
```

### Expected
- `/api/hello` → 200, JSON with service/version/pod/timestamp
- `/api/slow?ms=200` → 200, same JSON after ~200ms
- `/api/fail` → 500, JSON with error field
- `/healthz`, `/readyz` → 200, `OK`

### If It Fails
- `dotnet --version` must show 8.x
- Port 8080 busy → `netstat -ano | findstr :8080`

### Status: ✅ Done
- All 5 endpoints verified (hello, slow, fail, healthz, readyz)
- `/api/hello` → `{"service":"catalog","version":"v1","pod":"RHLM","timestamp":"..."}`
- `/api/fail` → 500 with error JSON
- Targets net8.0 with ImplicitUsings enabled (.NET 10 SDK installed)

---

## Step 2 — Create gateway-api

### Goal
.NET 10 minimal API that calls catalog's `/api/hello` and returns aggregated JSON. Propagates `x-request-id`.

### Files to Create
- `src/gateway-api/gateway-api.csproj`
- `src/gateway-api/Program.cs`
- `src/gateway-api/Properties/launchSettings.json`

### Endpoints
| Route | Behavior |
|-------|----------|
| `GET /api/aggregate` | Calls `http://catalog/api/hello`, wraps in `{"service":"gateway",...,"catalog":{...}}` |
| `GET /api/aggregate/slow?ms=NNN` | Calls `http://catalog/api/slow?ms=NNN` |
| `GET /healthz` | `OK` (200) |
| `GET /readyz` | `OK` (200) |

### Validate
```powershell
# Terminal 1 — catalog (port 8080)
cd src/catalog-api && dotnet run

# Terminal 2 — gateway (port 5100, env CATALOG_URL points to catalog)
cd src/gateway-api
$env:CATALOG_URL="http://localhost:8080"
dotnet run --urls "http://0.0.0.0:5100"

# Terminal 3
curl http://localhost:5100/api/aggregate
curl http://localhost:5100/healthz
```

### Expected
- `/api/aggregate` → 200, JSON with `service:"gateway"` + nested `catalog:{...}`
- Gateway gracefully returns error JSON if catalog is down

### If It Fails
- Wrong CATALOG_URL → check env var
- Connection refused → catalog not running

### Status: ✅ Done
- `/api/aggregate` → 200 with gateway wrapper + nested catalog JSON
- `/api/aggregate/slow?ms=200` → 200 with `"delayed":200` in catalog response
- `/healthz`, `/readyz` → 200 OK
- Uses `IHttpClientFactory`, propagates `x-request-id`, returns 502 when catalog is down
- Targets net10.0 with ImplicitUsings enabled

---

## Step 3 — Dockerfiles + local docker run

### Goal
Multi-stage Dockerfiles for both services. Images build and run in Docker.

### Files to Create
- `src/catalog-api/Dockerfile`
- `src/gateway-api/Dockerfile`
- `src/catalog-api/.dockerignore`
- `src/gateway-api/.dockerignore`

### Validate
```powershell
docker build -t catalog-api:local -f src/catalog-api/Dockerfile src/catalog-api
docker build -t gateway-api:local -f src/gateway-api/Dockerfile src/gateway-api

docker run --rm -d -p 8081:8080 --name catalog-test catalog-api:local
curl http://localhost:8081/api/hello
docker stop catalog-test
```

### Expected
- Build completes with no errors
- `/api/hello` returns JSON with pod = container hostname
- Image sizes < 120MB each

### If It Fails
- Docker not running → `docker info`
- Build error → check .dockerignore excludes `bin/`, `obj/`

### Status: ✅ Done
- Multi-stage Dockerfiles using Alpine + self-contained trimmed publish (`runtime-deps:10.0-alpine`)
- `docker build` succeeds with no errors for both services
- `/api/hello` → `{"service":"catalog","version":"v1","pod":"403a60583b7d","timestamp":"..."}` (container hostname)
- `/api/slow?ms=100` → 200 with `"delayed":100`
- `/api/fail` → 500 with error JSON
- `/healthz` → `"OK"`
- Image sizes: ~52 MB each (well under 120MB target)

---

## Step 4 — K8s base manifests + Kind deploy

### Goal
Services run in Kind with Istio sidecar injection. No Istio policies — just baseline mesh.

### Files to Create
- `k8s/namespace.yaml`
- `k8s/catalog-v1.yaml`
- `k8s/catalog-v2.yaml`
- `k8s/gateway.yaml`
- `k8s/services.yaml`

### Validate
```powershell
kind create cluster --name mesh-demo
istioctl install --set profile=demo -y

docker build -t catalog-api:local -f src/catalog-api/Dockerfile src/catalog-api
docker build -t gateway-api:local -f src/gateway-api/Dockerfile src/gateway-api
kind load docker-image catalog-api:local --name mesh-demo
kind load docker-image gateway-api:local --name mesh-demo

kubectl apply -f k8s/namespace.yaml
kubectl apply -f k8s/
kubectl wait --for=condition=ready pod -l app=catalog -n mesh-demo --timeout=120s
kubectl wait --for=condition=ready pod -l app=gateway -n mesh-demo --timeout=120s
kubectl get pods -n mesh-demo

# Port-forward and test
kubectl port-forward svc/gateway -n mesh-demo 9090:80 &
curl http://localhost:9090/api/aggregate
```

### Expected
```
catalog-v1-xxx   2/2   Running
catalog-v2-xxx   2/2   Running
gateway-xxx      2/2   Running
```
- `/api/aggregate` → 200 with catalog data (version randomly v1 or v2)

### If It Fails
- 1/2 containers: namespace missing `istio-injection=enabled` label
- ImagePullBackOff: forgot `kind load docker-image`
- Run `istioctl analyze -n mesh-demo`

### Status: 🔲 Not started

---

## Step 5 — Add loadgen manifest

### Goal
Loadgen deployment exists (replicas=0). Can be scaled to 1 to generate continuous traffic.

### Files to Create
- `k8s/loadgen.yaml`

### Validate
```powershell
kubectl apply -f k8s/loadgen.yaml
kubectl scale deploy/loadgen -n mesh-demo --replicas=1
kubectl wait --for=condition=ready pod -l app=loadgen -n mesh-demo --timeout=60s
kubectl logs -l app=loadgen -n mesh-demo --tail=3
kubectl scale deploy/loadgen -n mesh-demo --replicas=0
```

### Expected
- Pod starts as 2/2 (sidecar injected)
- Logs show HTTP 200 responses from gateway
- Scaling to 0 stops traffic

### If It Fails
- CrashLoopBackOff: check `TARGET_URL` env var — `http://gateway/api/aggregate`
- curl not found: ensure image is `curlimages/curl`

### Status: 🔲 Not started

---

## Step 6 — Canary routing (DestinationRule + VirtualService 90/10)

### Goal
Traffic to catalog splits ~90% v1, ~10% v2.

### Files to Create
- `istio/destinationrule-catalog.yaml`
- `istio/virtualservice-catalog-canary.yaml`

### Validate
```powershell
kubectl apply -f istio/destinationrule-catalog.yaml
kubectl apply -f istio/virtualservice-catalog-canary.yaml

# Ensure port-forward is active
1..50 | ForEach-Object {
  (Invoke-RestMethod http://localhost:9090/api/aggregate).catalog.version
} | Group-Object | Format-Table Name, Count
```

### Expected
```
Name  Count
----  -----
v1    ~45
v2    ~5
```

### If It Fails
- 100% one version: subset label mismatch — check `version` label on pods
- "no healthy upstream": DestinationRule subset doesn't match any pod
- `istioctl analyze -n mesh-demo`

### Status: 🔲 Not started

---

## Step 7 — Timeout + retry VirtualService

### Goal
2s timeout on catalog route. Calling `/api/aggregate/slow?ms=3000` fails; `ms=1000` succeeds.

### Files to Create
- `istio/virtualservice-catalog-resilience.yaml` (replaces canary VS)

### Validate
```powershell
kubectl apply -f istio/virtualservice-catalog-resilience.yaml

# Should fail (3s > 2s timeout)
curl -s -o /dev/null -w "%{http_code}" http://localhost:9090/api/aggregate/slow?ms=3000
# Should succeed (1s < 2s timeout)
curl -s -o /dev/null -w "%{http_code}" http://localhost:9090/api/aggregate/slow?ms=1000
```

### Expected
- `ms=3000` → 504 (upstream request timeout)
- `ms=1000` → 200

### If It Fails
- No timeout effect: VirtualService name must match the canary one to replace it
- Check `istioctl proxy-config route <gateway-pod> -n mesh-demo`

### Status: 🔲 Not started

---

## Step 8 — Outlier detection / circuit breaking

### Goal
DestinationRule adds outlier detection. Pods returning consecutive 5xx get ejected.

### Files to Create
- `istio/destinationrule-catalog-policy.yaml` (replaces base DR)

### Validate
```powershell
kubectl apply -f istio/destinationrule-catalog-policy.yaml

# Check endpoint health via Envoy admin
kubectl exec deploy/gateway -n mesh-demo -c istio-proxy -- \
  curl -s localhost:15000/clusters | Select-String "catalog.*health"
```

### Expected
- Endpoints show `health_flags::/failed_outlier_check` after consecutive 5xx
- Subsequent requests avoid ejected pods

### If It Fails
- Check DR applied: `kubectl get dr -n mesh-demo`
- Threshold too high: lower `consecutive5xxErrors`

### Status: 🔲 Not started

---

## Step 9 — mTLS STRICT

### Goal
Namespace-wide STRICT mTLS. Meshed traffic works; unmeshed callers fail.

### Files to Create
- `istio/peerauthentication-strict.yaml`

### Validate
```powershell
kubectl apply -f istio/peerauthentication-strict.yaml

# Meshed: should still work
curl http://localhost:9090/api/aggregate

# Unmeshed: should fail (default ns has no injection)
kubectl run test-mtls --image=curlimages/curl --rm -it --restart=Never -- \
  curl -s -o /dev/null -w "%{http_code}" http://catalog.mesh-demo.svc.cluster.local/api/hello
```

### Expected
- Gateway → catalog: 200
- Unmeshed pod → catalog: `000` or connection reset (no TLS cert)

### If It Fails
- Both pods must be 2/2
- Check `kubectl get peerauthentication -n mesh-demo`

### Status: 🔲 Not started

---

## Step 10 — AuthorizationPolicy

### Goal
Only `gateway-sa` can call catalog. Attacker pod calling catalog directly gets 403.

### Files to Create
- `istio/authorizationpolicy-catalog.yaml`

### Validate
```powershell
kubectl apply -f istio/authorizationpolicy-catalog.yaml

# Allowed (gateway-sa)
curl http://localhost:9090/api/aggregate

# Denied (attacker uses default SA, calls catalog directly)
kubectl run attacker --image=curlimages/curl --rm -it --restart=Never -n mesh-demo -- \
  curl -s -o /dev/null -w "%{http_code}" http://catalog/api/hello
```

### Expected
- Gateway aggregate: 200
- Attacker → catalog: `403`

### If It Fails
- Gateway also gets 403: wrong principal format — use `cluster.local/ns/mesh-demo/sa/gateway-sa`
- SA not set: check `serviceAccountName` in gateway deployment

### Status: 🔲 Not started

---

## Step 11 — Observability + README finalize

### Goal
Install addons, start loadgen, verify dashboards. Write complete article-style README.

### Files to Create
- `README.md`
- `scripts/load-on.ps1`
- `scripts/load-off.ps1`

### Validate
```powershell
# Install addons
kubectl apply -f https://raw.githubusercontent.com/istio/istio/release-1.23/samples/addons/prometheus.yaml
kubectl apply -f https://raw.githubusercontent.com/istio/istio/release-1.23/samples/addons/grafana.yaml
kubectl apply -f https://raw.githubusercontent.com/istio/istio/release-1.23/samples/addons/jaeger.yaml
kubectl apply -f https://raw.githubusercontent.com/istio/istio/release-1.23/samples/addons/kiali.yaml
kubectl rollout status deploy/kiali -n istio-system --timeout=120s

# Start loadgen
kubectl scale deploy/loadgen -n mesh-demo --replicas=1

# Wait 30s then open dashboards
istioctl dashboard kiali
istioctl dashboard grafana
istioctl dashboard jaeger
```

### Expected
- Kiali: graph shows `loadgen → gateway → catalog`
- Grafana: Istio dashboards show request rate
- Jaeger: traces with `gateway` and `catalog` spans

### Status: 🔲 Not started
