using System.Net;
using System.Security.Cryptography;
using System.Text;
using HomeTheatre.Models;
using HomeTheatre.Services;
using HomeTheatre.Services.Implementation;
using Microsoft.Extensions.FileProviders;

var token = Environment.GetEnvironmentVariable("HOME_THEATRE_TOKEN")
    ?? throw new InvalidOperationException("Launch the backend through Home Theatre.");
var tokenBytes = Encoding.UTF8.GetBytes(token);
var rendererPath = Environment.GetEnvironmentVariable("HOME_THEATRE_RENDERER")
    ?? throw new InvalidOperationException("The renderer path is required.");

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("settings.local.json", optional: true);
builder.Configuration.AddEnvironmentVariables("HOME_THEATRE_");
builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
builder.Services.Configure<VideoOptions>(builder.Configuration.GetSection("Video"));
builder.Services.AddControllers();
builder.Services.AddHttpClient<ITmdbService, TmdbService>().RemoveAllLoggers();
builder.Services.AddSingleton<IVideoPathResolver, VideoPathResolver>();
builder.Services.AddSingleton<IProbeService, ProbeService>();
builder.Services.AddSingleton<IStreamSessionStore, StreamSessionStore>();
builder.Services.AddSingleton<IStreamService, StreamService>();
builder.Services.AddSingleton<IMediaLibraryService, MediaLibraryService>();
builder.Services.AddHostedService<StreamReaperService>();

await using var app = builder.Build();
using var renderer = new PhysicalFileProvider(rendererPath);
app.Use(async (context, next) =>
{
    var presented = Encoding.UTF8.GetBytes(context.Request.Headers["X-Home-Theatre-Token"].ToString());
    if (!CryptographicOperations.FixedTimeEquals(presented, tokenBytes))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }

    context.Response.Headers.ContentSecurityPolicy =
        "default-src 'self'; style-src 'self' 'unsafe-inline'; worker-src 'self' blob:; media-src 'self' blob:; object-src 'none'; base-uri 'none'; frame-ancestors 'none'";
    await next();
});
app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = renderer });
app.UseStaticFiles(new StaticFileOptions { FileProvider = renderer });
app.MapControllers();
app.MapGet("/api/history/atomic", () => ReadHistory("history.local.json"));
app.MapGet("/api/history/atomic-mercury", () => ReadHistory("history.mercury.local.json"));

IResult ReadHistory(string filename)
{
    var path = Path.Combine(app.Environment.ContentRootPath, filename);
    return File.Exists(path) ? Results.File(path, "application/json") : Results.NoContent();
}

var store = app.Services.GetRequiredService<IStreamSessionStore>();
app.Lifetime.ApplicationStopping.Register(() =>
{
    foreach (var session in store.All())
        StreamLifecycle.Kill(store, session.Id, session, app.Logger);
});

await app.StartAsync();
Console.WriteLine($"HOME_THEATRE_READY {app.Urls.Single()}");
var shutdown = app.WaitForShutdownAsync();
if (await Task.WhenAny(shutdown, Console.In.ReadLineAsync()) != shutdown)
    app.Lifetime.StopApplication();
await shutdown;
