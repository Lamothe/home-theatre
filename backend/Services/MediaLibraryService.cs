using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using HomeTheatre.Models;
using Microsoft.Extensions.Options;

namespace HomeTheatre.Services;

public interface IMediaLibraryService
{
    LibraryScanResult Scan();
    List<EpisodeInfo> GetEpisodes(string fullPath, int? season = null);
}

public record LibraryScanResult(List<LibraryMovie> Movies, List<LibrarySeason> Seasons);
public record LibraryMovie(string Title, int? Year, string EncodedPath, string PlayPath, string? PosterUrl);
public record LibrarySeason(string Title, int? Year, int SeasonNumber, string EncodedPath, string? PosterUrl);
public record EpisodeInfo(
    int? Season,
    int? Episode,
    string? Title,
    string FileName,
    long Size,
    string EncodedPath);

public class MediaLibraryService(
    IVideoPathResolver pathResolver) : IMediaLibraryService
{
    private static readonly Regex YearRegex = new(@"\b(19\d{2}|20[0-3]\d)\b", RegexOptions.Compiled);
    private static readonly Regex SeasonEpisodeRegex = new(@"[Ss](\d{1,2})[Ee]\d{1,2}", RegexOptions.Compiled);
    private static readonly HashSet<string> VideoExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".mov", ".m4v", ".webm", ".ogv", ".ts", ".wmv", ".flv"
    };

    public LibraryScanResult Scan()
    {
        var root = pathResolver.Root;
        var movies = new List<LibraryMovie>();
        var seasons = new List<LibrarySeason>();

        var moviesDir = Path.Combine(root, "Movies");
        var seriesDir = Path.Combine(root, "Series");

        if (Directory.Exists(moviesDir))
        {
            foreach (var movie in ScanMovies(moviesDir))
                movies.Add(movie);
        }

        if (Directory.Exists(seriesDir))
        {
            foreach (var season in ScanSeries(seriesDir))
                seasons.Add(season);
        }

        movies.Sort((a, b) =>
        {
            var title = string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase);
            return title != 0 ? title : Nullable.Compare(a.Year, b.Year);
        });

        seasons.Sort((a, b) =>
        {
            var title = string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase);
            return title != 0 ? title : a.SeasonNumber.CompareTo(b.SeasonNumber);
        });

        return new LibraryScanResult(movies, seasons);
    }

    private List<LibraryMovie> ScanMovies(string moviesDir)
    {
        var result = new List<LibraryMovie>();

        foreach (var entry in SafeEnumerate(moviesDir))
        {
            string? name;
            string? videoPath = null;
            string encodedPath;

            if (Directory.Exists(entry))
            {
                // Skip hidden / cache folders
                if (Path.GetFileName(entry).StartsWith('.')) continue;

                name = Path.GetFileName(entry);
                encodedPath = EncodeRelativePath("Movies", name);
                videoPath = FindVideoFile(entry);

                if (videoPath == null) continue;

                // Play path points to the actual video file inside the folder
                var playPath = encodedPath + "/" + IPathResolver.EncodeSegment(Path.GetFileName(videoPath));
                var (title, year) = ParseTitleAndYear(name);
                var posterUrl = $"/api/stream/poster/{encodedPath}";
                result.Add(new LibraryMovie(title, year, encodedPath, playPath, posterUrl));
            }
            else if (IsVideoFile(entry))
            {
                name = Path.GetFileNameWithoutExtension(entry);
                encodedPath = EncodeRelativePath("Movies", Path.GetFileName(entry));
                videoPath = entry;

                var (title, year) = ParseTitleAndYear(name);
                var posterUrl = $"/api/stream/poster/{encodedPath}";
                result.Add(new LibraryMovie(title, year, encodedPath, encodedPath, posterUrl));
            }
        }

        return result;
    }

    private List<LibrarySeason> ScanSeries(string seriesDir)
    {
        var result = new List<LibrarySeason>();

        foreach (var seriesFolder in SafeEnumerate(seriesDir).Where(Directory.Exists))
        {
            if (Path.GetFileName(seriesFolder).StartsWith('.')) continue;

            var seriesName = Path.GetFileName(seriesFolder);
            var (seriesTitle, seriesYear) = ParseTitleAndYear(seriesName);
            var seasons = DetectSeasons(seriesFolder);

            foreach (var (seasonNum, seasonPath, _) in seasons)
            {
                var seasonFolderName = Path.GetFileName(seasonPath);
                var seasonEncoded = EncodeRelativePath("Series", seriesName, seasonFolderName);
                var posterUrl = $"/api/stream/poster/{seasonEncoded}";
                result.Add(new LibrarySeason(seriesTitle, seriesYear, seasonNum, seasonEncoded, posterUrl));
            }
        }

        return result;
    }

    private List<(int SeasonNum, string Path, string DisplayName)> DetectSeasons(string seriesFolder)
    {
        var seasons = new List<(int, string, string)>();
        var seriesName = Path.GetFileName(seriesFolder);

        // 1. Look for "Season N" subfolders
        var seasonFolders = SafeEnumerate(seriesFolder)
            .Where(Directory.Exists)
            .Select(d => (Path: d, Name: Path.GetFileName(d)))
            .Where(x => !x.Name.StartsWith('.'))
            .ToList();

        foreach (var sf in seasonFolders)
        {
            var seasonMatch = Regex.Match(sf.Name, @"Season\s*(\d+)", RegexOptions.IgnoreCase);
            if (seasonMatch.Success)
            {
                var num = int.Parse(seasonMatch.Groups[1].Value);
                seasons.Add((num, sf.Path, sf.Name));
            }
        }

        if (seasons.Count > 0) return seasons;

        // 2. No season folders — scan filenames for SxxExx pattern
        var allFiles = GetAllVideoFiles(seriesFolder);
        var seasonGroups = new Dictionary<int, List<string>>();

        foreach (var file in allFiles)
        {
            var match = SeasonEpisodeRegex.Match(Path.GetFileName(file));
            var seasonNum = match.Success ? int.Parse(match.Groups[1].Value) : 1;
            if (!seasonGroups.TryGetValue(seasonNum, out var list))
            {
                list = [];
                seasonGroups[seasonNum] = list;
            }
            list.Add(file);
        }

        if (seasonGroups.Count == 0 && allFiles.Count > 0)
        {
            // No SxxExx pattern at all — treat as Season 1
            seasonGroups[1] = allFiles;
        }

        foreach (var (num, _) in seasonGroups.OrderBy(x => x.Key))
        {
            // Use the series folder itself as the season path
            seasons.Add((num, seriesFolder, seriesName));
        }

        return seasons;
    }

    public List<EpisodeInfo> GetEpisodes(string fullPath, int? season = null)
    {
        var result = new List<EpisodeInfo>();

        // fullPath is the season folder (or series folder if no season subfolders)
        var files = GetAllVideoFiles(fullPath);

        foreach (var file in files)
        {
            var fileName = Path.GetFileName(file);
            // Compute path relative to VideoRoot so it includes Series/<name>/<season>/<file>
            var relPath = Path.GetRelativePath(pathResolver.Root, fullPath);
            var encodedPath = string.Join('/', relPath
                .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
                .Select(IPathResolver.EncodeSegment)) + "/" + IPathResolver.EncodeSegment(fileName);

            long size = 0;
            try { size = new FileInfo(file).Length; } catch { }

            var parsed = EpisodeTitleParser.Parse(fileName);

            // If a specific season was requested (a "Season 2" card pointing at a
            // shared series folder), keep only files that match it. Files that
            // don't carry an SxxExx marker can't be attributed to a season, so
            // they're only shown when no season filter applies.
            if (season is not null && parsed.Season is not null && parsed.Season != season)
                continue;

            result.Add(new EpisodeInfo(
                parsed.Season ?? season,
                parsed.Episode,
                parsed.Title,
                fileName,
                size,
                encodedPath));
        }

        // Order by season, then episode number, then by name so genuinely distinct
        // files (e.g. duplicate releases of the same episode) group together.
        return result
            .OrderBy(e => e.Season ?? int.MaxValue)
            .ThenBy(e => e.Episode ?? int.MaxValue)
            .ThenBy(e => e.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? FindVideoFile(string dir)
    {
        foreach (var f in SafeEnumerateFiles(dir))
        {
            if (IsVideoFile(f)) return f;
        }
        // Check one level of subfolders
        foreach (var sub in SafeEnumerate(dir).Where(Directory.Exists))
        {
            if (Path.GetFileName(sub).StartsWith('.')) continue;
            foreach (var f in SafeEnumerateFiles(sub))
            {
                if (IsVideoFile(f)) return f;
            }
        }
        return null;
    }

    private static List<string> GetAllVideoFiles(string folder)
    {
        var result = new List<string>();

        // Direct files
        result.AddRange(SafeEnumerateFiles(folder).Where(IsVideoFile));

        // One level of subfolders (for messy structures like www.UIndex.org dirs)
        foreach (var sub in SafeEnumerate(folder).Where(Directory.Exists))
        {
            if (Path.GetFileName(sub).StartsWith('.')) continue;
            result.AddRange(SafeEnumerateFiles(sub).Where(IsVideoFile));
        }

        return result;
    }

    private static (string Title, int? Year) ParseTitleAndYear(string name)
    {
        var match = YearRegex.Match(name);
        if (!match.Success)
        {
            return (CleanTitle(name), null);
        }

        var year = int.Parse(match.Value);
        var titlePart = name[..match.Index].Trim();
        return (CleanTitle(titlePart), year);
    }

    private static string CleanTitle(string s)
    {
        // Replace dots and underscores with spaces
        var t = s.Replace('.', ' ').Replace('_', ' ');
        // Remove trailing punctuation: brackets, colons, dashes
        t = t.TrimEnd(' ', '(', '[', ':', '-', '.');
        // Collapse multiple spaces
        t = Regex.Replace(t, @"\s+", " ").Trim();
        return t;
    }

    private static string EncodeRelativePath(params string[] segments)
    {
        return string.Join("/", segments.Select(IPathResolver.EncodeSegment));
    }

    private static bool IsVideoFile(string path)
    {
        var ext = Path.GetExtension(path);
        return !string.IsNullOrEmpty(ext) && VideoExts.Contains(ext);
    }

    private static IEnumerable<string> SafeEnumerate(string path)
    {
        try { return Directory.EnumerateFileSystemEntries(path); }
        catch { return []; }
    }

    private static string[] SafeEnumerateFiles(string path)
    {
        try { return Directory.EnumerateFiles(path).ToArray(); }
        catch { return []; }
    }
}
