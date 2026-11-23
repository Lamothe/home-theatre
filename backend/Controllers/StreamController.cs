using HomeTheatre.Models;
using HomeTheatre.Services;
using Microsoft.AspNetCore.Mvc;

namespace HomeTheatre.Controllers;

[ApiController]
[Route("api/stream")]
public class StreamController(
    IVideoPathResolver pathResolver,
    IProbeService probe,
    IStreamService streamService,
    IStreamSessionStore store,
    IMediaLibraryService mediaLibrary,
    ITmdbService tmdb,
    ILogger<StreamController> logger) : ControllerBase
{
    [HttpGet("library")]
    public IActionResult Library()
    {
        var result = mediaLibrary.Scan();
        return Ok(result);
    }

    [HttpGet("episodes/{**encodedPath}")]
    public IActionResult Episodes(string? encodedPath, [FromQuery] int? season)
    {
        if (!pathResolver.TryResolve(encodedPath, out var full, out _))
            return BadRequest(new { error = "Invalid path" });

        if (!Directory.Exists(full))
            return NotFound(new { error = "Directory not found" });

        var episodes = mediaLibrary.GetEpisodes(full, season);
        return Ok(new { episodes });
    }

    [HttpGet("poster/{**encodedPath}")]
    public async Task<IActionResult> Poster(string? encodedPath, CancellationToken cancellationToken)
    {
        if (!pathResolver.TryResolve(encodedPath, out var full, out _))
            return NotFound();

        var (title, year, isSeries, season) = ClassifyPath(full);

        if (string.IsNullOrEmpty(title))
            return NotFound();

        // Cache path: alongside the media in a .tmdb folder.
        // For folders (movie folder / series): cache inside the folder itself.
        // For standalone files: cache in the parent's .tmdb/ with a unique
        // filename derived from the item name, so multiple movies in the same
        // directory don't collide.
        string posterFile;
        if (Directory.Exists(full))
        {
            var tmdbDir = Path.Combine(full, ".tmdb");
            posterFile = season.HasValue
                ? Path.Combine(tmdbDir, $"season-{season}.jpg")
                : Path.Combine(tmdbDir, "poster.jpg");
        }
        else
        {
            // Standalone file — use a sanitised filename for the cache
            var cacheDir = Path.GetDirectoryName(full)!;
            var cacheKey = Path.GetFileNameWithoutExtension(full);
            foreach (var c in Path.GetInvalidFileNameChars())
                cacheKey = cacheKey.Replace(c, '_');
            var tmdbDir = Path.Combine(cacheDir, ".tmdb");
            posterFile = Path.Combine(tmdbDir, $"{cacheKey}.jpg");
        }

        // Serve from cache if available
        if (System.IO.File.Exists(posterFile))
        {
            return PhysicalFile(posterFile, "image/jpeg");
        }

        // Fetch from TMDB
        try
        {
            var posterUrl = await tmdb.GetPosterUrlAsync(title, isSeries, year, season);
            if (string.IsNullOrEmpty(posterUrl))
                return NotFound();

            var imageBytes = await tmdb.DownloadPosterAsync(posterUrl, cancellationToken);
            if (imageBytes is null)
                return NotFound();

            Directory.CreateDirectory(Path.GetDirectoryName(posterFile)!);
            await System.IO.File.WriteAllBytesAsync(posterFile, imageBytes, cancellationToken);

            return File(imageBytes, "image/jpeg");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fetch poster for {title}", title);
            return NotFound();
        }
    }

    private (string Title, int? Year, bool IsSeries, int? Season) ClassifyPath(string fullPath)
    {
        var root = pathResolver.Root;

        if (fullPath.StartsWith(root + Path.DirectorySeparatorChar))
        {
            var rel = fullPath[(root.Length + 1)..];
            var parts = rel.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length >= 2 && parts[0] == "Series")
            {
                // Series/<series name>[/<season folder>]
                var seriesName = parts[1];
                var (title, year) = ParseTitleAndYear(seriesName);

                int? season = null;
                if (parts.Length >= 3)
                {
                    var seasonMatch = System.Text.RegularExpressions.Regex.Match(parts[2], @"Season\s*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (seasonMatch.Success)
                        season = int.Parse(seasonMatch.Groups[1].Value);
                    else
                    {
                        // No season folder — try to detect from episode filenames
                        season = DetectSeasonFromFiles(fullPath);
                    }
                }
                else
                {
                    season = DetectSeasonFromFiles(fullPath);
                }

                return (title, year, true, season);
            }

            if (parts.Length >= 2 && parts[0] == "Movies")
            {
                var name = parts[1];
                // If it's a file, strip extension
                if (System.IO.File.Exists(fullPath))
                    name = Path.GetFileNameWithoutExtension(name);
                var (title, year) = ParseTitleAndYear(name);
                return (title, year, false, null);
            }
        }

        return ("", null, false, null);
    }

    private static int? DetectSeasonFromFiles(string folder)
    {
        try
        {
            var match = System.Text.RegularExpressions.Regex.Match(
                string.Join(' ', Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Take(50)),
                @"[Ss](\d{1,2})[Ee]\d{1,2}");
            if (match.Success)
                return int.Parse(match.Groups[1].Value);
        }
        catch { }
        return 1;
    }

    private static (string Title, int? Year) ParseTitleAndYear(string name)
    {
        var match = System.Text.RegularExpressions.Regex.Match(name, @"\b(19\d{2}|20[0-3]\d)\b");
        if (!match.Success)
            return (CleanTitle(name), null);

        var year = int.Parse(match.Value);
        var titlePart = name[..match.Index].Trim();
        return (CleanTitle(titlePart), year);
    }

    private static string CleanTitle(string s)
    {
        var t = s.Replace('.', ' ').Replace('_', ' ');
        t = t.TrimEnd(' ', '(', '[', ':', '-', '.');
        t = System.Text.RegularExpressions.Regex.Replace(t, @"\s+", " ").Trim();
        return t;
    }
    [HttpGet("list/{**encodedPath}")]
    public IActionResult List(string? encodedPath)
    {
        if (!pathResolver.TryResolve(encodedPath, out var full, out var rel))
            return BadRequest(new { error = "Invalid path" });

        if (!Directory.Exists(full))
            return NotFound(new { error = "Directory not found" });

        var entries = new List<EntryDto>();

        foreach (var dir in SafeEnumerate(full, directories: true))
        {
            var name = Path.GetFileName(dir);
            entries.Add(new EntryDto(name, IPathResolver.EncodeSegment(name), true, null, null));
        }

        foreach (var file in SafeEnumerate(full, directories: false))
        {
            try
            {
                var info = new FileInfo(file);
                entries.Add(new EntryDto(info.Name, IPathResolver.EncodeSegment(info.Name), false, info.Length, info.LastWriteTimeUtc));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to stat {file}", file);
            }
        }

        entries.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        return Ok(new
        {
            path = rel,
            parent = IPathResolver.ParentOf(encodedPath ?? ""),
            entries,
        });
    }

    private static IEnumerable<string> SafeEnumerate(string path, bool directories)
    {
        try
        {
            return directories
                ? Directory.EnumerateDirectories(path).ToArray()
                : Directory.EnumerateFiles(path).ToArray();
        }
        catch (UnauthorizedAccessException) { return []; }
        catch (DirectoryNotFoundException) { return []; }
        catch (IOException) { return []; }
    }

    private record EntryDto(string Name, string EncodedName, bool IsDirectory, long? Size, DateTime? ModifiedAt);

    [HttpGet("probe/{**encodedPath}")]
    public async Task<IActionResult> Probe(string? encodedPath, CancellationToken cancellationToken)
    {
        if (!pathResolver.TryResolve(encodedPath, out var full, out _))
            return BadRequest(new { error = "Invalid path" });
        if (!System.IO.File.Exists(full))
            return NotFound(new { error = "File not found" });

        try
        {
            var info = await probe.ProbeAsync(full, cancellationToken);
            return Ok(info);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Probe failed for {path}", full);
            return StatusCode(500, new { error = "Probe failed" });
        }
    }

    [HttpPost("start")]
    public async Task<IActionResult> Start([FromBody] StartStreamRequest req, CancellationToken cancellationToken)
    {
        if (!pathResolver.TryResolve(req.EncodedPath, out var full, out _))
            return BadRequest(new { error = "Invalid path" });
        if (!System.IO.File.Exists(full))
            return NotFound(new { error = "File not found" });

        try
        {
            var resp = await streamService.StartAsync(
                full,
                req.AudioStreamIndex,
                req.SubtitleStreamIndex,
                req.StartTimeSeconds,
                cancellationToken);
            return Ok(resp);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Stream start failed for {path}", full);
            // Surface the failure detail (e.g. the ffmpeg error tail StreamService
            // embeds in its exception) so the client message is actionable.
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpGet("{sessionId}/playlist.m3u8")]
    public IActionResult Playlist(string sessionId)
    {
        var result = streamService.GetPlaylistPath(sessionId);
        if (result is null) return NotFound();
        Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
        return StreamFile(result.Value.Path, result.Value.ContentType);
    }

    [HttpGet("{sessionId}/{fileName}")]
    public IActionResult Segment(string sessionId, string fileName)
    {
        var result = streamService.GetSegmentPath(sessionId, fileName);
        if (result is null) return NotFound();
        return StreamFile(result.Value.Path, result.Value.ContentType);
    }

    private IActionResult StreamFile(string path, string contentType)
    {
        // Stream with FileShare.ReadWrite so we don't conflict with ffmpeg
        // still writing the file. No Content-Length — the file may still be
        // growing, so chunked encoding is used instead.
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return File(stream, contentType);
    }

    [HttpGet("{sessionId}/head")]
    public IActionResult Head(string sessionId)
    {
        var head = streamService.GetTimelineHeadSeconds(sessionId);
        if (head is null) return NotFound();
        return Ok(new StreamHeadResponse(head.Value));
    }

    [HttpPost("{sessionId}/heartbeat")]
    public IActionResult Heartbeat(string sessionId)
    {
        if (store.Get(sessionId) is null) return NotFound();
        store.Touch(sessionId);
        return Ok(new HeartbeatResponse(true));
    }

    [HttpPost("{sessionId}/stop")]
    public IActionResult Stop(string sessionId)
    {
        if (store.TryRemove(sessionId, out var session) && session != null)
        {
            StreamLifecycle.Kill(store, sessionId, session, logger);
        }
        return NoContent();
    }
}
