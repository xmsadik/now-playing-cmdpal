using System.Collections.Generic;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Windows.Media.Control;

namespace NowPlaying.Services;

internal enum PlaybackState
{
    Other,
    Playing,
    Paused,
}

/// <summary>Immutable view of one media session at one point in time.</summary>
internal sealed record SessionInfo(
    GlobalSystemMediaTransportControlsSession Session,
    string AppId,
    string AppName,
    string Title,
    string Artist,
    string AlbumArtist,
    string Album,
    PlaybackState State,
    ArtImage? Image,
    bool ArtCarried)
{
    /// <summary>64 px icon for the Dock and the list; null when there is no album art.</summary>
    public IconInfo? Art => Image?.Icon;
}

/// <summary>All sessions plus the one the system considers current (null when nothing is playing).</summary>
internal sealed class MediaSnapshot
{
    public static readonly MediaSnapshot Empty = new([], null);

    public MediaSnapshot(IReadOnlyList<SessionInfo> sessions, SessionInfo? current)
    {
        Sessions = sessions;
        Current = current;
    }

    public IReadOnlyList<SessionInfo> Sessions { get; }

    public SessionInfo? Current { get; }
}
