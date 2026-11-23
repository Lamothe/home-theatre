namespace HomeTheatre.Models;

public sealed class VideoOptions
{
    public string VideoRoot { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Videos");
    public string TmdbApiKey { get; set; } = "";
    public string FfmpegPath { get; set; } = "ffmpeg";
    public string FfprobePath { get; set; } = "ffprobe";
    public string VaapiDevice { get; set; } = "/dev/dri/renderD128";
    public string StreamTempRoot { get; set; } = Path.Combine(Path.GetTempPath(), "home-theatre");
    public int StreamSessionTtlMinutes { get; set; } = 15;
}
