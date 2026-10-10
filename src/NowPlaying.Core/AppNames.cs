namespace NowPlaying.Core;

/// <summary>Turns a SourceAppUserModelId from the media session into a short, human readable app name.</summary>
public static class AppNames
{
    // Checked in order against the lower-cased id; first hit wins.
    private static readonly (string Needle, string Name)[] Known =
    [
        ("spotify", "Spotify"),
        ("msedge", "Edge"),
        ("microsoftedge", "Edge"),
        ("chrome", "Chrome"),
        ("firefox", "Firefox"),
        ("308046b0af4a39cb", "Firefox"), // Firefox's AppUserModelId
        ("brave", "Brave"),
        ("opera", "Opera"),
        ("vivaldi", "Vivaldi"),
        ("vlc", "VLC"),
        ("zunemusic", "Media Player"),
        ("zunevideo", "Movies & TV"),
        ("mediaplayer", "Media Player"),
        ("itunes", "iTunes"),
        ("applemusic", "Apple Music"),
    ];

    public static string GetFriendlyName(string? appUserModelId)
    {
        var id = appUserModelId?.Trim();
        if (string.IsNullOrEmpty(id))
        {
            return "";
        }

        var lower = id.ToLowerInvariant();
        foreach (var (needle, name) in Known)
        {
            if (lower.Contains(needle, StringComparison.Ordinal))
            {
                return name;
            }
        }

        if (id.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            id = id[..^4];
        }

        var cut = id.IndexOfAny(['_', '!']);
        if (cut >= 0)
        {
            id = id[..cut];
        }

        var dot = id.LastIndexOf('.');
        if (dot >= 0 && dot < id.Length - 1)
        {
            id = id[(dot + 1)..];
        }

        id = id.Trim();
        return id.Length == 0 ? "" : char.ToUpperInvariant(id[0]) + id[1..];
    }
}
