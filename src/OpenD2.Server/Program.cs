using System.Net;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using OpenD2.Online;
using OpenD2.Server;

var builder = WebApplication.CreateBuilder(args);
string mode = builder.Configuration["mode"] ?? "realm";
if (mode is not ("realm" or "open")) throw new ArgumentException("Mode must be realm or open.");
if (string.IsNullOrEmpty(builder.Configuration["urls"])) builder.WebHost.UseUrls("http://127.0.0.1:5080");
builder.WebHost.ConfigureKestrel(o => { o.Limits.MaxRequestBodySize = 8192; o.Limits.MaxConcurrentConnections = 64; });
builder.Services.ConfigureHttpJsonOptions(o => { o.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow; o.SerializerOptions.MaxDepth = 16; });
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        context.GetEndpoint()?.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.IEndpointNameMetadata>()?.EndpointName == "room-stream"
            ? RateLimitPartition.GetConcurrencyLimiter("room-stream", _ => new() { PermitLimit = 32, QueueLimit = 0 })
            : RateLimitPartition.GetConcurrencyLimiter("realm", _ => new() { PermitLimit = 8, QueueLimit = 0 }));
    o.AddFixedWindowLimiter("password", x => { x.PermitLimit = 60; x.Window = TimeSpan.FromMinutes(1); x.QueueLimit = 0; });
    o.AddFixedWindowLimiter("game", x => { x.PermitLimit = 1000; x.Window = TimeSpan.FromSeconds(1); x.QueueLimit = 0; });
});
builder.Services.AddSingleton(new Realm(builder.Configuration["data"] ?? Path.Combine(AppContext.BaseDirectory, mode + "-data"), mode: mode));
builder.Services.AddHostedService<RealmLoop>();
var app = builder.Build();
app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store";
    if (!context.Request.IsHttps && !(context.Connection.RemoteIpAddress is { } ip && IPAddress.IsLoopback(ip)))
    { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new ApiError("HTTPS required.")); return; }
    try { await next(context); }
    catch (Rejected error) { context.Response.StatusCode = error.Status; await context.Response.WriteAsJsonAsync(new ApiError(error.Message)); }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { app.Logger.LogError(error, "Realm storage unavailable"); context.Response.StatusCode = 503; await context.Response.WriteAsJsonAsync(new ApiError("Storage unavailable.")); }
});
app.UseRateLimiter();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(5), KeepAliveTimeout = TimeSpan.FromSeconds(5) });
string Token(HttpContext c)
{
    string header = c.Request.Headers.Authorization.ToString();
    return header.StartsWith("Bearer ", StringComparison.Ordinal) && header.Length == 71 ? header[7..] : "";
}
app.MapGet("/health", () => new { protocol = 1, rules = OpenD2.Core.GameSimulation.RulesVersion, roomStream = 1, mode });
app.MapPost("/v1/register", (Credentials input, Realm realm) => realm.Login(input, true)).RequireRateLimiting("password");
app.MapPost("/v1/login", (Credentials input, Realm realm) => realm.Login(input, false)).RequireRateLimiting("password");
var api = app.MapGroup("/v1").RequireRateLimiting("game");
api.MapPost("/logout", (HttpContext c, Realm r) => r.Logout(Token(c)));
api.MapGet("/characters", (HttpContext c, Realm r) => r.Characters(Token(c)));
api.MapPost("/characters", (HttpContext c, Realm r, NameRequest input) => r.CreateCharacter(Token(c), input.Name));
api.MapDelete("/characters/{id:guid}", (HttpContext c, Realm r, Guid id) => r.DeleteCharacter(Token(c), id));
api.MapGet("/rooms", (HttpContext c, Realm r) => r.Rooms(Token(c)));
api.MapPost("/rooms", (HttpContext c, Realm r, RoomRequest input) => r.CreateRoom(Token(c), input)).RequireRateLimiting("password");
api.MapPost("/rooms/{id:guid}/join", (HttpContext c, Realm r, Guid id, JoinRequest input) => r.Join(Token(c), id, input)).RequireRateLimiting("password");
api.MapGet("/rooms/{id:guid}", (HttpContext c, Realm r, Guid id) => r.State(Token(c), id));
api.MapGet("/rooms/{id:guid}/stream", (HttpContext c, Realm r, Guid id) => RoomStream.Run(c, r, id, Token(c))).WithName("room-stream");
api.MapPost("/rooms/{id:guid}/start", (HttpContext c, Realm r, Guid id) => r.Start(Token(c), id));
api.MapPost("/rooms/{id:guid}/input", (HttpContext c, Realm r, Guid id, InputRequest input) => r.Input(Token(c), id, input));
api.MapPost("/rooms/{id:guid}/save", (HttpContext c, Realm r, Guid id) => r.Checkpoint(Token(c), id));
api.MapPost("/rooms/{id:guid}/leave", (HttpContext c, Realm r, Guid id) => r.Leave(Token(c), id));
api.MapPost("/rooms/{id:guid}/close", (HttpContext c, Realm r, Guid id) => r.Close(Token(c), id));
app.Run();

internal sealed class RealmLoop(Realm realm) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(40)); int tick = 0;
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken)) { realm.Tick(); if (++tick % 25 == 0) realm.Checkpoint(); }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { realm.Checkpoint(); }
    }
}
