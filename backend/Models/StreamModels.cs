namespace HomeTheatre.Models;

public record ProbeResponse(
    string Container,
    double DurationSeconds,
    List<ProbeVideoStream> Video,
    List<ProbeAudioStream> Audio,
    List<ProbeSubtitleStream> Subtitles)
{
    internal double StartTimeSeconds { get; init; }
}

public record ProbeVideoStream(int Index, string Codec, int? Width, int? Height, string? ColorRange);

public record ProbeAudioStream(int Index, string Codec, string? Language, string? Title, bool IsDefault);

public record ProbeSubtitleStream(int Index, string Codec, string? Language, string? Title, bool IsDefault, bool CanDeliverAsVtt, bool IsForced = false, bool IsHearingImpaired = false)
{
    /// <summary>
    /// Codecs ffmpeg decodes to bitmap rects. The libass-based subtitles
    /// filter cannot render these ("Only text based subtitles are currently
    /// supported"), so burn-in must composite them via overlay instead.
    /// </summary>
    private static readonly HashSet<string> BitmapSubtitleCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "hdmv_pgs_subtitle", "pgssub", "dvd_subtitle", "dvdsub", "dvb_subtitle", "dvbsub", "dvb_teletext", "xsub",
    };

    public bool IsBitmap => BitmapSubtitleCodecs.Contains(Codec);
}

public record StartStreamRequest(string EncodedPath, int? AudioStreamIndex, int? SubtitleStreamIndex, double? StartTimeSeconds);

// StartOffsetSeconds is the timeline position where real content begins:
// 0 for a full-file stream (also when burn-in subtitles or a placeholder
// failure force the pipeline to cover the whole file), or the seek anchor
// when the playlist head was padded with placeholder segments.
public record StartStreamResponse(string SessionId, string PlaylistUrl, bool Transcoding, bool SubtitleBurnIn, string? SubtitleUrl, double StartOffsetSeconds = 0);

public record HeartbeatResponse(bool Alive);

// Timeline position just past the newest segment the pipeline has produced.
// Media beyond it does not exist yet; the player restarts the session there
// instead of stalling on segment requests until the transcode catches up.
public record StreamHeadResponse(double HeadSeconds);
