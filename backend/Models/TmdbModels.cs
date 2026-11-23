using System.Text.Json.Serialization;

namespace HomeTheatre.Models;

public class TmdbSearchResponse
{
    [JsonPropertyName("results")]
    public List<TmdbResult> Results { get; set; } = [];
}

public class TmdbResult
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("poster_path")]
    public string? PosterPath { get; set; }
}

public class TmdbSeasonResponse
{
    [JsonPropertyName("poster_path")]
    public string? PosterPath { get; set; }
}