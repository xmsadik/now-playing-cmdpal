using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using NowPlaying.Core;
using NowPlaying.Services;

namespace NowPlaying.Pages;

/// <summary>
/// Popup with the large album art, track details and a click-to-seek bar with transport buttons for one session.
/// Which session is decided by <c>selector</c> on every refresh (the Dock shows "the current one", the list
/// shows a fixed one). The content objects are created once and only mutated, so the host re-renders on PropChanged.
/// Threading: host calls (<see cref="GetContent"/>, submits) never block, hold no lock and never call the media app;
/// rendering runs on a pool thread, one render at a time, and no lock is held while property setters call the host.
/// </summary>
internal sealed partial class DetailsPage : ContentPage, IDisposable
{
    public const string HeadId = "NowPlaying.details";

    // The host gives no "page closed" signal, so live updates run on a lease that every open/click renews.
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(3);

    private readonly MediaService _service;
    private readonly Func<MediaSnapshot, SessionInfo?> _selector;
    private readonly SeekForm _form;
    private readonly object _gate = new();
    private readonly IContent[] _content;

    private Timer? _tick;
    private bool _ticking;
    private bool _subscribed;
    private DateTime _leaseUntil = DateTime.MinValue;
    private bool _disposed;
    private int _renderPending;
    private int _rendering;

    public DetailsPage(MediaService service, Func<MediaSnapshot, SessionInfo?> selector, string id)
    {
        _service = service;
        _selector = selector;
        Id = id;
        Name = "Details";
        Title = "Now Playing";
        Icon = Glyphs.Music();

        _form = new SeekForm(HandleSubmit);
        _form.DataJson = "{}";
        _form.TemplateJson = SeekCard.Build(default);
        _content = [_form];
    }

    public override IContent[] GetContent()
    {
        _service.Start();
        Activate();

        // First paint from cached data only (no lock held, no media app calls), so the host never shows the
        // constructor defaults; live updates then continue on the render loop.
        try
        {
            var (card, _) = Compose();
            SetIfChanged(card);
        }
        catch (Exception ex)
        {
            DiagLog.WriteLine($"Details first paint failed: {ex}");
        }

        RequestRender();
        return _content;
    }

    private void Activate()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _leaseUntil = DateTime.UtcNow + Lease;
            if (!_subscribed)
            {
                _subscribed = true;
                _service.Changed += OnServiceChanged;
                _service.TimelineChanged += OnServiceChanged;
            }
        }
    }

    private void Deactivate()
    {
        lock (_gate)
        {
            if (_subscribed)
            {
                _subscribed = false;
                _service.Changed -= OnServiceChanged;
                _service.TimelineChanged -= OnServiceChanged;
            }

            StopTimerLocked();
        }
    }

    private void OnServiceChanged() => RequestRender();

    private void OnTick(object? state) => RequestRender();

    // Requests collapse into one pending flag; a single pool-thread loop renders while the flag is set.
    private void RequestRender()
    {
        Interlocked.Exchange(ref _renderPending, 1);
        if (Interlocked.CompareExchange(ref _rendering, 1, 0) == 0)
        {
            _ = Task.Run(RenderLoop);
        }
    }

    private void RenderLoop()
    {
        try
        {
            while (Interlocked.Exchange(ref _renderPending, 0) == 1)
            {
                Render();
            }
        }
        finally
        {
            Volatile.Write(ref _rendering, 0);
        }

        // A request that arrived between the last check and clearing _rendering would otherwise be lost.
        if (Volatile.Read(ref _renderPending) == 1)
        {
            RequestRender();
        }
    }

    private void Render()
    {
        try
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                if (DateTime.UtcNow > _leaseUntil)
                {
                    // Nobody has used the page for a long time: stop updating until it is opened again.
                    _subscribed = false;
                    _service.Changed -= OnServiceChanged;
                    _service.TimelineChanged -= OnServiceChanged;
                    StopTimerLocked();
                    return;
                }
            }

            var (card, tick) = Compose();
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                if (tick)
                {
                    StartTimerLocked();
                }
                else
                {
                    StopTimerLocked();
                }
            }

            // Outside the lock: these setters raise PropChanged into the host process.
            SetIfChanged(card);
        }
        catch (Exception ex)
        {
            DiagLog.WriteLine($"Details update failed: {ex}");
        }
    }

    /// <summary>Builds the card for the selected session from cached data only (no media app calls).</summary>
    private (string Card, bool Tick) Compose()
    {
        var info = _selector(_service.Snapshot);
        if (info is null)
        {
            return (SeekCard.Build(default), false);
        }

        var playing = info.State == PlaybackState.Playing;
        var timeline = _service.GetTimeline(info.Session);
        var hasTimeline = timeline is { } t0 && PlaybackTimeline.HasTimeline(t0);

        string? timeText = null;
        var filled = 0;
        var showBar = false;
        if (timeline is { } t && hasTimeline)
        {
            var position = PlaybackTimeline.CurrentPosition(t, playing, DateTimeOffset.UtcNow);
            timeText = PlaybackTimeline.FormatProgress(position, t.Start, t.End);
            filled = PlaybackTimeline.FilledSegments(position, t.Start, t.End);
            showBar = t.CanSeek;
        }

        var detail = string.Join(" · ", new[] { info.Album, info.AppName }.Where(x => !string.IsNullOrEmpty(x)));
        var card = SeekCard.Build(new SeekCardState(
            true,
            playing,
            showBar,
            filled,
            timeText,
            info.Title,
            NowPlayingText.Subtitle(info.Artist, info.AlbumArtist, string.Empty),
            detail,
            info.Image?.LargeUri));
        return (card, playing && hasTimeline);
    }

    private void SetIfChanged(string card)
    {
        if (_form.TemplateJson != card)
        {
            _form.TemplateJson = card;
        }
    }

    private void StartTimerLocked()
    {
        if (_ticking)
        {
            return;
        }

        _ticking = true;
        _tick ??= new Timer(OnTick, null, Timeout.Infinite, Timeout.Infinite);
        _tick.Change(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    private void StopTimerLocked()
    {
        if (!_ticking)
        {
            return;
        }

        _ticking = false;
        _tick?.Change(Timeout.Infinite, Timeout.Infinite);
    }

    private CommandResult HandleSubmit(string? inputs, string? data)
    {
        try
        {
            Activate();
            var payload = SubmitPayload.Parse(data, inputs);
            var info = _selector(_service.Snapshot);
            if (info is not null)
            {
                _ = Task.Run(() => RunActionAsync(info, payload)); // never start media-app calls on the host's thread
            }
        }
        catch (Exception ex)
        {
            DiagLog.WriteLine($"Details submit failed: {ex}");
        }

        return CommandResult.KeepOpen();
    }

    private async Task RunActionAsync(SessionInfo info, SubmitPayload payload)
    {
        var session = info.Session;
        switch (payload.Action)
        {
            case SubmitPayload.Previous:
                await _service.SkipPreviousAsync(session).ConfigureAwait(false);
                break;
            case SubmitPayload.Next:
                await _service.SkipNextAsync(session).ConfigureAwait(false);
                break;
            case SubmitPayload.PlayPause:
                await _service.TogglePlayPauseAsync(session).ConfigureAwait(false);
                break;
            case SubmitPayload.Seek when payload.Fraction is { } fraction:
                if (_service.GetTimeline(session) is { } seekLine && PlaybackTimeline.HasTimeline(seekLine))
                {
                    await _service.SeekAsync(session, PlaybackTimeline.SeekTarget(fraction, seekLine.Start, seekLine.End)).ConfigureAwait(false);
                }

                break;
            case SubmitPayload.Back or SubmitPayload.Forward:
                if (_service.GetTimeline(session) is { } line && PlaybackTimeline.HasTimeline(line))
                {
                    var now = PlaybackTimeline.CurrentPosition(line, info.State == PlaybackState.Playing, DateTimeOffset.UtcNow);
                    var delta = payload.Action == SubmitPayload.Back ? -PlaybackTimeline.SkipStep : PlaybackTimeline.SkipStep;
                    await _service.SeekAsync(session, PlaybackTimeline.Offset(now, delta, line.Start, line.End)).ConfigureAwait(false);
                }

                break;
            default:
                break;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }

        Deactivate();
        _tick?.Dispose();
    }
}

/// <summary>The Toolkit form with its submit hook routed to the page.</summary>
internal sealed partial class SeekForm(Func<string?, string?, CommandResult> onSubmit) : FormContent
{
    public override ICommandResult SubmitForm(string inputs, string data) => onSubmit(inputs, data);
}
