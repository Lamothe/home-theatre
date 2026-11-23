using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using HomeTheatre.Models;

namespace HomeTheatre.Services;

public interface IPathResolver
{
    string Root { get; }
    bool TryResolve(string? encodedPath, out string fullPath, out string relativePath);
    static string EncodeSegment(string s) =>
        WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(s));
    static string ParentOf(string? rel)
    {
        if (string.IsNullOrEmpty(rel)) return "";
        var idx = rel.LastIndexOf('/');
        return idx < 0 ? "" : rel[..idx];
    }
}

public abstract class PathResolverBase(string root)
{
    protected readonly string _root = NormalizeRoot(root);

    public string Root => _root;

    public bool TryResolve(string? encodedPath, out string fullPath, out string relativePath)
    {
        fullPath = "";
        relativePath = "";

        if (string.IsNullOrEmpty(encodedPath))
        {
            fullPath = _root;
            relativePath = "";
            return true;
        }

        var segments = encodedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var decoded = new List<string>(segments.Length);
        foreach (var seg in segments)
        {
            try
            {
                var bytes = WebEncoders.Base64UrlDecode(seg);
                var name = Encoding.UTF8.GetString(bytes);
                if (string.IsNullOrEmpty(name) || name.Contains('\0'))
                    return false;
                decoded.Add(name);
            }
            catch
            {
                return false;
            }
        }

        var rel = string.Join('/', decoded);
        var combined = Path.GetFullPath(Path.Combine(_root, rel));

        if (!IsUnderRoot(combined))
            return false;

        fullPath = combined;
        relativePath = rel;
        return true;
    }

    private bool IsUnderRoot(string combined)
    {
        if (string.Equals(combined, _root, StringComparison.Ordinal))
            return true;

        var prefix = _root + Path.DirectorySeparatorChar;
        return combined.StartsWith(prefix, StringComparison.Ordinal);
    }

    private static string NormalizeRoot(string? root)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new InvalidOperationException("Path root is not configured.");
        var full = Path.GetFullPath(root);
        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}

public interface IVideoPathResolver : IPathResolver { }

public class VideoPathResolver(IOptions<VideoOptions> options) : PathResolverBase(options.Value.VideoRoot), IVideoPathResolver { }
