namespace NowPlaying.Core;

/// <summary>Pure text rules for what the Dock band and the list show.</summary>
public static class NowPlayingText
{
    public const string NothingPlaying = "Nothing playing";
    public const string NothingIsPlaying = "Nothing is playing";

    /// <summary>Track title; falls back to the app name, then to "Unknown track".</summary>
    public static string Title(string? title, string? appName)
    {
        var t = Clean(title);
        if (t.Length > 0)
        {
            return t;
        }

        var a = Clean(appName);
        return a.Length > 0 ? a : "Unknown track";
    }

    /// <summary>Artist; falls back to the album artist, then to the app name.</summary>
    public static string Subtitle(string? artist, string? albumArtist, string? appName)
    {
        foreach (var candidate in new[] { artist, albumArtist, appName })
        {
            var c = Clean(candidate);
            if (c.Length > 0)
            {
                return c;
            }
        }

        return "";
    }

    /// <summary>"Artist · App · Playing" for the session list; empty parts are skipped.</summary>
    public static string SessionSubtitle(string? artist, string? appName, string? status)
    {
        var parts = new List<string>(3);
        foreach (var p in new[] { artist, appName, status })
        {
            var c = Clean(p);
            if (c.Length > 0)
            {
                parts.Add(c);
            }
        }

        return string.Join(" · ", parts);
    }

    /// <summary>Trims and collapses internal whitespace (titles can contain newlines).</summary>
    public static string Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        return string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
