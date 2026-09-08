using System.Runtime.InteropServices.WindowsRuntime;
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
    private readonly MediaManager _mediaManager = new();
    private volatile NowPlayingInfo? _currentNowPlaying;

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
        _mediaManager.Start();

        // MediaManager.Start() synchronously fires OnAnySessionOpened/OnAnyMediaPropertyChanged
        // for sessions that already exist at startup (e.g. media that was already playing before
        // this app launched) — before its own internal "started" flag is set. Those callbacks'
        // calls into GetActiveSession() throw InvalidOperationException("MediaManager has not
        // started"), which RefreshAsync's catch-all swallows, so the pre-existing session is
        // silently dropped and never displayed. Refresh once more now that Start() has actually
        // returned to pick up exactly that case.
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

    /// <summary>
    /// Session selection, simplest-first: prefer the session the OS considers "focused," else
    /// the first session that's actually Playing, else just the first available session, else
    /// null (nothing playing anywhere — widget should collapse).
    /// </summary>
    private MediaSession? GetActiveSession()
    {
        var sessions = _mediaManager.CurrentMediaSessions.Values.ToList();
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
        try
        {
            var info = await BuildNowPlayingInfoAsync(GetActiveSession());
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
        _mediaManager.OnAnySessionOpened -= OnAnySessionOpened;
        _mediaManager.OnAnyMediaPropertyChanged -= OnAnyMediaPropertyChanged;
        _mediaManager.OnAnyPlaybackStateChanged -= OnAnyPlaybackStateChanged;
        _mediaManager.OnAnySessionClosed -= OnAnySessionClosed;
        _mediaManager.Dispose();
    }
}
