using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.Win32;
using TaskbarMediaWidget.Core;
using TaskbarMediaWidget.Interop;
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
    private readonly object _thumbnailCacheLock = new();
    private volatile NowPlayingInfo? _currentNowPlaying;
    private long _refreshGeneration;
    private (string Title, string Artist, string AlbumTitle)? _lastThumbnailKey;
    private System.Windows.Media.Imaging.BitmapImage? _lastThumbnail;
    private System.Windows.Media.Color? _lastAccent;

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
        _mediaManager.OnAnyTimelinePropertyChanged += OnAnyTimelinePropertyChanged;
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

    public async Task SeekAsync(TimeSpan position)
    {
        if (GetActiveSession() is { } session)
        {
            await session.ControlSession.TryChangePlaybackPositionAsync(position.Ticks);
        }
    }

    public async Task ToggleShuffleAsync()
    {
        if (GetActiveSession() is { } session)
        {
            var isShuffle = session.ControlSession.GetPlaybackInfo()?.IsShuffleActive ?? false;
            await session.ControlSession.TryChangeShuffleActiveAsync(!isShuffle);
        }
    }

    public async Task ToggleRepeatAsync()
    {
        if (GetActiveSession() is { } session)
        {
            var current = session.ControlSession.GetPlaybackInfo()?.AutoRepeatMode ?? Windows.Media.MediaPlaybackAutoRepeatMode.None;
            var next = current switch
            {
                Windows.Media.MediaPlaybackAutoRepeatMode.None => Windows.Media.MediaPlaybackAutoRepeatMode.List,
                Windows.Media.MediaPlaybackAutoRepeatMode.List => Windows.Media.MediaPlaybackAutoRepeatMode.Track,
                _ => Windows.Media.MediaPlaybackAutoRepeatMode.None,
            };
            await session.ControlSession.TryChangeAutoRepeatModeAsync(next);
        }
    }

    private void OnAnySessionOpened(MediaSession mediaSession) => _ = RefreshAsync();

    private void OnAnyMediaPropertyChanged(MediaSession mediaSession, Windows.Media.Control.GlobalSystemMediaTransportControlsSessionMediaProperties mediaProperties) =>
        _ = RefreshAsync();

    private void OnAnyPlaybackStateChanged(MediaSession mediaSession, Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackInfo? playbackInfo) =>
        _ = RefreshAsync();

    private void OnAnyTimelinePropertyChanged(MediaSession mediaSession, Windows.Media.Control.GlobalSystemMediaTransportControlsSessionTimelineProperties? timelineProperties) =>
        _ = RefreshAsync();

    private void OnAnySessionClosed(MediaSession mediaSession) => _ = RefreshAsync();

    // GetFocusedSession() reflects the OS's current focus pick, but nothing previously told this
    // service when that pick changed — so switching focus between two already-open sessions (e.g.
    // pausing Spotify and starting a YouTube tab) never triggered a refresh on its own.
    private void OnFocusedSessionChanged(MediaSession mediaSession) => _ = RefreshAsync();

    private string? _pinnedSessionId;

    public void SelectSession(string sessionId)
    {
        _pinnedSessionId = sessionId;
        _ = RefreshAsync();
    }

    public void SelectNextSession()
    {
        var sessions = GetValidSessions();
        if (sessions.Count <= 1) return;

        var current = GetActiveSession();
        var index = current is not null ? sessions.FindIndex(s => s.Id == current.Id) : -1;
        var nextIndex = (index + 1) % sessions.Count;
        _pinnedSessionId = sessions[nextIndex].Id;
        _ = RefreshAsync();
    }

    public async Task<IReadOnlyList<MediaSessionModel>> GetAvailableSessionsAsync()
    {
        var result = new List<MediaSessionModel>();
        var sessions = GetValidSessions();
        var active = GetActiveSession();

        foreach (var s in sessions)
        {
            var isSelected = active is not null && s.Id == active.Id;
            var isPlaying = s.ControlSession?.GetPlaybackInfo()?.PlaybackStatus ==
                            Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            var appName = ResolveSourceApp(s);
            var title = string.Empty;
            var artist = string.Empty;

            try
            {
                if (s.ControlSession is not null)
                {
                    var props = await s.ControlSession.TryGetMediaPropertiesAsync();
                    if (props is not null)
                    {
                        title = props.Title ?? string.Empty;
                        artist = props.Artist ?? string.Empty;
                    }
                }
            }
            catch
            {
                // Fallback to empty strings
            }

            result.Add(new MediaSessionModel(
                Id: s.Id,
                AppId: appName,
                Title: string.IsNullOrWhiteSpace(title) ? appName : title,
                Artist: artist,
                IsPlaying: isPlaying,
                IsSelected: isSelected));
        }

        return result;
    }

    public void FocusActiveSessionApp()
    {
        try
        {
            var session = GetActiveSession();
            if (session is null) return;

            var rawId = session.ControlSession?.SourceAppUserModelId ?? session.Id ?? string.Empty;
            AppLog.Info($"Focusing source app for session: {rawId}");

            var candidateName = System.IO.Path.GetFileNameWithoutExtension(rawId);
            if (candidateName.Contains('!'))
            {
                candidateName = candidateName.Substring(candidateName.LastIndexOf('!') + 1);
            }

            var procs = System.Diagnostics.Process.GetProcesses();
            System.Diagnostics.Process? targetProc = null;

            foreach (var p in procs)
            {
                try
                {
                    if (string.Equals(p.ProcessName, candidateName, StringComparison.OrdinalIgnoreCase) ||
                        rawId.Contains(p.ProcessName, StringComparison.OrdinalIgnoreCase))
                    {
                        if (p.MainWindowHandle != IntPtr.Zero)
                        {
                            targetProc = p;
                            break;
                        }
                    }
                }
                catch { }
            }

            if (targetProc != null && targetProc.MainWindowHandle != IntPtr.Zero)
            {
                var hwnd = targetProc.MainWindowHandle;
                NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
                NativeMethods.SetForegroundWindow(hwnd);
                return;
            }

            NativeMethods.EnumWindows((hwnd, _) =>
            {
                if (!NativeMethods.IsWindowVisible(hwnd)) return true;
                NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
                try
                {
                    var p = System.Diagnostics.Process.GetProcessById((int)pid);
                    if (rawId.Contains(p.ProcessName, StringComparison.OrdinalIgnoreCase))
                    {
                        NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
                        NativeMethods.SetForegroundWindow(hwnd);
                        return false;
                    }
                }
                catch { }
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Failed to focus source app: {ex.Message}");
        }
    }

    private static bool IsAppIgnored(MediaSession session)
    {
        var ignored = AppSettings.IgnoredApps;
        if (string.IsNullOrWhiteSpace(ignored)) return false;

        var source = session.ControlSession?.SourceAppUserModelId ?? session.Id ?? string.Empty;
        var tokens = ignored.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return tokens.Any(t => source.Contains(t, StringComparison.OrdinalIgnoreCase));
    }

    private List<MediaSession> GetValidSessions()
    {
        if (!_mediaManager.IsStarted) return [];

        List<MediaSession> sessions;
        try
        {
            sessions = _mediaManager.CurrentMediaSessions.Values.ToList();
        }
        catch (InvalidOperationException)
        {
            try
            {
                sessions = _mediaManager.CurrentMediaSessions.Values.ToList();
            }
            catch (InvalidOperationException)
            {
                return [];
            }
        }

        return sessions.Where(s => !IsAppIgnored(s)).ToList();
    }

    /// <summary>
    /// Session selection: pinned user choice first, else focused, else playing, else first.
    /// </summary>
    private MediaSession? GetActiveSession()
    {
        var sessions = GetValidSessions();
        if (sessions.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrEmpty(_pinnedSessionId))
        {
            var pinned = sessions.FirstOrDefault(s => s.Id == _pinnedSessionId);
            if (pinned is not null)
            {
                return pinned;
            }
            _pinnedSessionId = null;
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

    private async Task<NowPlayingInfo?> BuildNowPlayingInfoAsync(MediaSession? session)
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
        var controls = playbackInfo?.Controls;
        var timelineInfo = controlSession.GetTimelineProperties();
        var (thumbnail, accent) = await GetArtworkAsync(mediaProperties);
        var sourceApp = ResolveSourceApp(session);

        return new NowPlayingInfo(
            Title: mediaProperties.Title ?? string.Empty,
            Artist: mediaProperties.Artist ?? string.Empty,
            AlbumTitle: mediaProperties.AlbumTitle ?? string.Empty,
            SourceApp: sourceApp,
            Thumbnail: thumbnail,
            PlaybackStatus: playbackInfo?.PlaybackStatus ?? Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed,
            IsPreviousEnabled: controls?.IsPreviousEnabled ?? false,
            IsPlayPauseEnabled: (controls?.IsPlayEnabled ?? false) || (controls?.IsPauseEnabled ?? false),
            IsNextEnabled: controls?.IsNextEnabled ?? false,
            Position: timelineInfo?.Position ?? TimeSpan.Zero,
            Duration: timelineInfo?.EndTime ?? TimeSpan.Zero,
            LastUpdatedTime: timelineInfo?.LastUpdatedTime ?? DateTimeOffset.UtcNow,
            CanSeek: controls?.IsPlaybackPositionEnabled ?? false,
            IsShuffleActive: playbackInfo?.IsShuffleActive,
            AutoRepeatMode: playbackInfo?.AutoRepeatMode,
            AccentColor: accent,
            SessionId: session.Id,
            AvailableSessionsCount: GetValidSessions().Count);
    }

    private static string ResolveSourceApp(MediaSession session)
    {
        var raw = session.ControlSession?.SourceAppUserModelId;
        if (string.IsNullOrWhiteSpace(raw))
        {
            raw = session.Id ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            return "Media Player";
        }

        if (raw.Contains("spotify", StringComparison.OrdinalIgnoreCase)) return "Spotify";
        if (raw.Contains("chrome", StringComparison.OrdinalIgnoreCase)) return "Google Chrome";
        if (raw.Contains("msedge", StringComparison.OrdinalIgnoreCase) || raw.Contains("edge", StringComparison.OrdinalIgnoreCase)) return "Microsoft Edge";
        if (raw.Contains("firefox", StringComparison.OrdinalIgnoreCase)) return "Mozilla Firefox";
        if (raw.Contains("vlc", StringComparison.OrdinalIgnoreCase)) return "VLC Media Player";
        if (raw.Contains("music", StringComparison.OrdinalIgnoreCase)) return "Music";
        if (raw.Contains("itunes", StringComparison.OrdinalIgnoreCase)) return "Apple Music";
        if (raw.Contains("tidal", StringComparison.OrdinalIgnoreCase)) return "TIDAL";
        if (raw.Contains("foobar", StringComparison.OrdinalIgnoreCase)) return "foobar2000";

        var name = System.IO.Path.GetFileNameWithoutExtension(raw);
        return string.IsNullOrWhiteSpace(name) ? raw : name;
    }

    // Chromium-based browsers fire media-property-changed aggressively — often several times for
    // the same track. Each call previously re-opened the SMTC thumbnail stream and re-decoded a
    // JPEG to produce a bitmap identical to the one already on screen. Cache the one most recent
    // decode keyed on (title, artist, album) and skip the decode entirely on a repeat. The accent
    // color is derived from that same bitmap, so it rides along in the same cache entry rather
    // than being re-extracted per event.
    private async Task<(System.Windows.Media.Imaging.BitmapImage? Thumbnail, System.Windows.Media.Color? Accent)> GetArtworkAsync(
        Windows.Media.Control.GlobalSystemMediaTransportControlsSessionMediaProperties mediaProperties)
    {
        var key = (mediaProperties.Title ?? string.Empty, mediaProperties.Artist ?? string.Empty, mediaProperties.AlbumTitle ?? string.Empty);

        lock (_thumbnailCacheLock)
        {
            if (_lastThumbnailKey == key)
            {
                return (_lastThumbnail, _lastAccent);
            }
        }

        // Raised from the original 64px default so cover art stays reasonably sharp at 200% DPI
        // scaling, where a 64px source stretched to the widget's 24dp box would visibly blur.
        var thumbnail = await ThumbnailConverter.TryConvertAsync(mediaProperties.Thumbnail, decodePixelWidth: 96);
        var accent = AccentColorExtractor.TryGetAccent(thumbnail);

        lock (_thumbnailCacheLock)
        {
            _lastThumbnailKey = key;
            _lastThumbnail = thumbnail;
            _lastAccent = accent;
        }

        return (thumbnail, accent);
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
        _mediaManager.OnAnyTimelinePropertyChanged -= OnAnyTimelinePropertyChanged;
        _mediaManager.OnAnySessionClosed -= OnAnySessionClosed;
        _mediaManager.OnFocusedSessionChanged -= OnFocusedSessionChanged;
        _mediaManager.Dispose();
    }
}

public sealed record MediaSessionModel(
    string Id,
    string AppId,
    string Title,
    string Artist,
    bool IsPlaying,
    bool IsSelected);
