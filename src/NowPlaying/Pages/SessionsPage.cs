using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using NowPlaying.Commands;
using NowPlaying.Core;
using NowPlaying.Services;
using Windows.Media.Control;

namespace NowPlaying.Pages;

/// <summary>Lists every media session on the system. Enter opens the details popup of that session; Ctrl+K has Play / Pause, Previous and Next.</summary>
internal sealed partial class SessionsPage : ListPage, IDisposable
{
    private readonly MediaService _service;
    private readonly List<(GlobalSystemMediaTransportControlsSession Session, DetailsPage Page)> _details = [];

    public SessionsPage(MediaService service)
    {
        _service = service;
        Id = "NowPlaying.sessions";
        Name = "Open";
        Title = "Now Playing";
        Icon = IconHelpers.FromRelativePath("Assets\\StoreLogo.png");
        EmptyContent = new CommandItem
        {
            Title = NowPlayingText.NothingIsPlaying,
            Subtitle = "Start playing something in Spotify, YouTube or another app.",
            Icon = Glyphs.Music(),
        };

        IsLoading = true;
        _service.Changed += OnChanged;
        _service.Start();
    }

    public override IListItem[] GetItems()
    {
        try
        {
            _service.Start();
            var snapshot = _service.Snapshot;
            var sessions = snapshot.Sessions;
            var items = new List<IListItem>(sessions.Count);
            foreach (var s in sessions)
            {
                var session = s.Session;
                var details = DetailsFor(session, s.AppId);
                var status = s.State switch
                {
                    PlaybackState.Playing => "Playing",
                    PlaybackState.Paused => "Paused",
                    _ => string.Empty,
                };

                items.Add(new ListItem(details)
                {
                    Title = s.Title,
                    Subtitle = NowPlayingText.SessionSubtitle(s.Artist.Length > 0 ? s.Artist : s.AlbumArtist, s.AppName, status),
                    Icon = s.Art ?? Glyphs.Music(),
                    MoreCommands =
                    [
                        new CommandContextItem(new MediaActionCommand("NowPlaying.session.toggle", s.State == PlaybackState.Playing ? "Pause" : "Play", Glyphs.Music(), () => _service.TogglePlayPauseAsync(session))),
                        new CommandContextItem(new MediaActionCommand("NowPlaying.session.previous", "Previous", Glyphs.Previous(), () => _service.SkipPreviousAsync(session))),
                        new CommandContextItem(new MediaActionCommand("NowPlaying.session.next", "Next", Glyphs.Next(), () => _service.SkipNextAsync(session))),
                    ],
                });
            }

            PruneDetails(sessions);
            return [.. items];
        }
        catch (Exception ex)
        {
            DiagLog.WriteLine($"Listing sessions failed: {ex}");
            return [];
        }
    }

    // One details page per session, reused across refreshes: an opened page keeps a live-update timer,
    // so creating a new page on every refresh would pile timers up.
    private DetailsPage DetailsFor(GlobalSystemMediaTransportControlsSession session, string appId)
    {
        lock (_details)
        {
            foreach (var (key, page) in _details)
            {
                if (MediaService.IsSame(key, session))
                {
                    return page;
                }
            }

            var created = new DetailsPage(_service, snap => snap.Sessions.FirstOrDefault(i => MediaService.IsSame(i.Session, session)), "NowPlaying.session.details." + appId);
            _details.Add((session, created));
            return created;
        }
    }

    private void PruneDetails(IReadOnlyList<SessionInfo> live)
    {
        lock (_details)
        {
            for (var i = _details.Count - 1; i >= 0; i--)
            {
                var (key, page) = _details[i];
                if (!live.Any(s => MediaService.IsSame(s.Session, key)))
                {
                    _details.RemoveAt(i);
                    page.Dispose();
                }
            }
        }
    }

    private void OnChanged()
    {
        try
        {
            IsLoading = false;
            RaiseItemsChanged();
        }
        catch (Exception ex)
        {
            DiagLog.WriteLine($"ItemsChanged failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _service.Changed -= OnChanged;
        lock (_details)
        {
            foreach (var (_, page) in _details)
            {
                page.Dispose();
            }

            _details.Clear();
        }
    }
}
