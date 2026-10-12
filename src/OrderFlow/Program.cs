using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using OrderFlow;

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<OrderStore>();
// Both API boundaries resolve the very same store consumed by the memory worker.
builder.Services.AddSingleton<IOrderAdmission>(services => services.GetRequiredService<OrderStore>());
builder.Services.AddSingleton<IOrderQueries>(services => services.GetRequiredService<OrderStore>());
builder.Services.AddSingleton<IFulfillmentSimulator, FulfillmentSimulator>();
builder.Services.AddSingleton<OrderProcessor>();
builder.Services.AddHostedService<OrderWorker>();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("submissions", _ => RateLimitPartition.GetFixedWindowLimiter("global", _ => new FixedWindowRateLimiterOptions
    { PermitLimit = 60, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
var app = builder.Build();
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; style-src 'self'; script-src 'self'; connect-src 'self'; img-src 'self' data:; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
    if (context.Request.Path.StartsWithSegments("/api")) context.Response.Headers.CacheControl = "no-store";
    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRateLimiter();
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
app.MapGet("/api/orders", async (HttpRequest request, IOrderQueries queries, CancellationToken cancellationToken) =>
    Session(request) is Guid session ? Results.Ok(await queries.ListAsync(session, cancellationToken)) : Results.BadRequest(new { error = "A valid X-Demo-Session UUID is required." }));
app.MapPost("/api/orders", async (HttpRequest request, SubmitRequest body, IOrderAdmission admission, CancellationToken cancellationToken) =>
{
    if (Session(request) is not Guid session || !Guid.TryParse(request.Headers["Idempotency-Key"], out var key) || key == Guid.Empty)
        return Results.BadRequest(new { error = "X-Demo-Session and Idempotency-Key must be non-empty UUIDs." });
    if (!Enum.TryParse<Scenario>(body.Scenario, true, out var scenario) || !Enum.IsDefined(scenario) || int.TryParse(body.Scenario, out _))
        return Results.BadRequest(new { error = "Scenario must be Success, Recover, or Exhaust." });
    var result = await admission.SubmitAsync(session, key, scenario, cancellationToken);
    if (result.Error is not null) return Results.Conflict(new { error = result.Error });
    return Results.Json(new { order = result.Order, replayed = result.Replayed }, statusCode: result.Replayed ? 200 : 202);
}).RequireRateLimiting("submissions");
app.Run();

static Guid? Session(HttpRequest request) => Guid.TryParse(request.Headers["X-Demo-Session"], out var session) && session != Guid.Empty ? session : null;
public sealed record SubmitRequest(string? Scenario);
public partial class Program;
