using System.Diagnostics;
using System.Globalization;
using System.Text;
using HomeTheatre.Models;
using Microsoft.Extensions.Options;

namespace HomeTheatre.Services;

public interface IStreamService
{
    Task<StartStreamResponse> StartAsync(string fullPath, int? audioStreamIndex, int? subtitleStreamIndex, double? startTimeSeconds, CancellationToken cancellationToken);
    (string Path, string ContentType)? GetPlaylistPath(string sessionId);
    (string Path, string ContentType)? GetSegmentPath(string sessionId, string fileName);

    /// <summary>
    /// Timeline position just past the newest segment ffmpeg has produced, or
    /// null when the session is unknown. Seeks beyond it target segments that
    /// do not exist yet and would stall the player until the pipeline catches up.
    /// </summary>
    double? GetTimelineHeadSeconds(string sessionId);
}

public class StreamService(
    IProbeService probe,
    IStreamSessionStore store,
    IOptions<VideoOptions> options,
    ILogger<StreamService> logger) : IStreamService
{
    private static readonly HashSet<string> BrowserVideoCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "h264", "av1", "vp9", "vp8"
    };

    private static readonly HashSet<string> BrowserAudioCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "aac", "opus", "vorbis", "mp3"
    };

    // Whether hardware H.264 encoders are usable on this host, each lazily
    // probed once (StreamService is a singleton). Selection order:
    //   1. h264_nvenc  — NVIDIA dGPUs (e.g. mercury's RTX 4070 Ti Super)
    //   2. h264_vaapi  — AMD iGPUs that expose H.264 VAAPI *encode* profiles
    //   3. libx264     — CPU fallback
    // Note that AMD Raphael (mercury) and Strix Halo (platinum) iGPUs expose
    // NO usable VAAPI H.264 *encode* profile (decode-only VCN), so both fall
    // through to the CPU path — mercury via NVENC first. Probing (rather than
    // assuming) keeps hosts with working VAAPI encode on the hardware path.
    private bool? _nvencAvailable;
    private readonly object _nvencLock = new();
    private bool? _vaapiEncodeAvailable;
    private readonly object _vaapiLock = new();

    /// <summary>
    /// Declared duration of every playlist entry except the last. The
    /// encoders are keyframe-locked to this grid (see the transcode argument
    /// builders) so real segment durations match the static playlist within a
    /// frame — hls.js maps seek positions through the declared durations, so
    /// any drift between the two lands seeks on the wrong media.
    /// </summary>
    private const double SegmentSeconds = 4.0;

    /// <summary>Forces encoder keyframes onto the <see cref="SegmentSeconds"/> grid.</summary>
    private static string SegmentKeyFrameExpr =>
        $"expr:gte(t,n_forced*{SegmentSeconds.ToString(CultureInfo.InvariantCulture)})";

    public async Task<StartStreamResponse> StartAsync(
        string fullPath,
        int? audioStreamIndex,
        int? subtitleStreamIndex,
        double? startTimeSeconds,
        CancellationToken cancellationToken)
    {
        if (!System.IO.File.Exists(fullPath))
            throw new FileNotFoundException("Source file not found", fullPath);

        var info = await probe.ProbeAsync(fullPath, cancellationToken);
        if (info.Video.Count == 0)
            throw new InvalidOperationException("File has no video stream");

        var videoStream = info.Video[0];
        var audioStream = info.Audio.Count > 0
            ? info.Audio.FirstOrDefault(a => a.Index == audioStreamIndex) ?? info.Audio[0]
            : null;

        var subStream = info.Subtitles.FirstOrDefault(s => s.Index == subtitleStreamIndex);

        var videoCompatible = BrowserVideoCodecs.Contains(videoStream.Codec);
        var audioCompatible = audioStream != null && BrowserAudioCodecs.Contains(audioStream.Codec);
        var needsBurnIn = subStream != null && !subStream.CanDeliverAsVtt;
        var softSubSidecar = subStream != null && subStream.CanDeliverAsVtt;

        // Direct play: video + audio codec compatible AND no burn-in required
        // (soft subs can be delivered as a VTT sidecar without touching the video)
        var directPlay = videoCompatible && audioCompatible && !needsBurnIn;
        // Audio-only transcode: video codec is fine but audio isn't — copy video,
        // re-encode audio to AAC. No VAAPI needed.
        var audioOnlyTranscode = !directPlay && videoCompatible && !needsBurnIn;

        var sessionId = Guid.NewGuid().ToString("N");
        var tempRoot = options.Value.StreamTempRoot;
        Directory.CreateDirectory(tempRoot);
        var tempDir = Path.Combine(tempRoot, sessionId);
        Directory.CreateDirectory(tempDir);

        var cts = new CancellationTokenSource();
        var burnIn = needsBurnIn;
        // Text tracks burn from a controlled single-track ASS extract: the
        // subtitles filter re-opens its input by path through filtergraph and
        // URL parsing, which library names containing quotes or colons break.
        // Bitmap tracks composite straight from the source container's stream.
        string? burnAssPath = null;
        if (burnIn && subStream is { IsBitmap: false })
        {
            burnAssPath = await ExtractTextBurnAssAsync(fullPath, subStream, tempDir, cancellationToken);
        }
        var burnTarget = burnIn ? new BurnInTarget(subStream!, burnAssPath) : null;

        // Resume via input seek: when the client asks to start mid-file, have
        // ffmpeg seek (-ss before -i) so the pipeline begins at that point,
        // and pad the playlist head with placeholder segments so the player
        // still sees the absolute source timeline (0..full duration) for its
        // seek bar and progress, while playback starts instantly at the
        // resume point. Sessions burning subtitles must decode from zero so
        // the burn-in filter graph (subtitles filter / sub2video overlay) sees
        // source-aligned timestamps.
        // Stream-copy pipelines cannot retime source keyframes: where the HLS
        // muxer cuts follows the source keyframe layout, so scan it once up
        // front — both to anchor mid-file starts on the real -ss landing point
        // and to plan the playlist tail around the cuts (PlanCopySegmentDurations).
        var copyLayout = (directPlay || audioOnlyTranscode)
            ? await ScanVideoKeyFramesAsync(fullPath, cancellationToken)
            : null;

        double? seekStart = needsBurnIn || startTimeSeconds is not > 0
            ? null
            : Math.Min(Math.Floor(startTimeSeconds.Value), Math.Max(0, info.DurationSeconds - 1));
        List<(string Name, double Duration)>? placeholderHead = null;
        if (seekStart is > 0)
        {
            // Stream-copy pipelines start at the last keyframe at or before the
            // target, so the placeholder head must cover exactly that anchor or
            // the picture plays ahead of the absolute timeline (and the subtitle
            // sidecar with it). Transcode pipelines are frame-accurate —
            // accurate_seek discards decode output up to the target — so they
            // anchor at the target itself instead of snapping playback back to
            // the previous keyframe.
            if (copyLayout is not null)
            {
                double? anchor = null;
                foreach (var pts in copyLayout.KeyFrames)
                {
                    if (pts <= seekStart.Value && (anchor is null || pts > anchor)) anchor = pts;
                }
                if (anchor is not null) seekStart = anchor.Value;
            }
            else if (directPlay || audioOnlyTranscode)
            {
                seekStart = await FindSeekAnchorAsync(fullPath, seekStart.Value, cancellationToken);
            }
            placeholderHead = await BuildPlaceholderHeadAsync(
                tempDir, videoStream.Width, videoStream.Height, audioStream != null, seekStart.Value, cancellationToken);
            if (placeholderHead is null)
            {
                // Without a padded head the player cannot show an absolute
                // timeline; run the pipeline from zero and let the client
                // seek the browser once metadata is available instead.
                seekStart = null;
            }
        }

        Process proc;
        Task<string> stderrDrainTask;
        Task<string> stdoutDrainTask;

        if (directPlay)
        {
            (proc, stderrDrainTask, stdoutDrainTask) = await StartWithCpuFallbackAsync(
                sessionId, tempDir, allowCpuFallback: false,
                () => StartRemux(fullPath, videoStream, audioStream, tempDir, seekStart, cts.Token),
                () => StartCpuTranscode(fullPath, videoStream, audioStream, audioCompatible, burnTarget, tempDir, seekStart, cts.Token),
                cancellationToken);
        }
        else if (audioOnlyTranscode)
        {
            (proc, stderrDrainTask, stdoutDrainTask) = await StartWithCpuFallbackAsync(
                sessionId, tempDir, allowCpuFallback: false,
                () => StartAudioTranscode(fullPath, videoStream, audioStream, tempDir, seekStart, cts.Token),
                () => StartCpuTranscode(fullPath, videoStream, audioStream, audioCompatible, burnTarget, tempDir, seekStart, cts.Token),
                cancellationToken);
        }
        else
        {
            (proc, stderrDrainTask, stdoutDrainTask) = await StartWithCpuFallbackAsync(
                sessionId, tempDir, allowCpuFallback: true,
                () => StartHardwareTranscode(fullPath, videoStream, audioStream, audioCompatible, burnTarget, tempDir, seekStart, cts.Token),
                () => StartCpuTranscode(fullPath, videoStream, audioStream, audioCompatible, burnTarget, tempDir, seekStart, cts.Token),
                cancellationToken);
        }

        // If a soft subtitle was requested, extract it to a standalone VTT sidecar
        // before returning its URL. The browser renders it via a <track> element — no need to
        // entangle it with the HLS muxer's subtitle playlist handling.
        string? subtitleUrl = null;
        if (softSubSidecar)
        {
            // Extracted from the full source: cue timestamps stay absolute,
            // matching the (placeholder-padded) absolute HLS timeline.
            using var extractionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            extractionCts.CancelAfter(TimeSpan.FromSeconds(60));
            try
            {
                if (await ExtractVttSidecar(fullPath, subStream!, tempDir, extractionCts.Token))
                {
                    subtitleUrl = $"/api/stream/{sessionId}/sub.vtt";
                }
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch
            {
                // Startup was abandoned before the session could be registered.
                TryKill(proc);
                cts.Cancel();
                await proc.WaitForExitAsync(CancellationToken.None);
                await Task.WhenAll(stderrDrainTask, stdoutDrainTask);
                proc.Dispose();
                cts.Dispose();
                try { Directory.Delete(tempDir, recursive: true); } catch { }
                throw;
            }
        }

        var session = new StreamSession
        {
            Id = sessionId,
            Process = proc,
            TempDir = tempDir,
            SourcePath = fullPath,
            Transcoding = !directPlay,
            SubtitleBurnIn = burnIn,
        };
        store.Register(session);

        // Write our own static VOD playlist listing ALL segments upfront, so
        // the browser knows the total duration from the start. ffmpeg's
        // growing playlist (enc.m3u8) is ignored for playback but tracked for
        // GetTimelineHeadSeconds. Stream-copy pipelines cannot retime source
        // keyframes, so their tail durations follow the source GOP layout
        // (PlanCopySegmentDurationsAsync); transcode pipelines are keyframe-
        // locked to the uniform SegmentSeconds grid. When the pipeline was
        // input-seeked to a resume point, placeholderHead pads the head of the
        // timeline up to the seek anchor and the real media follows an
        // EXT-X-DISCONTINUITY — an absolute source timeline with instant start.
        var plannedTail = copyLayout is not null
            ? PlanCopySegmentDurations(copyLayout, seekStart ?? 0)
            : null;
        var layout = WriteStaticPlaylist(tempDir, info.DurationSeconds, placeholderHead, plannedTail);
        session.TimelineContentStart = layout.ContentStart;
        session.TimelineSegmentDurations = layout.SegmentDurations;

        // Fire-and-forget cleanup if ffmpeg dies on its own.
        // stderr/stdout drain tasks were started above to prevent pipe deadlock.
        _ = Task.Run(async () =>
        {
            try { await proc.WaitForExitAsync(CancellationToken.None); }
            catch { }
            await stderrDrainTask;
            await stdoutDrainTask;
            if (store.Get(sessionId) is { } s && s.Process?.HasExited == true)
            {
                logger.LogInformation("ffmpeg exited for session {id} (code {code})", sessionId, proc.ExitCode);
            }
        }, CancellationToken.None);

        return new StartStreamResponse(
            sessionId,
            $"/api/stream/{sessionId}/playlist.m3u8",
            !directPlay,
            burnIn,
            subtitleUrl,
            layout.ContentStart);
    }

    private sealed record TranscodeAttempt(Process? Proc, Task<string>? Stderr, Task<string>? Stdout, int ExitCode, string Error)
    {
        public bool Success => Proc is not null;
    }

    /// <summary>
    /// Starts ffmpeg and waits for the first HLS segment. If the primary
    /// attempt is a hardware transcode that dies before producing any output
    /// (encoder probe passed but the encoder rejects this specific stream),
    /// the source is retried once with the CPU pipeline before failing.
    /// </summary>
    private async Task<(Process Proc, Task<string> Stderr, Task<string> Stdout)> StartWithCpuFallbackAsync(
        string sessionId,
        string tempDir,
        bool allowCpuFallback,
        Func<Process> startPrimary,
        Func<Process> startCpuFallback,
        CancellationToken cancellationToken)
    {
        var attempt = await StartAndAwaitFirstSegmentAsync(sessionId, tempDir, startPrimary, cancellationToken);
        if (attempt.Success || !allowCpuFallback)
        {
            return ThrowIfFailed(attempt);
        }

        logger.LogWarning(
            "ffmpeg transcode failed for session {id} (code {code}); retrying with CPU encoder: {err}",
            sessionId, attempt.ExitCode, Truncate(attempt.Error));

        // Remove any partial output from the failed attempt so the retry
        // starts from a clean slate. Placeholder segments (ph*.ts) were
        // materialised before the pipeline started and are referenced by the
        // playlist written after it, so they must survive the retry.
        foreach (var stale in Directory.EnumerateFiles(tempDir))
        {
            if (Path.GetFileName(stale).StartsWith("ph", StringComparison.Ordinal)) continue;
            TryDeleteFile(stale);
        }

        var retry = await StartAndAwaitFirstSegmentAsync(sessionId, tempDir, startCpuFallback, cancellationToken);
        return ThrowIfFailed(retry);
    }

    private (Process Proc, Task<string> Stderr, Task<string> Stdout) ThrowIfFailed(TranscodeAttempt attempt)
    {
        if (attempt.Proc is not { } proc || attempt.Stderr is not { } stderr || attempt.Stdout is not { } stdout)
            throw new InvalidOperationException(
                $"ffmpeg exited with code {attempt.ExitCode}{DescribeFfmpegError(attempt.Error)}");
        return (proc, stderr, stdout);
    }

    /// <summary>
    /// Surfaces the tail of ffmpeg's stderr in the thrown error so the client
    /// sees why startup failed instead of a generic failure message.
    /// </summary>
    private static string DescribeFfmpegError(string error)
    {
        var trimmed = error.Trim();
        if (trimmed.Length == 0) return string.Empty;
        const int max = 300;
        if (trimmed.Length > max) trimmed = "…" + trimmed[^max..];
        return $": {trimmed}";
    }

    private async Task<TranscodeAttempt> StartAndAwaitFirstSegmentAsync(
        string sessionId,
        string tempDir,
        Func<Process> start,
        CancellationToken cancellationToken)
    {
        var proc = start();

        // Start draining stderr/stdout immediately to prevent pipe buffer
        // deadlock, then wait (up to ~15s) for the first segment to appear so
        // the client doesn't race ahead and 404 on every segment.
        var stderr = proc.StandardError.ReadToEndAsync(CancellationToken.None);
        var stdout = proc.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var firstSegment = Path.Combine(tempDir, "0.ts");
        try
        {
            for (var i = 0; i < 150; i++)
            {
                if (System.IO.File.Exists(firstSegment))
                    return new TranscodeAttempt(proc, stderr, stdout, 0, string.Empty);

                if (proc.HasExited)
                {
                    var error = await WaitForStderr(stderr);
                    logger.LogError("ffmpeg exited early for session {id} (code {code}): {err}", sessionId, proc.ExitCode, error);
                    return new TranscodeAttempt(null, null, null, proc.ExitCode, error);
                }

                await Task.Delay(100, cancellationToken);
            }
        }
        catch
        {
            // Request aborted or unexpected failure — don't leak the process.
            TryKill(proc);
            throw;
        }

        // First segment never appeared but ffmpeg is still running (slow
        // source); hand the session back as-is, matching previous behaviour.
        return new TranscodeAttempt(proc, stderr, stdout, 0, string.Empty);
    }

    private static async Task<string> WaitForStderr(Task<string> stderr)
    {
        try
        {
            return await stderr.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string Truncate(string value, int maxLength = 2000) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private static void TryDeleteFile(string path)
    {
        try { System.IO.File.Delete(path); } catch { }
    }

    /// <summary>Length of each generated placeholder (black + silence) segment.</summary>
    private const double PlaceholderSegmentSeconds = 4;

    /// <summary>
    /// Writes the static VOD playlist for the session. When head is present
    /// the timeline starts with placeholder segments (black + silence) up to
    /// the seek anchor and the real media follows an EXT-X-DISCONTINUITY,
    /// giving the player an absolute source timeline (0..duration). Returns
    /// the position where real content begins — 0 when no placeholders were
    /// used (then the playlist only covers the remaining media and the
    /// client must seek the browser itself).
    /// </summary>
    private PlaylistLayout WriteStaticPlaylist(
        string tempDir,
        double totalDuration,
        List<(string Name, double Duration)>? head,
        IReadOnlyList<double>? plannedTail)
    {
        var anchor = head?.Sum(e => e.Duration) ?? 0;
        List<double> tail;
        if (plannedTail is not null)
        {
            tail = plannedTail.ToList();
        }
        else
        {
            var remaining = Math.Max(0.1, totalDuration - anchor);
            var segmentCount = Math.Max(1, (int)Math.Ceiling(remaining / SegmentSeconds));
            var lastDuration = remaining - (segmentCount - 1) * SegmentSeconds;
            if (lastDuration < 0.1) { segmentCount--; lastDuration = SegmentSeconds; }
            tail = Enumerable.Range(0, segmentCount)
                .Select(i => i == segmentCount - 1 ? lastDuration : SegmentSeconds)
                .ToList();
        }

        var entries = new List<(string Name, double Duration)>();
        double maxDuration = SegmentSeconds;
        if (head is not null)
        {
            foreach (var entry in head)
            {
                entries.Add(entry);
                maxDuration = Math.Max(maxDuration, entry.Duration);
            }
        }
        for (var i = 0; i < tail.Count; i++)
        {
            entries.Add(($"{i}.ts", tail[i]));
            maxDuration = Math.Max(maxDuration, tail[i]);
        }

        var sb = new StringBuilder();
        sb.AppendLine("#EXTM3U");
        sb.AppendLine("#EXT-X-VERSION:6");
        sb.AppendLine($"#EXT-X-TARGETDURATION:{(int)Math.Ceiling(maxDuration)}");
        sb.AppendLine("#EXT-X-MEDIA-SEQUENCE:0");
        sb.AppendLine("#EXT-X-PLAYLIST-TYPE:VOD");
        sb.AppendLine("#EXT-X-INDEPENDENT-SEGMENTS");

        var headCount = head?.Count ?? 0;
        for (var i = 0; i < entries.Count; i++)
        {
            if (i == headCount && headCount > 0)
            {
                // Real content resumes with fresh codec/timestamp state.
                sb.AppendLine("#EXT-X-DISCONTINUITY");
            }
            sb.AppendLine($"#EXTINF:{entries[i].Duration:F3},");
            sb.AppendLine(entries[i].Name);
        }

        sb.AppendLine("#EXT-X-ENDLIST");

        File.WriteAllText(Path.Combine(tempDir, "playlist.m3u8"), sb.ToString());
        return new PlaylistLayout(anchor, tail);
    }

    /// <summary>Static playlist layout: content start and the real-media tail entries in declared order.</summary>
    private sealed record PlaylistLayout(double ContentStart, IReadOnlyList<double> SegmentDurations);

    /// <summary>
    /// Builds the placeholder segment list covering [0, anchor). Segments are
    /// black video + silence encoded once into a shared cache directory
    /// (keyed by size and audio configuration) and copied into the session
    /// directory. Declared EXTINF values sum to exactly the anchor — hls.js
    /// re-bases every fragment to its playlist position, so the trailing
    /// milliseconds of encoder padding are absorbed at each boundary and real
    /// content starts precisely at the anchor. Returns null when placeholders
    /// cannot be produced; callers then fall back to a partial timeline.
    /// </summary>
    private async Task<List<(string Name, double Duration)>?> BuildPlaceholderHeadAsync(
        string tempDir,
        int? width,
        int? height,
        bool withAudio,
        double anchor,
        CancellationToken ct)
    {
        if (width is not > 0 || height is not > 0) return null;
        try
        {
            var cacheDir = Path.Combine(options.Value.StreamTempRoot, "placeholders");
            Directory.CreateDirectory(cacheDir);
            var size = $"{width}x{height}";
            var baseKey = $"ph_{size}_{(withAudio ? "a" : "v")}";

            var full = await EnsurePlaceholderAsync(cacheDir, baseKey, size, withAudio, PlaceholderSegmentSeconds, ct);
            if (full is null) return null;

            var entries = new List<(string Name, double Duration)>();
            var whole = (int)Math.Floor(anchor / PlaceholderSegmentSeconds);
            for (var i = 0; i < whole; i++)
            {
                var name = $"ph{i}.ts";
                File.Copy(full, Path.Combine(tempDir, name), overwrite: true);
                entries.Add((name, PlaceholderSegmentSeconds));
            }

            var remainder = anchor - whole * PlaceholderSegmentSeconds;
            if (remainder > 0.05)
            {
                // Partial placeholder for the tail of the padded head; the
                // cache key rounds so nearby sessions share one file.
                var partialKey = $"{baseKey}_p{Math.Round(remainder, 1).ToString(CultureInfo.InvariantCulture)}";
                var partial = await EnsurePlaceholderAsync(cacheDir, partialKey, size, withAudio, remainder, ct);
                if (partial is null) return null;
                File.Copy(partial, Path.Combine(tempDir, "php.ts"), overwrite: true);
                entries.Add(("php.ts", remainder));
            }

            return entries;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Placeholder generation failed for {size}; falling back to a partial timeline", $"{width}x{height}");
            return null;
        }
    }

    /// <summary>
    /// Returns the cached placeholder segment for the given size/duration,
    /// encoding it once on first use. The declared playlist duration is the
    /// requested value; any encoder tail padding is absorbed by the player's
    /// per-fragment re-basing.
    /// </summary>
    private async Task<string?> EnsurePlaceholderAsync(
        string cacheDir,
        string key,
        string size,
        bool withAudio,
        double duration,
        CancellationToken ct)
    {
        var file = Path.Combine(cacheDir, $"{key}.ts");
        if (File.Exists(file)) return file;

        var stamp = Guid.NewGuid().ToString("N");
        var tmpFile = $"{file}.{stamp}.tmp";
        var args = new List<string>
        {
            "-y", "-hide_banner", "-loglevel", "error",
            "-f", "lavfi", "-i", $"color=c=black:s={size}:r=25",
            "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo",
            "-map", "0:v",
            "-t", duration.ToString(CultureInfo.InvariantCulture),
            "-r", "25",
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
            "-g", "25", "-keyint_min", "25", "-sc_threshold", "0",
            "-bsf:v", "h264_mp4toannexb",
        };
        if (withAudio)
        {
            args.AddRange(new[] { "-map", "1:a", "-c:a", "aac", "-b:a", "128k" });
        }
        else
        {
            args.Add("-an");
        }
        args.AddRange(new[] { "-f", "mpegts", tmpFile });

        var psi = NewProcessStartInfo(options.Value.FfmpegPath, args);
        using var proc = Process.Start(psi);
        if (proc == null) return null;
        await proc.WaitForExitAsync(ct);
        if (proc.ExitCode != 0 || !File.Exists(tmpFile))
        {
            TryDeleteFile(tmpFile);
            return null;
        }
        File.Move(tmpFile, file, overwrite: true);
        return file;
    }

    /// <summary>
    /// Finds the last video keyframe at or before the seek target. Input
    /// seeking with stream copy anchors every stream at that keyframe, so it
    /// must be the reference point for every seek (pipeline and subtitle
    /// sidecar alike). Falls back to the raw target when the probe fails or
    /// no keyframe is found even in the widened window.
    /// </summary>
    private const double SeekAnchorProbeWindowSeconds = 30;

    /// <summary>
    /// Second-pass window for sparse-GOP sources (long-GOP x265 releases can
    /// go minutes between keyframes). Only paid when the fast window finds
    /// nothing; packet scanning is demux-only and cheap on that scale.
    /// </summary>
    private const double SeekAnchorProbeFallbackWindowSeconds = 300;

    private async Task<double> FindSeekAnchorAsync(string input, double target, CancellationToken ct)
    {
        var anchor = await ProbeSeekAnchorAsync(input, target, SeekAnchorProbeWindowSeconds, ct);
        if (anchor is null)
        {
            anchor = await ProbeSeekAnchorAsync(input, target, SeekAnchorProbeFallbackWindowSeconds, ct);
        }
        return anchor ?? target;
    }

    private async Task<double?> ProbeSeekAnchorAsync(string input, double target, double windowSeconds, CancellationToken ct)
    {
        try
        {
            var from = Math.Max(0, target - windowSeconds);
            var args = new List<string>
            {
                "-v", "error",
                "-select_streams", "v:0",
                "-show_entries", "packet=pts_time,flags",
                "-of", "csv=p=0",
                "-read_intervals", $"{from.ToString(CultureInfo.InvariantCulture)}%{target.ToString(CultureInfo.InvariantCulture)}",
                input,
            };
            var psi = NewProcessStartInfo(options.Value.FfprobePath, args);
            using var proc = Process.Start(psi);
            if (proc is null) return null;
            ct.Register(() => TryKill(proc));
            var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            if (proc.ExitCode != 0) return null;

            double? anchor = null;
            foreach (var pts in ParseKeyFramePts(stdout))
            {
                if (pts <= target && (anchor is null || pts > anchor)) anchor = pts;
            }

            return anchor;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Keyframe anchor probe failed for {input} ({window}s window)", input, windowSeconds);
            return null;
        }
    }

    /// <summary>Video keyframe PTS values from ffprobe packet CSV output ("12.000000,K__").</summary>
    private static List<double> ParseKeyFramePts(string packetCsv)
    {
        var keyframes = new List<double>();
        foreach (var line in packetCsv.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(',');
            if (parts.Length < 2 || !parts[1].Contains('K')) continue;
            if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var pts)) continue;
            keyframes.Add(pts);
        }
        return keyframes;
    }

    private async Task<CopySourceLayout?> ScanVideoKeyFramesAsync(string input, CancellationToken ct)
    {
        try
        {
            var args = new List<string>
            {
                "-v", "error",
                "-select_streams", "v:0",
                "-show_entries", "packet=pts_time,duration_time,flags",
                "-of", "csv=p=0",
                input,
            };
            var psi = NewProcessStartInfo(options.Value.FfprobePath, args);
            using var proc = Process.Start(psi);
            if (proc is null) return null;
            ct.Register(() => TryKill(proc));
            var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            if (proc.ExitCode != 0) return null;

            var keyframes = new List<double>();
            var endPts = 0.0;
            foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                // csv=p=0 rows look like: 12.000000,0.040000,K__
                var parts = line.Split(',');
                if (parts.Length < 3 || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var pts))
                {
                    continue;
                }
                if (double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var duration))
                {
                    endPts = Math.Max(endPts, pts + duration);
                }
                if (parts[2].Contains('K')) keyframes.Add(pts);
            }

            return keyframes.Count > 0 ? new CopySourceLayout(keyframes, endPts) : null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Keyframe scan failed for {input}; falling back to uniform segments", input);
            return null;
        }
    }

    /// <summary>Source video keyframe PTS values and the end PTS the muxer measures the final segment against.</summary>
    private sealed record CopySourceLayout(IReadOnlyList<double> KeyFrames, double EndPts);

    /// <summary>
    /// Declares stream-copy segment durations matching where ffmpeg's HLS
    /// muxer will cut. Stream copy cannot retime keyframes, and the muxer
    /// closes a segment at the first video keyframe at or after SegmentSeconds×k
    /// of media from the pipeline's first packet (hlsenc.c compares
    /// <c>pkt->pts - vs->start_pts</c> against <c>recording_time * number</c>),
    /// so with a coarse source GOP the cuts do NOT fall on the uniform grid the
    /// static playlist declares — the playlist must follow the source layout or
    /// hls.js maps seeks to the wrong media. The grid therefore starts at the
    /// pipeline's first output keyframe and ends at the source video end PTS,
    /// not at zero/the container duration: sources need not begin at PTS 0
    /// (transport streams carry arbitrary bases). Residual: the muxer's origin
    /// can sit up to one audio frame below the first keyframe when audio is
    /// muxed first, which only matters when a keyframe falls inside that band.
    /// </summary>
    private static List<double>? PlanCopySegmentDurations(CopySourceLayout layout, double start)
    {
        double? origin = null;
        foreach (var pts in layout.KeyFrames)
        {
            if (pts >= start) { origin = pts; break; }
        }
        var gridStart = origin ?? start;

        var durations = new List<double>();
        var cut = gridStart;
        var index = 0;
        while (cut < layout.EndPts - 0.001)
        {
            var threshold = gridStart + SegmentSeconds * (durations.Count + 1);
            double? next = null;
            while (index < layout.KeyFrames.Count)
            {
                var pts = layout.KeyFrames[index++];
                if (pts <= cut) continue;
                if (pts >= threshold) { next = pts; break; }
            }
            var end = next ?? layout.EndPts;
            if (end <= cut) break;
            durations.Add(end - cut);
            cut = end;
        }

        return durations.Count > 0 ? durations : null;
    }

    /// <summary>
    /// Timeline position just past the newest segment ffmpeg has produced,
    /// in the static playlist's declared coordinates: the placeholder head
    /// plus the declared durations of the produced tail entries. Null when
    /// the session is unknown.
    /// </summary>
    public double? GetTimelineHeadSeconds(string sessionId)
    {
        var session = store.Get(sessionId);
        if (session is null) return null;

        var produced = 0;
        try
        {
            // FileShare.ReadWrite: ffmpeg rewrites this playlist as segments
            // complete; a torn read just under-counts one segment.
            using var stream = new FileStream(
                Path.Combine(session.TempDir, "enc.m3u8"),
                FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                if (line.Length > 0 && line[0] != '#') produced++;
            }
        }
        catch (FileNotFoundException)
        {
            // No completed segments yet; the placeholder head is all we have.
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (IOException)
        {
            return null;
        }

        var declared = session.TimelineSegmentDurations;
        var count = Math.Min(produced, declared.Count);
        var tail = 0.0;
        for (var i = 0; i < count; i++)
        {
            tail += declared[i];
        }
        return session.TimelineContentStart + tail;
    }

    private async Task<bool> ExtractVttSidecar(string input, ProbeSubtitleStream sub, string tempDir, CancellationToken ct)
    {
        var ffmpeg = options.Value.FfmpegPath;
        var outPath = Path.Combine(tempDir, "sub.vtt");
        // Never expose a growing VTT: browsers fetch text tracks once, unlike HLS
        // segments. Publish the sidecar atomically only after successful extraction.
        var pendingPath = Path.Combine(tempDir, "sub.pending");
        var args = new List<string> { "-y", "-i", input, "-map", $"0:{sub.Index}", "-c:s", "webvtt", "-f", "webvtt", pendingPath };
        var psi = NewProcessStartInfo(ffmpeg, args);
        try
        {
            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start subtitle extraction");
            using var registration = ct.Register(() => TryKill(proc));
            // ffmpeg writes diagnostics/progress to stderr; drain both pipes before
            // waiting so large/multi-track sources cannot fill a pipe and deadlock.
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            await stdoutTask;
            var stderr = await stderrTask;
            if (proc.ExitCode != 0)
            {
                logger.LogWarning("VTT sidecar extraction failed for stream {idx} ({code}): {err}", sub.Index, proc.ExitCode, stderr);
                return false;
            }
            File.Move(pendingPath, outPath);
            logger.LogInformation("VTT sidecar extracted for stream {idx}", sub.Index);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "VTT sidecar extraction failed for stream {idx}", sub.Index);
            return false;
        }
        finally
        {
            try { File.Delete(pendingPath); } catch { }
        }
    }

    /// <summary>
    /// Extracts a text subtitle track to a controlled single-track ASS file for
    /// the burn-in filter graph. The subtitles filter re-opens its input by
    /// path (through filtergraph quoting and avformat URL parsing), so library
    /// names containing quotes or colons cannot be passed to it directly. Fails
    /// loudly (with ffmpeg's stderr tail) — the caller asked for burned
    /// subtitles, and silently dropping them is worse than an actionable error.
    /// </summary>
    private async Task<string> ExtractTextBurnAssAsync(string input, ProbeSubtitleStream sub, string tempDir, CancellationToken ct)
    {
        var ffmpeg = options.Value.FfmpegPath;
        var outPath = Path.Combine(tempDir, "burn.ass");
        var args = new List<string> { "-y", "-i", input, "-map", $"0:{sub.Index}", "-c:s", "ass", "-f", "ass", outPath };
        var psi = NewProcessStartInfo(ffmpeg, args);
        try
        {
            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start subtitle extraction");
            using var registration = ct.Register(() => TryKill(proc));
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            await stdoutTask;
            var stderr = await stderrTask;
            if (proc.ExitCode != 0 || !File.Exists(outPath))
            {
                logger.LogWarning("Burn-in ASS extraction failed for stream {idx} ({code}): {err}", sub.Index, proc.ExitCode, stderr);
                throw new InvalidOperationException(
                    $"Subtitle extraction failed (ffmpeg exit {proc.ExitCode}){DescribeFfmpegError(stderr)}");
            }
            logger.LogInformation("Burn-in ASS extracted for stream {idx}", sub.Index);
            return outPath;
        }
        catch
        {
            // Startup was abandoned before the session could be registered.
            try { Directory.Delete(tempDir, recursive: true); } catch { }
            throw;
        }
    }

    private Process StartRemux(
        string input,
        ProbeVideoStream video,
        ProbeAudioStream? audio,
        string tempDir,
        double? startTime,
        CancellationToken ct)
    {
        var ffmpeg = options.Value.FfmpegPath;
        var playlist = Path.Combine(tempDir, "enc.m3u8");
        var segPattern = Path.Combine(tempDir, "%d.ts");

        var args = new List<string>();
        // Input seek before -i. Stream copy lands at the keyframe at or before
        // the value, so nudge 10ms past the anchor: the printed keyframe PTS is
        // rounded, and an exact match can snap to the previous keyframe while
        // the placeholder head assumes the anchor.
        if (startTime is > 0)
        {
            args.Add("-ss"); args.Add((startTime.Value + 0.01).ToString(CultureInfo.InvariantCulture));
        }
        args.Add("-fflags"); args.Add("+genpts");
        args.Add("-i"); args.Add(input);
        args.Add("-map"); args.Add($"0:{video.Index}");
        if (audio != null)
        {
            args.Add("-map"); args.Add($"0:{audio.Index}");
        }
        args.Add("-c:v"); args.Add("copy");
        args.Add("-c:a"); args.Add("copy");
        args.Add("-bsf:v"); args.Add("h264_mp4toannexb");
        AppendHlsArgs(args, playlist, segPattern);

        var psi = NewProcessStartInfo(ffmpeg, args);
        var proc = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg start failed");
        ct.Register(() => TryKill(proc));
        return proc;
    }

    private Process StartAudioTranscode(
        string input,
        ProbeVideoStream video,
        ProbeAudioStream? audio,
        string tempDir,
        double? startTime,
        CancellationToken ct)
    {
        var ffmpeg = options.Value.FfmpegPath;
        var playlist = Path.Combine(tempDir, "enc.m3u8");
        var segPattern = Path.Combine(tempDir, "%d.ts");

        var args = new List<string>();
        // Input seek before -i (nudged as in StartRemux: stream copy must land
        // on the anchor keyframe, not the one before it).
        if (startTime is > 0)
        {
            args.Add("-ss"); args.Add((startTime.Value + 0.01).ToString(CultureInfo.InvariantCulture));
        }
        args.Add("-fflags"); args.Add("+genpts");
        args.Add("-i"); args.Add(input);
        args.Add("-map"); args.Add($"0:{video.Index}");
        if (audio != null)
        {
            args.Add("-map"); args.Add($"0:{audio.Index}");
        }
        args.Add("-c:v"); args.Add("copy");
        args.Add("-c:a"); args.Add("aac");
        args.Add("-b:a"); args.Add("160k");
        args.Add("-ac"); args.Add("2");
        args.Add("-bsf:v"); args.Add("h264_mp4toannexb");
        AppendHlsArgs(args, playlist, segPattern);

        var psi = NewProcessStartInfo(ffmpeg, args);
        var proc = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg start failed");
        ct.Register(() => TryKill(proc));
        return proc;
    }

    /// <summary>
    /// Burn-in target: the subtitle stream plus, for text tracks, the path of
    /// the extracted single-track ASS file the burn-in filter reads (null for
    /// bitmap tracks, which overlay composites straight from the source).
    /// </summary>
    private sealed record BurnInTarget(ProbeSubtitleStream Stream, string? TextAssPath);

    /// <summary>
    /// Appends the video mapping and filter graph shared by the transcode
    /// pipelines. Bitmap subtitles (PGS/DVB/DVD) cannot pass through libass at
    /// all ("Only text based subtitles are currently supported") — they are
    /// decoded to bitmaps and composited with overlay (ffmpeg's sub2video turns
    /// the subtitle stream into filter-graph frames), referencing the source
    /// stream directly by index so no path enters the graph. Text subtitles
    /// burn through the libass-based subtitles filter reading the controlled
    /// single-track ASS extract (see ExtractTextBurnAssAsync), hence si=0.
    /// <paramref name="tailFilter"/> converts pixels for the encoder that follows.
    /// </summary>
    private static void AppendVideoPipeline(
        List<string> args,
        ProbeVideoStream video,
        ProbeAudioStream? audio,
        BurnInTarget? burnSub,
        string tailFilter)
    {
        if (burnSub is { Stream.IsBitmap: true })
        {
            args.Add("-filter_complex");
            args.Add($"[0:{video.Index}][0:{burnSub.Stream.Index}]overlay,{tailFilter}[v]");
            args.Add("-map"); args.Add("[v]");
        }
        else
        {
            args.Add("-map"); args.Add($"0:{video.Index}");
            // Single-quote the (server-controlled) extract path, joining any
            // quote with '\'' — the filtergraph tokenizer treats backslashes
            // inside quotes as literal.
            var vf = burnSub != null
                ? $"subtitles='{burnSub.TextAssPath!.Replace("'", "'\\''")}':si=0,{tailFilter}"
                : tailFilter;
            args.Add("-vf"); args.Add(vf);
        }

        if (audio != null)
        {
            args.Add("-map"); args.Add($"0:{audio.Index}");
        }
    }

    private Process StartVaapiTranscode(
        string input,
        ProbeVideoStream video,
        ProbeAudioStream? audio,
        bool audioCompatible,
        BurnInTarget? burnSub,
        string tempDir,
        double? startTime,
        CancellationToken ct)
    {
        var ffmpeg = options.Value.FfmpegPath;
        var vaapiDevice = options.Value.VaapiDevice;
        var playlist = Path.Combine(tempDir, "enc.m3u8");
        var segPattern = Path.Combine(tempDir, "%d.ts");

        var args = new List<string>();
        args.Add("-vaapi_device"); args.Add(vaapiDevice);

        // Input seek before -i for fast seek
        if (startTime is > 0)
        {
            args.Add("-ss"); args.Add(startTime.Value.ToString(CultureInfo.InvariantCulture));
        }
        args.Add("-i"); args.Add(input);
        AppendVideoPipeline(args, video, audio, burnSub, tailFilter: "format=nv12,hwupload");

        args.Add("-c:v"); args.Add("h264_vaapi");
        args.Add("-b:v"); args.Add("6M");
        args.Add("-maxrate"); args.Add("8M");
        args.Add("-bufsize"); args.Add("12M");
        args.Add("-rc_mode"); args.Add("VBR");
        // Lock keyframes to the playlist's segment grid (as IDR frames) so the
        // HLS muxer cuts every segment there and real durations stay within a
        // frame of the declared EXTINF values hls.js maps seeks through.
        args.Add("-forced_idr"); args.Add("1");
        args.Add("-force_key_frames"); args.Add(SegmentKeyFrameExpr);

        args.Add("-c:a");
        args.Add(audioCompatible ? "copy" : "aac");
        if (!audioCompatible)
        {
            args.Add("-b:a"); args.Add("160k");
            args.Add("-ac"); args.Add("2");
        }

        AppendHlsArgs(args, playlist, segPattern);

        var psi = NewProcessStartInfo(ffmpeg, args);
        var proc = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg start failed");
        ct.Register(() => TryKill(proc));
        return proc;
    }

    private Process StartHardwareTranscode(
        string input,
        ProbeVideoStream video,
        ProbeAudioStream? audio,
        bool audioCompatible,
        BurnInTarget? burnSub,
        string tempDir,
        double? startTime,
        CancellationToken ct)
    {
        if (UseNvenc())
        {
            logger.LogInformation("Using h264_nvenc to transcode {input}", input);
            return StartNvencTranscode(input, video, audio, audioCompatible, burnSub, tempDir, startTime, ct);
        }

        if (UseVaapi())
        {
            logger.LogInformation("Using VAAPI (h264_vaapi) to transcode {input}", input);
            return StartVaapiTranscode(input, video, audio, audioCompatible, burnSub, tempDir, startTime, ct);
        }

        logger.LogInformation("Using CPU (libx264) to transcode {input}", input);
        return StartCpuTranscode(input, video, audio, audioCompatible, burnSub, tempDir, startTime, ct);
    }

    private bool NvencAvailable()
    {
        if (_nvencAvailable.HasValue) return _nvencAvailable.Value;
        lock (_nvencLock)
        {
            if (_nvencAvailable.HasValue) return _nvencAvailable.Value;
            _nvencAvailable = ProbeNvenc();
            if (_nvencAvailable == false)
                logger.LogWarning("h264_nvenc is not usable on this host; trying VAAPI, then CPU transcoding");
            return _nvencAvailable.Value;
        }
    }

    private bool VaapiEncodeAvailable()
    {
        if (_vaapiEncodeAvailable.HasValue) return _vaapiEncodeAvailable.Value;
        lock (_vaapiLock)
        {
            if (_vaapiEncodeAvailable.HasValue) return _vaapiEncodeAvailable.Value;
            _vaapiEncodeAvailable = ProbeVaapiEncode();
            if (_vaapiEncodeAvailable == false)
                logger.LogWarning("h264_vaapi encode is not usable on this host; falling back to CPU transcoding");
            return _vaapiEncodeAvailable.Value;
        }
    }

    // Virtual so tests can force specific encoder-selection paths.
    protected virtual bool UseNvenc() => NvencAvailable();
    protected virtual bool UseVaapi() => VaapiEncodeAvailable();

    private bool ProbeNvenc()
    {
        try
        {
            var psi = NewProcessStartInfo(options.Value.FfmpegPath, new List<string>
            {
                "-hide_banner", "-loglevel", "error",
                "-f", "lavfi", "-i", "testsrc=size=320x240:rate=10:duration=1",
                "-c:v", "h264_nvenc", "-f", "null", "-"
            });
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            if (!proc.WaitForExit(8000))
            {
                TryKill(proc);
                proc.WaitForExit(3000);
                return false;
            }
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private bool ProbeVaapiEncode()
    {
        try
        {
            var psi = NewProcessStartInfo(options.Value.FfmpegPath, new List<string>
            {
                "-hide_banner", "-loglevel", "error",
                "-vaapi_device", options.Value.VaapiDevice,
                "-f", "lavfi", "-i", "testsrc=size=320x240:rate=10:duration=1",
                "-vf", "format=nv12,hwupload",
                "-c:v", "h264_vaapi", "-frames:v", "3", "-f", "null", "-"
            });
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            if (!proc.WaitForExit(8000))
            {
                TryKill(proc);
                proc.WaitForExit(3000);
                return false;
            }
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private Process StartNvencTranscode(
        string input,
        ProbeVideoStream video,
        ProbeAudioStream? audio,
        bool audioCompatible,
        BurnInTarget? burnSub,
        string tempDir,
        double? startTime,
        CancellationToken ct)
    {
        var ffmpeg = options.Value.FfmpegPath;
        var playlist = Path.Combine(tempDir, "enc.m3u8");
        var segPattern = Path.Combine(tempDir, "%d.ts");

        var args = new List<string>();
        // Decode on NVDEC: CPU decode (10-bit HEVC in particular) is what
        // keeps the transcode head from outrunning seeks. Frames download to
        // system memory automatically for the filter graph, and sources
        // NVDEC rejects fall back to the CPU pipeline via the caller.
        args.Add("-hwaccel"); args.Add("cuda");
        // Input seek before -i for fast seek
        if (startTime is > 0)
        {
            args.Add("-ss"); args.Add(startTime.Value.ToString(CultureInfo.InvariantCulture));
        }
        args.Add("-i"); args.Add(input);

        // NVENC H.264 only accepts 8-bit yuv420p in system memory. Downconvert
        // explicitly (handles 10-bit HEVC/x265 sources) after any subtitle burn-in.
        AppendVideoPipeline(args, video, audio, burnSub, tailFilter: "format=yuv420p");

        args.Add("-c:v"); args.Add("h264_nvenc");
        args.Add("-preset"); args.Add("p4");
        args.Add("-b:v"); args.Add("6M");
        args.Add("-maxrate"); args.Add("8M");
        args.Add("-bufsize"); args.Add("12M");
        // Lock keyframes to the playlist's segment grid (NVENC only honours
        // forced keyframes when forced-idr is on) so the HLS muxer cuts every
        // segment there: real durations stay within a frame of the declared
        // EXTINF values hls.js maps seeks through.
        args.Add("-forced-idr"); args.Add("1");
        args.Add("-force_key_frames"); args.Add(SegmentKeyFrameExpr);

        args.Add("-c:a");
        args.Add(audioCompatible ? "copy" : "aac");
        if (!audioCompatible)
        {
            args.Add("-b:a"); args.Add("160k");
            args.Add("-ac"); args.Add("2");
        }

        AppendHlsArgs(args, playlist, segPattern);

        var psi = NewProcessStartInfo(ffmpeg, args);
        var proc = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg start failed");
        ct.Register(() => TryKill(proc));
        return proc;
    }

    private Process StartCpuTranscode(
        string input,
        ProbeVideoStream video,
        ProbeAudioStream? audio,
        bool audioCompatible,
        BurnInTarget? burnSub,
        string tempDir,
        double? startTime,
        CancellationToken ct)
    {
        var ffmpeg = options.Value.FfmpegPath;
        var playlist = Path.Combine(tempDir, "enc.m3u8");
        var segPattern = Path.Combine(tempDir, "%d.ts");

        var args = new List<string>();
        // Input seek before -i for fast seek
        if (startTime is > 0)
        {
            args.Add("-ss"); args.Add(startTime.Value.ToString(CultureInfo.InvariantCulture));
        }
        args.Add("-i"); args.Add(input);

        // Purely software pipeline. format=yuv420p keeps 10-bit HEVC/x265
        // sources compatible with libx264 and MPEG-TS, after any burn-in.
        AppendVideoPipeline(args, video, audio, burnSub, tailFilter: "format=yuv420p");

        args.Add("-c:v"); args.Add("libx264");
        args.Add("-preset"); args.Add("veryfast");
        args.Add("-crf"); args.Add("20");
        // Lock keyframes to the playlist's segment grid; x264's scenecut
        // I-frames would otherwise make the HLS muxer cut segments early and
        // desynchronise seeks from the declared EXTINF positions.
        args.Add("-x264-params"); args.Add("scenecut=0");
        args.Add("-force_key_frames"); args.Add(SegmentKeyFrameExpr);

        args.Add("-c:a");
        args.Add(audioCompatible ? "copy" : "aac");
        if (!audioCompatible)
        {
            args.Add("-b:a"); args.Add("160k");
            args.Add("-ac"); args.Add("2");
        }

        AppendHlsArgs(args, playlist, segPattern);

        var psi = NewProcessStartInfo(ffmpeg, args);
        var proc = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg start failed");
        ct.Register(() => TryKill(proc));
        return proc;
    }

    private static void AppendHlsArgs(List<string> args, string playlist, string segPattern)
    {
        args.Add("-f"); args.Add("hls");
        args.Add("-hls_time"); args.Add(SegmentSeconds.ToString("0", CultureInfo.InvariantCulture));
        args.Add("-hls_list_size"); args.Add("0");
        args.Add("-hls_playlist_type"); args.Add("event");
        args.Add("-hls_segment_type"); args.Add("mpegts");
        args.Add("-hls_segment_filename"); args.Add(segPattern);
        args.Add("-hls_flags"); args.Add("independent_segments+temp_file");
        args.Add(playlist);
    }

    private static ProcessStartInfo NewProcessStartInfo(string ffmpeg, List<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.Environment["LIBVA_DRIVER_NAME"] = "radeonsi";
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    private static void TryKill(Process proc)
    {
        try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
    }

    public (string Path, string ContentType)? GetPlaylistPath(string sessionId)
    {
        if (store.Get(sessionId) is not { } session) return null;
        var playlist = Path.Combine(session.TempDir, "playlist.m3u8");
        if (!System.IO.File.Exists(playlist)) return null;
        return (playlist, "application/vnd.apple.mpegurl");
    }

    public (string Path, string ContentType)? GetSegmentPath(string sessionId, string fileName)
    {
        if (store.Get(sessionId) is not { } session) return null;
        if (!IsValidSegmentName(fileName)) return null;
        var path = Path.Combine(session.TempDir, fileName);
        if (!System.IO.File.Exists(path)) return null;
        var contentType = fileName.EndsWith(".ts")
            ? "video/mp2t"
            : fileName.EndsWith(".m4s")
                ? "video/iso.segment"
                : fileName.EndsWith(".mp4")
                    ? "video/mp4"
                    : fileName.EndsWith(".vtt")
                        ? "text/vtt; charset=utf-8"
                        : "application/octet-stream";
        return (path, contentType);
    }

    private static bool IsValidSegmentName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (name.Contains('/') || name.Contains('\\')) return false;
        if (name.Contains("..")) return false;
        var ext = Path.GetExtension(name).ToLowerInvariant();
        return ext is ".ts" or ".m4s" or ".mp4" or ".vtt" or ".m3u8";
    }
}
