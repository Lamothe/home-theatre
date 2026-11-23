using System.Text.RegularExpressions;

namespace HomeTheatre.Services;

public static class EpisodeTitleParser
{
    // Matches "S01E02", "s04e01", "S3E9" etc. anchored anywhere in the name.
    private static readonly Regex SeasonEpisodeRegex = new(
        @"[Ss](\d{1,2})[Ee](\d{1,3})",
        RegexOptions.Compiled);

    // Tokens that end the descriptive episode title. Everything after the
    // SxxExx marker up to the first of these is the episode name; the rest of
    // the line is release metadata. An opening bracket/paren is a hard stop
    // ("...Destiny (1080p HULU...)"), as is a resolution tag
    // ("...The Black Rabbits 1080p NF..."). In release naming the source word
    // ("web", "netflix", "amzn"...) always follows the resolution, so we rely
    // on the resolution/bracket rather than those words — source words can
    // legitimately begin a descriptive title and must not truncate it.
    private static readonly Regex TitleEndRegex = new(
        @"[\(\[\{]|\b(?:[0-9]{3,4}p|4k|2160p|1080p|720p|480p|uhd)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Separator / noise characters that may surround the title text.
    private static readonly Regex SeparatorsRegex = new(@"[\._\-]+", RegexOptions.Compiled);
    private static readonly Regex WhitespaceRegex = new(@"\s{2,}", RegexOptions.Compiled);

    /// <summary>Parses the season, episode and a clean episode title from a
    /// release-style filename. Returns nulls for fields that couldn't be
    /// confidently determined.</summary>
    public static EpisodeTitle Parse(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return EpisodeTitle.None;

        // Remove the container extension so ".mkv"/".mp4" never leaks into the title.
        var stem = Regex.Replace(fileName, @"\.[A-Za-z0-9]{2,4}$", "");
        var match = SeasonEpisodeRegex.Match(stem);
        if (!match.Success) return EpisodeTitle.None;

        var season = int.Parse(match.Groups[1].Value);
        var episode = int.Parse(match.Groups[2].Value);

        // Everything between the SxxExx marker and the first title-end token is
        // the descriptive episode title. Trim leading separators.
        var after = stem[(match.Index + match.Length)..];
        var titleEnd = TitleEndRegex.Match(after);
        var rawTitle = titleEnd.Success ? after[..titleEnd.Index] : after;

        var title = Clean(rawTitle);

        return new EpisodeTitle(season, episode, title);
    }

    private static string? Clean(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var s = raw;
        // Trim leading/trailing separator & bracket noise.
        s = s.Trim(' ', '.', '_', '-', '(', ')', '[', ']', '{', '}');

        // Inner separator chars (dots/underscores/hyphens between title words)
        // become spaces; collapse runs.
        s = SeparatorsRegex.Replace(s, " ");
        s = WhitespaceRegex.Replace(s, " ").Trim();

        if (s.Length == 0) return null;

        // First letter capitalised; the rest kept verbatim (mixes of case in
        // release names are common and lower-casing can mangle proper nouns).
        return char.ToUpperInvariant(s[0]) + s[1..];
    }
}

public readonly record struct EpisodeTitle(int? Season, int? Episode, string? Title)
{
    public static EpisodeTitle None => new(null, null, null);
}
