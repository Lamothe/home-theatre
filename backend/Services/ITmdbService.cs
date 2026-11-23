namespace HomeTheatre.Services;

public interface ITmdbService
{
    Task<string?> GetPosterUrlAsync(string title, bool isSeries, int? year = null, int? season = null);
    Task<byte[]?> DownloadPosterAsync(string posterUrl, CancellationToken cancellationToken = default);
    Task FetchPoster(string outputFileName, string showTitle, bool isSeries, int? seasonNumber = null);
}
