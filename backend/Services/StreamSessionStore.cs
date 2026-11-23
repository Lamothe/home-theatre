using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Options;
using HomeTheatre.Models;

namespace HomeTheatre.Services;

public sealed class StreamSession
{
    public required string Id { get; init; }
    public required Process? Process { get; init; }
    public required string TempDir { get; init; }
    public required string SourcePath { get; init; }
    public required bool Transcoding { get; init; }
    public required bool SubtitleBurnIn { get; init; }

    /// <summary>
    /// Absolute timeline bookkeeping for the static playlist written after
    /// startup: where real content begins (0 = no placeholder head) and the
    /// declared durations of the real-media segments that follow it. Used to
    /// convert produced segment counts into timeline positions (see
    /// StreamService.GetTimelineHeadSeconds).
    /// </summary>
    public double TimelineContentStart { get; set; }
    public IReadOnlyList<double> TimelineSegmentDurations { get; set; } = [];
    public DateTime LastHeartbeat { get; set; } = DateTime.UtcNow;
    public CancellationTokenSource Cts { get; } = new();
}

public interface IStreamSessionStore
{
    StreamSession Register(StreamSession session);
    StreamSession? Get(string id);
    bool TryRemove(string id, out StreamSession? session);
    IEnumerable<StreamSession> All();
    void Touch(string id);
}

public class StreamSessionStore : IStreamSessionStore
{
    private readonly ConcurrentDictionary<string, StreamSession> _sessions = new();

    public StreamSession Register(StreamSession session)
    {
        _sessions[session.Id] = session;
        return session;
    }

    public StreamSession? Get(string id) =>
        _sessions.TryGetValue(id, out var s) ? s : null;

    public bool TryRemove(string id, out StreamSession? session)
    {
        if (_sessions.TryRemove(id, out var s))
        {
            session = s;
            return true;
        }
        session = null;
        return false;
    }

    public IEnumerable<StreamSession> All() => _sessions.Values;

    public void Touch(string id)
    {
        if (_sessions.TryGetValue(id, out var s))
            s.LastHeartbeat = DateTime.UtcNow;
    }
}

public class StreamReaperService(IStreamSessionStore store, IOptions<VideoOptions> options, ILogger<StreamReaperService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var ttl = TimeSpan.FromMinutes(options.Value.StreamSessionTtlMinutes);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

                var now = DateTime.UtcNow;
                foreach (var s in store.All())
                {
                    if (now - s.LastHeartbeat < ttl)
                    {
                        continue;
                    }

                    logger.LogInformation("Reaping expired stream session {id}", s.Id);
                    StreamLifecycle.Kill(store, s.Id, s, logger);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal application shutdown.
        }
    }
}

public static class StreamLifecycle
{
    public static void Kill(IStreamSessionStore store, string id, StreamSession? session, ILogger logger)
    {
        if (session == null) return;
        try { session.Cts.Cancel(); } catch { }
        try
        {
            if (session.Process is { HasExited: false } p)
            {
                p.Kill(entireProcessTree: true);
                p.WaitForExit(2000);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to kill ffmpeg for session {id}", id);
        }

        try
        {
            if (Directory.Exists(session.TempDir))
                Directory.Delete(session.TempDir, recursive: true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to delete temp dir {dir} for session {id}", session.TempDir, id);
        }

        store.TryRemove(id, out _);
    }
}
