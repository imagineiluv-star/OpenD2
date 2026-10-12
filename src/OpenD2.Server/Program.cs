using System.Net;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using OpenD2.Online;
using OpenD2.Server;

var builder = WebApplication.CreateBuilder(args);
string mode = builder.Configuration["mode"] ?? "realm";
bool managedHost = builder.Configuration["managed-host"] == "true";
LanHostSettings? lan = null;
if (managedHost && builder.Configuration["managed-lan"] == "stdin")
{
    if (!Console.IsInputRedirected || mode != "open") throw new ArgumentException("LAN host requires Open mode and a private stdin pipe.");
    lan = LanHostSettings.ReadLaunch(Console.In);
}
using var managedCertificate = lan?.LoadCertificate();
if (managedHost)
{
    if (mode != "open" || !Console.IsInputRedirected) throw new ArgumentException("Managed host requires Open mode and a private stdin pipe.");
    // Ignore configured network endpoints for the client-owned local host.
    builder.WebHost.ConfigureKestrel(options =>
    {
        options.Configure(new ConfigurationBuilder().Build());
        if (lan is null) options.Listen(IPAddress.Loopback, 0);
        else options.Listen(lan.Validate(), lan.Port, endpoint => endpoint.UseHttps(managedCertificate!));
    });
    builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(5));
}
if (mode is not ("realm" or "open")) throw new ArgumentException("Mode must be realm or open.");
// Operator opt-in only. Client-owned hosts ignore inherited publication settings.
// Validate before opening the realm store or listening; requests never read files.
byte[]? catalog = !managedHost && builder.Configuration["server-catalog"] is { } catalogFile
    ? ServerCatalog.Load(catalogFile).Encode() : null;
if (string.IsNullOrEmpty(builder.Configuration["urls"])) builder.WebHost.UseUrls("http://127.0.0.1:5080");
builder.WebHost.ConfigureKestrel(o => { o.Limits.MaxRequestBodySize = 8192; o.Limits.MaxConcurrentConnections = 64; });
builder.Services.ConfigureHttpJsonOptions(o => { o.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow; o.SerializerOptions.MaxDepth = 16; o.SerializerOptions.RespectRequiredConstructorParameters = true; o.SerializerOptions.AllowDuplicateProperties = false; });
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        context.GetEndpoint()?.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.IEndpointNameMetadata>()?.EndpointName == "room-stream"
            ? RateLimitPartition.GetConcurrencyLimiter("room-stream", _ => new() { PermitLimit = 32, QueueLimit = 0 })
            : RateLimitPartition.GetConcurrencyLimiter("realm", _ => new() { PermitLimit = 8, QueueLimit = 0 }));
    o.AddFixedWindowLimiter("password", x => { x.PermitLimit = 60; x.Window = TimeSpan.FromMinutes(1); x.QueueLimit = 0; });
    o.AddFixedWindowLimiter("game", x => { x.PermitLimit = 1000; x.Window = TimeSpan.FromSeconds(1); x.QueueLimit = 0; });
    o.AddFixedWindowLimiter("catalog", x => { x.PermitLimit = 60; x.Window = TimeSpan.FromMinutes(1); x.QueueLimit = 0; });
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
app.MapGet("/health", () => new { protocol = 1, rules = OpenD2.Core.GameSimulation.RulesVersion, roomStream = 1, mode, characterFileVersion = 4, inputReceipt = 1, roomDelta = 1, inputBatch = 1, roomInterest = 1, roomInputProgress = 1, roomWorld = 1, characterProgress = 1 });
if (catalog is not null)
    app.MapGet("/v1/server-catalog", (HttpContext context) => context.Request.IsHttps
        ? Results.Bytes(catalog, "application/json") : (IResult)Results.StatusCode(400)).RequireRateLimiting("catalog");
app.MapPost("/v1/register", (Credentials input, Realm realm) => realm.Login(input, true)).RequireRateLimiting("password");
app.MapPost("/v1/login", (Credentials input, Realm realm) => realm.Login(input, false)).RequireRateLimiting("password");
var api = app.MapGroup("/v1").RequireRateLimiting("game");
api.MapPost("/logout", (HttpContext c, Realm r) => r.Logout(Token(c)));
api.MapGet("/characters", (HttpContext c, Realm r) => r.Characters(Token(c)));
api.MapPost("/characters", (HttpContext c, Realm r, NameRequest input) => r.CreateCharacter(Token(c), input.Name, input.Class));
api.MapPost("/characters/{id:guid}/stats", (HttpContext c, Realm r, Guid id, StatRequest input) => r.SpendStat(Token(c), id, input));
api.MapPost("/characters/{id:guid}/skills", (HttpContext c, Realm r, Guid id, SkillRequest input) => r.SpendSkill(Token(c), id, input));
api.MapDelete("/characters/{id:guid}", (HttpContext c, Realm r, Guid id) => r.DeleteCharacter(Token(c), id));
api.MapGet("/characters/{id:guid}/export", (HttpContext c, Realm r, Guid id) => r.ExportCharacter(Token(c), id));
api.MapPost("/characters/import", (HttpContext c, Realm r, OpenCharacter input) => r.ImportCharacter(Token(c), input));
api.MapGet("/worlds", (HttpContext c, Realm r) => r.Worlds(Token(c)));
api.MapGet("/rooms", (HttpContext c, Realm r, bool? includeWorlds) => r.Rooms(Token(c), includeWorlds == true));
api.MapPost("/rooms", (HttpContext c, Realm r, RoomRequest input) => r.CreateRoom(Token(c), input)).RequireRateLimiting("password");
api.MapPost("/rooms/{id:guid}/join", (HttpContext c, Realm r, Guid id, JoinRequest input) => r.Join(Token(c), id, input)).RequireRateLimiting("password");
api.MapPost("/rooms/{id:guid}/invitation", (HttpContext c, Realm r, Guid id) => r.IssueInvitation(Token(c), id)).RequireRateLimiting("password");
api.MapDelete("/rooms/{id:guid}/invitation", (HttpContext c, Realm r, Guid id) => r.RevokeInvitation(Token(c), id));
api.MapGet("/rooms/{id:guid}", (HttpContext c, Realm r, Guid id) => r.State(Token(c), id));
api.MapGet("/rooms/{id:guid}/stream", (HttpContext c, Realm r, Guid id) => RoomStream.Run(c, r, id, Token(c))).WithName("room-stream");
api.MapPost("/rooms/{id:guid}/start", (HttpContext c, Realm r, Guid id) => r.Start(Token(c), id));
api.MapPost("/rooms/{id:guid}/input", (HttpContext c, Realm r, Guid id, InputRequest input) => r.Input(Token(c), id, input));
api.MapPost("/rooms/{id:guid}/input/ack", (HttpContext c, Realm r, Guid id, InputRequest input) => r.AcknowledgeInput(Token(c), id, input));
api.MapPost("/rooms/{id:guid}/input/batch", (HttpContext c, Realm r, Guid id, InputBatchRequest input) => r.BatchInput(Token(c), id, input));
api.MapPost("/rooms/{id:guid}/save", (HttpContext c, Realm r, Guid id) => r.Checkpoint(Token(c), id));
api.MapPost("/rooms/{id:guid}/leave", (HttpContext c, Realm r, Guid id) => r.Leave(Token(c), id));
api.MapPost("/rooms/{id:guid}/close", (HttpContext c, Realm r, Guid id) => r.Close(Token(c), id));
if (managedHost)
{
    app.Lifetime.ApplicationStarted.Register(() =>
    {
        string address = app.Urls.Single();
        Console.WriteLine("OPEND2_HOST_READY " + address);
    });
    _ = Task.Run(async () =>
    {
        // Closing the parent pipe, or an explicit stop, requests normal checkpointed shutdown.
        try
        {
            while (await Console.In.ReadLineAsync() is { } line)
                if (line == "stop") break;
        }
        finally { app.Lifetime.StopApplication(); }
    });
}
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
