using System;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using NowPlaying.Commands;
using NowPlaying.Core;
using NowPlaying.Services;

namespace NowPlaying.Pages;

/// <summary>
/// The Dock band: track info (album art + title + artist) followed by Previous / Play-Pause / Next buttons.
/// The four list items are created once and only ever mutated, so the host sees property changes, not item churn.
/// </summary>
internal sealed partial class NowPlayingDockBand : IDisposable
{
    public const string BandId = "NowPlaying.band";

    private readonly MediaService _service;
    private readonly DetailsPage _details;
    private readonly ListItem _head;
    private readonly ListItem _playPause;
    private readonly MediaActionCommand _playPauseCommand;

    // Last values pushed to the host, to skip no-op writes (each write would repaint the Dock).
    private object? _lastArt;
    private bool _headShowsGlyph = true;
    private PlaybackState? _lastState;

    public NowPlayingDockBand(MediaService service)
    {
        _service = service;

        // Clicking the head item opens the details popup for whatever the Dock currently shows.
        _details = new DetailsPage(_service, snapshot => snapshot.Current, DetailsPage.HeadId);
        _head = new ListItem(_details)
        {
            Title = NowPlayingText.NothingPlaying,
            Subtitle = string.Empty,
            Icon = Glyphs.Music(),
            MoreCommands =
            [
                new CommandContextItem(new MediaActionCommand("NowPlaying.head.playpause", "Play / Pause", Glyphs.Play(), () => _service.TogglePlayPauseAsync(null))),
                new CommandContextItem(new MediaActionCommand("NowPlaying.head.previous", "Previous", Glyphs.Previous(), () => _service.SkipPreviousAsync(null))),
                new CommandContextItem(new MediaActionCommand("NowPlaying.head.next", "Next", Glyphs.Next(), () => _service.SkipNextAsync(null))),
            ],
        };

        // Icon-only buttons: both the item title and the command name are empty, because the Toolkit (and the
        // host) fall back from an empty Title to Command.Name. Subtitle is never set.
        var previous = new ListItem(new MediaActionCommand("NowPlaying.previous", string.Empty, Glyphs.Previous(), () => _service.SkipPreviousAsync(null)))
        {
            Title = string.Empty,
            Icon = Glyphs.Previous(),
        };

        _playPauseCommand = new MediaActionCommand("NowPlaying.playpause", string.Empty, Glyphs.Play(), () => _service.TogglePlayPauseAsync(null));
        _playPause = new ListItem(_playPauseCommand)
        {
            Title = string.Empty,
            Icon = Glyphs.Play(),
        };

        var next = new ListItem(new MediaActionCommand("NowPlaying.next", string.Empty, Glyphs.Next(), () => _service.SkipNextAsync(null)))
        {
            Title = string.Empty,
            Icon = Glyphs.Next(),
        };

        Band = new WrappedDockItem([_head, previous, _playPause, next], BandId, "Now Playing");

        _service.Changed += OnChanged;
    }

    public ICommandItem Band { get; }

    private void OnChanged()
    {
        try
        {
            Apply(_service.Snapshot);
        }
        catch (Exception ex)
        {
            DiagLog.WriteLine($"Dock update failed: {ex}");
        }
    }

    private void Apply(MediaSnapshot snapshot)
    {
        var current = snapshot.Current;

        var title = current is null ? NowPlayingText.NothingPlaying : current.Title;
        var subtitle = current is null ? string.Empty : NowPlayingText.Subtitle(current.Artist, current.AlbumArtist, current.AppName);
        if (_head.Title != title)
        {
            _head.Title = title;
        }

        if (_head.Subtitle != subtitle)
        {
            _head.Subtitle = subtitle;
        }

        var art = current?.Art;
        if (art is not null)
        {
            if (!ReferenceEquals(_lastArt, art))
            {
                _head.Icon = art;
                _lastArt = art;
                _headShowsGlyph = false;
            }
        }
        else if (!_headShowsGlyph)
        {
            _head.Icon = Glyphs.Music();
            _lastArt = null;
            _headShowsGlyph = true;
        }

        var state = current?.State ?? PlaybackState.Other;
        if (_lastState != state)
        {
            _lastState = state;
            var playing = state == PlaybackState.Playing;
            _playPauseCommand.Icon = playing ? Glyphs.Pause() : Glyphs.Play();
            _playPause.Icon = playing ? Glyphs.Pause() : Glyphs.Play();
        }
    }

    public void Dispose()
    {
        _service.Changed -= OnChanged;
        _details.Dispose();
    }
}
