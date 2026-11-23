using System.Diagnostics;
using System.Text.Json;
using HomeTheatre.Models;
using Microsoft.Extensions.Options;

namespace HomeTheatre.Services;

public interface IProbeService
{
    Task<ProbeResponse> ProbeAsync(string fullPath, CancellationToken cancellationToken);
}

public class ProbeService(IOptions<VideoOptions> options, ILogger<ProbeService> logger) : IProbeService
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private static readonly HashSet<string> SoftSubtitleCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "subrip", "srt", "ass", "ssa", "webvtt", "mov_text", "vtt"
    };

    public async Task<ProbeResponse> ProbeAsync(string fullPath, CancellationToken cancellationToken)
    {
        var ffprobe = options.Value.FfprobePath;
        var psi = new ProcessStartInfo
        {
            FileName = ffprobe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-print_format");
        psi.ArgumentList.Add("json");
        psi.ArgumentList.Add("-show_streams");
        psi.ArgumentList.Add("-show_format");
        psi.ArgumentList.Add(fullPath);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start ffprobe");

        // Read stdout/stderr concurrently to avoid deadlocking when the
        // OS pipe buffer fills before WaitForExit completes.
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = proc.StandardError.ReadToEndAsync(cancellationToken);
        await proc.WaitForExitAsync(cancellationToken);

        var json = await stdoutTask;
        var stderr = await stderrTask;

        if (proc.ExitCode != 0)
        {
            logger.LogError("ffprobe failed ({code}): {err}", proc.ExitCode, stderr);
            throw new InvalidOperationException($"ffprobe exited {proc.ExitCode}");
        }
        using var doc = JsonDocument.Parse(json);

        var video = new List<ProbeVideoStream>();
        var audio = new List<ProbeAudioStream>();
        var subs = new List<ProbeSubtitleStream>();

        if (doc.RootElement.TryGetProperty("streams", out var streamsEl))
        {
            foreach (var s in streamsEl.EnumerateArray())
            {
                var codec = s.GetProperty("codec_type").GetString() ?? "";
                var idx = s.GetProperty("index").GetInt32();
                var codecName = s.GetProperty("codec_name").GetString() ?? "";
                var lang = s.TryGetProperty("tags", out var t) && t.TryGetProperty("language", out var l)
                    ? l.GetString() : null;
                var title = s.TryGetProperty("tags", out var t2) && t2.TryGetProperty("title", out var ti)
                    ? ti.GetString() : null;
                var isDefault = s.TryGetProperty("disposition", out var d) && d.TryGetProperty("default", out var dd) && dd.GetInt32() == 1;

                if (codec == "video")
                {
                    var width = s.TryGetProperty("width", out var w) ? w.GetInt32() : (int?)null;
                    var height = s.TryGetProperty("height", out var h) ? h.GetInt32() : (int?)null;
                    var colorRange = s.TryGetProperty("color_range", out var cr) ? cr.GetString() : null;
                    video.Add(new ProbeVideoStream(idx, codecName, width, height, colorRange));
                }
                else if (codec == "audio")
                {
                    audio.Add(new ProbeAudioStream(idx, codecName, lang, title, isDefault));
                }
                else if (codec == "subtitle")
                {
                    var canVtt = SoftSubtitleCodecs.Contains(codecName);
                    var isForced = s.TryGetProperty("disposition", out var sd) && sd.TryGetProperty("forced", out var forced) && forced.GetInt32() == 1;
                    var isHearingImpaired = s.TryGetProperty("disposition", out var hd) && hd.TryGetProperty("hearing_impaired", out var hearing) && hearing.GetInt32() == 1;
                    subs.Add(new ProbeSubtitleStream(idx, codecName, lang, title, isDefault, canVtt, isForced, isHearingImpaired));
                }
            }
        }

        var duration = 0.0;
        if (doc.RootElement.TryGetProperty("format", out var fmt) &&
            fmt.TryGetProperty("duration", out var durEl) &&
            double.TryParse(durEl.GetString(), out var parsedDuration))
        {
            duration = parsedDuration;
        }

        var container = Path.GetExtension(fullPath).TrimStart('.').ToLowerInvariant();

        return new ProbeResponse(container, duration, video, audio, subs);
    }
}
