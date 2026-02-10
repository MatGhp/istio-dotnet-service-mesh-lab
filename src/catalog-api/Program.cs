var builder = WebApplication.CreateBuilder(args);

// Version is set via environment variable; defaults to "v1".
var version = Environment.GetEnvironmentVariable("CATALOG_VERSION") ?? "v1";
var pod = Environment.MachineName;

var app = builder.Build();

// ───────────────────────────────────────────────
// Health probes
// ───────────────────────────────────────────────
app.MapGet("/healthz", () => Results.Ok("OK"));
app.MapGet("/readyz",  () => Results.Ok("OK"));

// ───────────────────────────────────────────────
// GET /api/hello — identity response
// ───────────────────────────────────────────────
app.MapGet("/api/hello", () => Results.Ok(new
{
    service   = "catalog",
    version,
    pod,
    timestamp = DateTime.UtcNow.ToString("o")
}));

// ───────────────────────────────────────────────
// GET /api/slow?ms=NNN — artificial delay
// ───────────────────────────────────────────────
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

// ───────────────────────────────────────────────
// GET /api/fail — always returns 500
// ───────────────────────────────────────────────
app.MapGet("/api/fail", () => Results.Json(new
{
    service   = "catalog",
    error     = "simulated failure",
    pod,
    timestamp = DateTime.UtcNow.ToString("o")
}, statusCode: 500));

app.Run();
