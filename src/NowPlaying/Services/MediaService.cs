using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NowPlaying.Core;
using Windows.Media.Control;

namespace NowPlaying.Services;

/// <summary>
/// Wraps the system media transport controls (GSMTC). Raises <see cref="Changed"/> (throttled) with a fresh
/// <see cref="Snapshot"/> whenever sessions, track info or playback state change, and <see cref="TimelineChanged"/>
/// when only the playback position moved. All GSMTC events arrive on arbitrary threads; state is guarded by
/// <c>_gate</c>, which is never held across awaits or host calls.
/// </summary>
internal sealed partial class MediaService : IDisposable
{
    private const int MaxArtRetries = 2;

    private static readonly TimeSpan Throttle = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ArtRetryDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan[] InitBackoff =
    [
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(60),
    ];

    private readonly object _gate = new();
    private readonly object _subGate = new();
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly Coalescer _coalescer;
    private readonly Coalescer _timelineCoalescer;
    private readonly HashSet<GlobalSystemMediaTransportControlsSession> _subscribed = [];
    private readonly ArtCache _art = new(16);
    private readonly Dictionary<string, int> _artRetries = [];
    private readonly Dictionary<GlobalSystemMediaTransportControlsSession, TimelineState> _timelines = [];

    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private MediaSnapshot _snapshot = MediaSnapshot.Empty;
    private int _started;
    private int _initFailures;
    private int _refreshPending;
    private bool _disposed;

    public MediaService()
    {
        _coalescer = new Coalescer(() => _ = RefreshAsync(), Throttle);
        _timelineCoalescer = new Coalescer(() => TimelineChanged?.Invoke(), Throttle);
    }

    /// <summary>Raised on a thread-pool thread after <see cref="Snapshot"/> was replaced.</summary>
    public event Action? Changed;

    /// <summary>Raised on a thread-pool thread when a session reported a new playback position (throttled).</summary>
    public event Action? TimelineChanged;

    public MediaSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _snapshot;
            }
        }
    }

    /// <summary>True once the first snapshot (possibly empty) has been published.</summary>
    public bool HasSnapshot => !ReferenceEquals(Snapshot, MediaSnapshot.Empty);

    /// <summary>Compares two projected sessions (CsWinRT compares by native pointer), falling back to reference equality.</summary>
    public static bool IsSame(GlobalSystemMediaTransportControlsSession? a, GlobalSystemMediaTransportControlsSession? b) =>
        a is not null && b is not null && (ReferenceEquals(a, b) || a.Equals(b));

    /// <summary>Starts the (asynchronous) connection to the system. Safe (and cheap) to call repeatedly.</summary>
    public void Start()
    {
        if (Volatile.Read(ref _disposed) || Interlocked.Exchange(ref _started, 1) == 1)
        {
            return;
        }

        _ = Task.Run(InitializeAsync);
    }

    /// <summary>
    /// The last timeline read for a session, or null. Never calls the media app: reading the timeline is a
    /// synchronous cross-process call that can hang, so it only happens on background threads
    /// (<see cref="UpdateTimelineAsync"/>) and code running on host calls reads this cache.
    /// </summary>
    public TimelineState? GetTimeline(GlobalSystemMediaTransportControlsSession session)
    {
        lock (_gate)
        {
            if (_timelines.TryGetValue(session, out var hit))
            {
                return hit;
            }

            foreach (var (key, value) in _timelines)
            {
                if (IsSame(key, session))
                {
                    return value;
                }
            }

            return null;
        }
    }

    private static TimelineState? ReadTimeline(GlobalSystemMediaTransportControlsSession session)
    {
        var t = session.GetTimelineProperties();
        if (t is null)
        {
            return null;
        }

        var canSeek = session.GetPlaybackInfo()?.Controls?.IsPlaybackPositionEnabled ?? false;
        return new TimelineState(t.Position, t.StartTime, t.EndTime, t.LastUpdatedTime, canSeek);
    }

    /// <summary>Reads the timeline on a pool thread with a timeout and stores it in the cache.</summary>
    private async Task UpdateTimelineAsync(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            var timeline = await Task.Run(() => ReadTimeline(session)).WaitAsync(CallTimeout).ConfigureAwait(false);
            lock (_gate)
            {
                // The event sender may be a different wrapper of the same session: drop every alias first.
                foreach (var alias in _timelines.Keys.Where(k => IsSame(k, session)).ToList())
                {
                    _timelines.Remove(alias);
                }

                if (timeline is { } t)
                {
                    _timelines[session] = t;
                }
            }
        }
        catch (Exception ex)
        {
            DiagLog.WriteLine($"Reading timeline failed: {ex.Message}");
        }
    }

    public Task TogglePlayPauseAsync(GlobalSystemMediaTransportControlsSession? session) =>
        ControlAsync(session, s => s.TryTogglePlayPauseAsync().AsTask());

    public Task SkipPreviousAsync(GlobalSystemMediaTransportControlsSession? session) =>
        ControlAsync(session, s => s.TrySkipPreviousAsync().AsTask());

    public Task SkipNextAsync(GlobalSystemMediaTransportControlsSession? session) =>
        ControlAsync(session, s => s.TrySkipNextAsync().AsTask());

    public Task SeekAsync(GlobalSystemMediaTransportControlsSession? session, TimeSpan position) =>
        ControlAsync(session, s => s.TryChangePlaybackPositionAsync(position.Ticks).AsTask());

    // A null session means "the one the Dock shows", so display and controls always agree.
    private async Task ControlAsync(GlobalSystemMediaTransportControlsSession? session, Func<GlobalSystemMediaTransportControlsSession, Task<bool>> action)
    {
        try
        {
            Start();
            var target = session ?? Snapshot.Current?.Session;
            if (target is null)
            {
                return;
            }

            await action(target).WaitAsync(CallTimeout).ConfigureAwait(false);
            _coalescer.Trigger();
            _timelineCoalescer.Trigger();
        }
        catch (Exception ex)
        {
            DiagLog.WriteLine($"Media control failed: {ex}");
        }
    }

    private async Task InitializeAsync()
    {
        try
        {
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask()
                .WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _manager = manager;
            }

            manager.CurrentSessionChanged += OnManagerChanged;
            manager.SessionsChanged += OnManagerChanged;
            Volatile.Write(ref _initFailures, 0);
            await RefreshAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            DiagLog.WriteLine($"Media session manager unavailable: {ex}");
            Volatile.Write(ref _started, 0);

            // Publish an (empty) first snapshot so the UI stops showing "loading", then try again with backoff.
            PublishEmptyIfNone();
            var attempt = Interlocked.Increment(ref _initFailures);
            var delay = InitBackoff[Math.Min(attempt - 1, InitBackoff.Length - 1)];
            _ = Task.Delay(delay).ContinueWith(_ => Start(), TaskScheduler.Default);
        }
    }

    private void PublishEmptyIfNone()
    {
        lock (_gate)
        {
            if (_disposed || HasSnapshotUnlocked())
            {
                return;
            }

            _snapshot = new MediaSnapshot([], null);
        }

        Changed?.Invoke();
    }

    private bool HasSnapshotUnlocked() => !ReferenceEquals(_snapshot, MediaSnapshot.Empty);

    private void OnManagerChanged(GlobalSystemMediaTransportControlsSessionManager sender, object args) => _coalescer.Trigger();

    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args) => _coalescer.Trigger();

    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args) => _coalescer.Trigger();

    private void OnTimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args) =>
        _ = UpdateTimelineAsync(sender).ContinueWith(_ => _timelineCoalescer.Trigger(), TaskScheduler.Default);

    // Triggers collapse into one pending flag: whoever holds the lock keeps looping while the flag is set,
    // everyone else just returns (and the lock holder re-checks the flag after releasing).
    private async Task RefreshAsync()
    {
        Interlocked.Exchange(ref _refreshPending, 1);
        while (Volatile.Read(ref _refreshPending) == 1)
        {
            try
            {
                if (!await _refreshLock.WaitAsync(0).ConfigureAwait(false))
                {
                    return;
                }
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            try
            {
                while (Interlocked.Exchange(ref _refreshPending, 0) == 1)
                {
                    await RefreshOnceAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                DiagLog.WriteLine($"Refresh failed: {ex}");
            }
            finally
            {
                try
                {
                    _refreshLock.Release();
                }
                catch (ObjectDisposedException)
                {
                    // Disposed while refreshing.
                }
            }
        }
    }

    private async Task RefreshOnceAsync()
    {
        GlobalSystemMediaTransportControlsSessionManager? manager;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            manager = _manager;
        }

        if (manager is null)
        {
            return;
        }

        var sessions = manager.GetSessions().ToList();
        var cur = manager.GetCurrentSession();
        SyncSubscriptions(sessions);

        var previous = Snapshot;
        var timelines = Task.WhenAll(sessions.Select(UpdateTimelineAsync));
        var built = await Task.WhenAll(sessions.Select(s => BuildSafeAsync(s, previous))).ConfigureAwait(false);
        await timelines.ConfigureAwait(false);
        var infos = built.OfType<SessionInfo>().ToList();
        lock (_gate)
        {
            foreach (var gone in _timelines.Keys.Where(k => !sessions.Any(s => IsSame(s, k))).ToList())
            {
                _timelines.Remove(gone);
            }
        }

        var curId = cur?.SourceAppUserModelId;
        var current = infos.FirstOrDefault(i => IsSame(i.Session, cur))
            ?? (curId is null ? null : infos.FirstOrDefault(i => i.AppId == curId))
            ?? infos.FirstOrDefault(i => i.State == PlaybackState.Playing)
            ?? infos.FirstOrDefault();

        var inUse = new HashSet<ArtImage>();
        foreach (var i in infos.Concat(previous.Sessions))
        {
            if (i.Image is not null)
            {
                inUse.Add(i.Image);
            }
        }

        _art.Trim(inUse);

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _snapshot = new MediaSnapshot(infos, current);
        }

        Changed?.Invoke();
    }

    private void SyncSubscriptions(List<GlobalSystemMediaTransportControlsSession> sessions)
    {
        lock (_subGate)
        {
            if (_disposed)
            {
                return;
            }

            foreach (var gone in _subscribed.Where(s => !sessions.Any(x => IsSame(x, s))).ToList())
            {
                Unsubscribe(gone);
                _subscribed.Remove(gone);
            }

            foreach (var session in sessions)
            {
                if (!_subscribed.Add(session))
                {
                    continue;
                }

                try
                {
                    session.MediaPropertiesChanged += OnMediaPropertiesChanged;
                    session.PlaybackInfoChanged += OnPlaybackInfoChanged;
                    session.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
                }
                catch (Exception ex)
                {
                    DiagLog.WriteLine($"Subscribe failed: {ex.Message}");
                    Unsubscribe(session);
                    _subscribed.Remove(session);
                }
            }
        }
    }

    private void Unsubscribe(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            session.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
        }
        catch (Exception ex)
        {
            DiagLog.WriteLine($"Unsubscribe failed: {ex.Message}");
        }
    }

    private async Task<SessionInfo?> BuildSafeAsync(GlobalSystemMediaTransportControlsSession session, MediaSnapshot previous)
    {
        try
        {
            return await BuildInfoAsync(session, previous).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            DiagLog.WriteLine($"Reading session failed: {ex.Message}");
            return null;
        }
    }

    private async Task<SessionInfo> BuildInfoAsync(GlobalSystemMediaTransportControlsSession session, MediaSnapshot previous)
    {
        var appId = session.SourceAppUserModelId;
        var appName = AppNames.GetFriendlyName(appId);
        var props = await session.TryGetMediaPropertiesAsync().AsTask().WaitAsync(CallTimeout).ConfigureAwait(false);
        var status = session.GetPlaybackInfo()?.PlaybackStatus;
        var state = status switch
        {
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => PlaybackState.Playing,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => PlaybackState.Paused,
            _ => PlaybackState.Other,
        };

        var title = NowPlayingText.Title(props?.Title, appName);
        var artist = NowPlayingText.Clean(props?.Artist);
        var albumArtist = NowPlayingText.Clean(props?.AlbumArtist);
        var album = NowPlayingText.Clean(props?.AlbumTitle);

        var image = await ResolveArtAsync($"{appId}|{title}|{artist}", props).ConfigureAwait(false);

        // Spotify (and others) briefly report no thumbnail on track changes. If it is still the same track,
        // keep the previous art for one refresh instead of flashing the glyph.
        var carried = false;
        if (image is null)
        {
            var old = previous.Sessions.FirstOrDefault(i => IsSame(i.Session, session));
            if (old?.Image is not null && !old.ArtCarried && old.Title == title && old.Artist == artist)
            {
                image = old.Image;
                carried = true;
            }
        }

        return new SessionInfo(session, appId, appName, title, artist, albumArtist, album, state, image, carried);
    }

    // Album art, cached by content hash so an unchanged picture keeps the very same IconInfo (no Dock repaint).
    // On any failure the result is null (glyph) - never an image that belongs to another track.
    private async Task<ArtImage?> ResolveArtAsync(string trackKey, GlobalSystemMediaTransportControlsSessionMediaProperties? props)
    {
        if (props?.Thumbnail is null)
        {
            return null;
        }

        try
        {
            var read = await ThumbnailLoader.ReadAsync(props.Thumbnail).ConfigureAwait(false);
            if (read is null)
            {
                ScheduleArtRetry(trackKey);
                return null;
            }

            var (bytes, key) = read.Value;
            var existing = _art.TryGet(key);
            if (existing is not null)
            {
                return existing;
            }

            var fresh = await ThumbnailLoader.BuildAsync(bytes, key).ConfigureAwait(false);
            if (fresh is null)
            {
                ScheduleArtRetry(trackKey);
                return null;
            }

            return _art.GetOrAdd(fresh);
        }
        catch (Exception ex)
        {
            DiagLog.WriteLine($"Album art failed: {ex.Message}");
            ScheduleArtRetry(trackKey);
            return null;
        }
    }

    // At most MaxArtRetries extra refreshes per track after a failed art read.
    private void ScheduleArtRetry(string trackKey)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (_artRetries.Count > 64)
            {
                _artRetries.Clear();
            }

            var used = _artRetries.GetValueOrDefault(trackKey);
            if (used >= MaxArtRetries)
            {
                return;
            }

            _artRetries[trackKey] = used + 1;
        }

        _ = Task.Delay(ArtRetryDelay).ContinueWith(_ => _coalescer.Trigger(), TaskScheduler.Default);
    }

    public void Dispose()
    {
        GlobalSystemMediaTransportControlsSessionManager? manager;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            manager = _manager;
            _manager = null;
        }

        _coalescer.Dispose();
        _timelineCoalescer.Dispose();
        try
        {
            if (manager is not null)
            {
                manager.CurrentSessionChanged -= OnManagerChanged;
                manager.SessionsChanged -= OnManagerChanged;
            }
        }
        catch (Exception ex)
        {
            DiagLog.WriteLine($"Dispose failed: {ex.Message}");
        }

        // SyncSubscriptions checks _disposed under the same lock, so nothing is subscribed after this.
        lock (_subGate)
        {
            foreach (var session in _subscribed)
            {
                Unsubscribe(session);
            }

            _subscribed.Clear();
        }
    }
}
