using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.Win32;
using TaskbarMediaWidget.Core;
using WindowsMediaController;
using static WindowsMediaController.MediaManager;

namespace TaskbarMediaWidget.Media;

/// <summary>
/// Wraps WindowsMediaController's SMTC session manager and reduces "all current media sessions"
/// down to a single NowPlayingInfo (or null) for the widget to display. No app allow/block-list
/// filtering, no "pause other sessions" behavior — that's FluentFlyout-specific scope we don't
/// need here.
/// </summary>
internal sealed class MediaSessionService : IDisposable
{
    // WindowsMediaController's own XML doc calls out a known bug where SMTC events stop firing;
    // ForceUpdate() exists specifically to work around it. A tray app that runs for days is
    // exactly the workload that hits it, so re-sync on a heartbeat plus resume-from-sleep/unlock
    // rather than trusting events alone to keep arriving.
    private const int HeartbeatIntervalMs = 45_000;

    private readonly MediaManager _mediaManager = new();
    private readonly System.Timers.Timer _heartbeatTimer = new(HeartbeatIntervalMs) { AutoReset = true };
    private volatile NowPlayingInfo? _currentNowPlaying;
    private long _refreshGeneration;

    public event Action<NowPlayingInfo?>? NowPlayingChanged;

    /// <summary>
    /// The most recently published now-playing snapshot (or null), for a freshly (re)created
    /// widget window to pick up immediately instead of waiting for the next SMTC event — this
    /// matters after an Explorer restart, where the old window's HWND is gone and a new one has
    /// nothing to show until something happens to trigger a fresh SMTC callback.
    /// </summary>
    public NowPlayingInfo? CurrentNowPlaying => _currentNowPlaying;

    public void Start()
    {
        _mediaManager.OnAnySessionOpened += OnAnySessionOpened;
        _mediaManager.OnAnyMediaPropertyChanged += OnAnyMediaPropertyChanged;
        _mediaManager.OnAnyPlaybackStateChanged += OnAnyPlaybackStateChanged;
        _mediaManager.OnAnySessionClosed += OnAnySessionClosed;
        _mediaManager.OnFocusedSessionChanged += OnFocusedSessionChanged;
        _mediaManager.Start();

        // MediaManager.Start() synchronously fires OnAnySessionOpened/OnAnyMediaPropertyChanged
        // for sessions that already exist at startup (e.g. media that was already playing before
        // this app launched) — before its own internal "started" flag is set. Those callbacks'
        // calls into GetActiveSession() throw InvalidOperationException("MediaManager has not
        // started"), which RefreshAsync's catch-all swallows, so the pre-existing session is
        // silently dropped and never displayed. Refresh once more now that Start() has actually
        // returned to pick up exactly that case.
        _ = RefreshAsync();

        _heartbeatTimer.Elapsed += (_, _) => ForceUpdateAndRefresh();
        _heartbeatTimer.Start();

        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            ForceUpdateAndRefresh();
        }
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect)
        {
            ForceUpdateAndRefresh();
        }
    }

    private void ForceUpdateAndRefresh()
    {
        try
        {
            _mediaManager.ForceUpdate();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"ForceUpdate failed: {ex.Message}");
        }

        _ = RefreshAsync();
    }

    public async Task PlayPauseAsync()
    {
        if (GetActiveSession() is { } session)
        {
            await session.ControlSession.TryTogglePlayPauseAsync();
        }
    }

    public async Task PreviousAsync()
    {
        if (GetActiveSession() is { } session)
        {
            await session.ControlSession.TrySkipPreviousAsync();
        }
    }

    public async Task NextAsync()
    {
        if (GetActiveSession() is { } session)
        {
            await session.ControlSession.TrySkipNextAsync();
        }
    }

    private void OnAnySessionOpened(MediaSession mediaSession) => _ = RefreshAsync();

    private void OnAnyMediaPropertyChanged(MediaSession mediaSession, Windows.Media.Control.GlobalSystemMediaTransportControlsSessionMediaProperties mediaProperties) =>
        _ = RefreshAsync();

    private void OnAnyPlaybackStateChanged(MediaSession mediaSession, Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackInfo? playbackInfo) =>
        _ = RefreshAsync();

    private void OnAnySessionClosed(MediaSession mediaSession) => _ = RefreshAsync();

    // GetFocusedSession() reflects the OS's current focus pick, but nothing previously told this
    // service when that pick changed — so switching focus between two already-open sessions (e.g.
    // pausing Spotify and starting a YouTube tab) never triggered a refresh on its own.
    private void OnFocusedSessionChanged(MediaSession mediaSession) => _ = RefreshAsync();

    /// <summary>
    /// Session selection, simplest-first: prefer the session the OS considers "focused," else
    /// the first session that's actually Playing, else just the first available session, else
    /// null (nothing playing anywhere — widget should collapse).
    /// </summary>
    private MediaSession? GetActiveSession()
    {
        if (!_mediaManager.IsStarted)
        {
            return null;
        }

        List<MediaSession> sessions;
        try
        {
            sessions = _mediaManager.CurrentMediaSessions.Values.ToList();
        }
        catch (InvalidOperationException)
        {
            // CurrentMediaSessions is a plain Dictionary mutated from SMTC callback threads with
            // no synchronization; a session opening/closing mid-enumeration throws here. Retry
            // once against a fresh snapshot rather than letting the fault propagate — this runs
            // on both UI-thread button clicks and background refreshes.
            try
            {
                sessions = _mediaManager.CurrentMediaSessions.Values.ToList();
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        if (sessions.Count == 0)
        {
            return null;
        }

        var focused = _mediaManager.GetFocusedSession();
        if (focused is not null && sessions.Any(s => s.Id == focused.Id))
        {
            return focused;
        }

        var playing = sessions.FirstOrDefault(s =>
            s.ControlSession?.GetPlaybackInfo()?.PlaybackStatus ==
            Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing);

        return playing ?? sessions.FirstOrDefault();
    }

    private async Task RefreshAsync()
    {
        // Every SMTC callback fires a refresh on an arbitrary thread-pool thread, and building
        // NowPlayingInfo awaits property/thumbnail reads that can take a while — overlapping
        // refreshes routinely finish out of order. Stamp each one; if a newer refresh has already
        // started by the time this one finishes, drop this result instead of publishing stale
        // data (e.g. the previous track's cover art under the new track's title) over it.
        var generation = Interlocked.Increment(ref _refreshGeneration);
        try
        {
            var info = await BuildNowPlayingInfoAsync(GetActiveSession());

            if (Interlocked.Read(ref _refreshGeneration) != generation)
            {
                return;
            }

            _currentNowPlaying = info;
            NowPlayingChanged?.Invoke(info);
        }
        catch (Exception ex)
        {
            AppLog.Error("Failed to refresh now-playing info", ex);
        }
    }

    private static async Task<NowPlayingInfo?> BuildNowPlayingInfoAsync(MediaSession? session)
    {
        if (session?.ControlSession is null)
        {
            return null;
        }

        var controlSession = session.ControlSession;

        Windows.Media.Control.GlobalSystemMediaTransportControlsSessionMediaProperties? mediaProperties;
        try
        {
            mediaProperties = await controlSession.TryGetMediaPropertiesAsync();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Failed to read media properties: {ex.Message}");
            return null;
        }

        if (mediaProperties is null)
        {
            return null;
        }

        var playbackInfo = controlSession.GetPlaybackInfo();
        var thumbnail = await ThumbnailConverter.TryConvertAsync(mediaProperties.Thumbnail);

        return new NowPlayingInfo(
            Title: mediaProperties.Title ?? string.Empty,
            Artist: mediaProperties.Artist ?? string.Empty,
            Thumbnail: thumbnail,
            PlaybackStatus: playbackInfo?.PlaybackStatus ?? Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed,
            IsPreviousEnabled: playbackInfo?.Controls?.IsPreviousEnabled ?? false,
            IsPlayPauseEnabled: (playbackInfo?.Controls?.IsPlayEnabled ?? false) || (playbackInfo?.Controls?.IsPauseEnabled ?? false),
            IsNextEnabled: playbackInfo?.Controls?.IsNextEnabled ?? false);
    }

    public void Dispose()
    {
        _heartbeatTimer.Stop();
        _heartbeatTimer.Dispose();
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;

        _mediaManager.OnAnySessionOpened -= OnAnySessionOpened;
        _mediaManager.OnAnyMediaPropertyChanged -= OnAnyMediaPropertyChanged;
        _mediaManager.OnAnyPlaybackStateChanged -= OnAnyPlaybackStateChanged;
        _mediaManager.OnAnySessionClosed -= OnAnySessionClosed;
        _mediaManager.OnFocusedSessionChanged -= OnFocusedSessionChanged;
        _mediaManager.Dispose();
    }
}
