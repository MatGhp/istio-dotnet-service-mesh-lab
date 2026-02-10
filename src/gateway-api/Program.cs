var builder = WebApplication.CreateBuilder(args);

// Base URL for catalog service — overridden locally, defaults to K8s service name.
var catalogUrl = Environment.GetEnvironmentVariable("CATALOG_URL") ?? "http://catalog";
var pod = Environment.MachineName;

// Register a named HttpClient for calling catalog-api.
builder.Services.AddHttpClient("catalog", client =>
{
    client.BaseAddress = new Uri(catalogUrl);
    client.Timeout = TimeSpan.FromSeconds(10);
});

var app = builder.Build();

// ───────────────────────────────────────────────
// Health probes
// ───────────────────────────────────────────────
app.MapGet("/healthz", () => Results.Ok("OK"));
app.MapGet("/readyz",  () => Results.Ok("OK"));

// ───────────────────────────────────────────────
// GET /api/aggregate — calls catalog /api/hello
// ───────────────────────────────────────────────
app.MapGet("/api/aggregate", async (IHttpClientFactory httpFactory, HttpContext ctx) =>
{
    var client = httpFactory.CreateClient("catalog");

    try
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/hello");
        PropagateRequestId(ctx, request);
        var response = await client.SendAsync(request);
        var catalogJson = await response.Content.ReadFromJsonAsync<object>();

        return Results.Ok(new
        {
            service   = "gateway",
            pod,
            timestamp = DateTime.UtcNow.ToString("o"),
            catalog   = catalogJson
        });
    }
    catch (Exception ex)
    {
        return Results.Json(new
        {
            service   = "gateway",
            pod,
            timestamp = DateTime.UtcNow.ToString("o"),
            error     = "catalog unavailable",
            detail    = ex.Message
        }, statusCode: 502);
    }
});

// ───────────────────────────────────────────────
// GET /api/aggregate/slow?ms=NNN — calls catalog /api/slow
// ───────────────────────────────────────────────
app.MapGet("/api/aggregate/slow", async (int ms, IHttpClientFactory httpFactory, HttpContext ctx) =>
{
    var client = httpFactory.CreateClient("catalog");

    try
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/slow?ms={ms}");
        PropagateRequestId(ctx, request);
        var response = await client.SendAsync(request);
        var catalogJson = await response.Content.ReadFromJsonAsync<object>();

        return Results.Ok(new
        {
            service   = "gateway",
            pod,
            timestamp = DateTime.UtcNow.ToString("o"),
            catalog   = catalogJson
        });
    }
    catch (Exception ex)
    {
        return Results.Json(new
        {
            service   = "gateway",
            pod,
            timestamp = DateTime.UtcNow.ToString("o"),
            error     = "catalog unavailable",
            detail    = ex.Message
        }, statusCode: 502);
    }
});

app.Run();

// ───────────────────────────────────────────────
// Propagate x-request-id for Istio tracing
// ───────────────────────────────────────────────
static void PropagateRequestId(HttpContext ctx, HttpRequestMessage request)
{
    if (ctx.Request.Headers.TryGetValue("x-request-id", out var requestId))
    {
        request.Headers.Add("x-request-id", requestId.ToString());
    }
}
