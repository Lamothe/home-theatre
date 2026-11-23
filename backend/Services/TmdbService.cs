using HomeTheatre.Models;
using Microsoft.Extensions.Options;

namespace HomeTheatre.Services.Implementation;

public class TmdbService(
    IOptions<VideoOptions> options,
    HttpClient httpClient,
    ILogger<TmdbService> logger
    ) : ITmdbService
{
    public async Task<string?> GetPosterUrlAsync(string title, bool isSeries, int? year = null, int? season = null)
    {
        try
        {
            if (!isSeries)
            {
                return await GetMoviePosterAsync(title, year);
            }

            return await GetTvShowPosterAsync(title, year, season);
        }
        catch (Exception ex)
        {
            logger.LogError("Failed to get poster URL: {message}", ex.Message);
            return null;
        }
    }

    private async Task<string?> GetMoviePosterAsync(string title, int? year)
    {
        var searchUrl = year.HasValue
            ? $"https://api.themoviedb.org/3/search/movie?api_key={options.Value.TmdbApiKey}&query={Uri.EscapeDataString(title)}&year={year.Value}"
            : $"https://api.themoviedb.org/3/search/movie?api_key={options.Value.TmdbApiKey}&query={Uri.EscapeDataString(title)}";

        var searchResponse = await httpClient.GetFromJsonAsync<TmdbSearchResponse>(searchUrl);
        var posterPath = searchResponse?.Results?.FirstOrDefault()?.PosterPath;

        if (string.IsNullOrEmpty(posterPath))
        {
            return null;
        }

        return $"https://image.tmdb.org/t/p/w500{posterPath}";
    }

    private async Task<string?> GetTvShowPosterAsync(string title, int? year, int? season)
    {
        var searchUrl = year.HasValue
            ? $"https://api.themoviedb.org/3/search/tv?api_key={options.Value.TmdbApiKey}&query={Uri.EscapeDataString(title)}&first_air_date_year={year.Value}"
            : $"https://api.themoviedb.org/3/search/tv?api_key={options.Value.TmdbApiKey}&query={Uri.EscapeDataString(title)}";

        var searchResponse = await httpClient.GetFromJsonAsync<TmdbSearchResponse>(searchUrl);
        var tvShow = searchResponse?.Results?.FirstOrDefault();

        if (tvShow == null)
        {
            return null;
        }

        // If no season specified, return show poster
        if (!season.HasValue)
        {
            if (string.IsNullOrEmpty(tvShow.PosterPath))
            {
                return null;
            }

            return $"https://image.tmdb.org/t/p/w500{tvShow.PosterPath}";
        }

        // Get season poster
        var seasonUrl = $"https://api.themoviedb.org/3/tv/{tvShow.Id}/season/{season}?api_key={options.Value.TmdbApiKey}";
        var seasonResponse = await httpClient.GetFromJsonAsync<TmdbSeasonResponse>(seasonUrl);

        var seasonPosterPath = seasonResponse?.PosterPath ?? tvShow.PosterPath;

        if (string.IsNullOrEmpty(seasonPosterPath))
        {
            return null;
        }

        return $"https://image.tmdb.org/t/p/w500{seasonPosterPath}";
    }

    public async Task<byte[]?> DownloadPosterAsync(string posterUrl, CancellationToken cancellationToken = default)
    {
        try
        {
            return await httpClient.GetByteArrayAsync(posterUrl, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError("Failed to download poster: {message}", ex.Message);
            return null;
        }
    }

    public async Task FetchPoster(string outputFileName, string showTitle, bool isSeries, int? seasonNumber = null)
    {
        var posterUrl = await GetPosterUrlAsync(showTitle, isSeries, null, seasonNumber);

        if (string.IsNullOrEmpty(posterUrl))
        {
            return;
        }

        var imageBytes = await DownloadPosterAsync(posterUrl);
        if (imageBytes is null)
        {
            return;
        }

        var directory = Path.GetDirectoryName(outputFileName);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllBytesAsync(outputFileName, imageBytes);
    }
}
