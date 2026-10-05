using System.Windows.Media.Imaging;
using Windows.Media;
using Windows.Media.Control;

namespace TaskbarMediaWidget.Media;

/// <summary>Immutable snapshot of what to show for the currently active media session.</summary>
public sealed record NowPlayingInfo(
    string Title,
    string Artist,
    string AlbumTitle,
    string SourceApp,
    BitmapImage? Thumbnail,
    GlobalSystemMediaTransportControlsSessionPlaybackStatus PlaybackStatus,
    bool IsPreviousEnabled,
    bool IsPlayPauseEnabled,
    bool IsNextEnabled,
    TimeSpan Position,
    TimeSpan Duration,
    DateTimeOffset LastUpdatedTime,
    bool CanSeek,
    bool? IsShuffleActive = null,
    MediaPlaybackAutoRepeatMode? AutoRepeatMode = null,
    System.Windows.Media.Color? AccentColor = null)
{
    public bool IsPlaying => PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

    /// <summary>
    /// Computes the estimated current playback position based on time elapsed since last SMTC update.
    /// </summary>
    public TimeSpan CurrentPosition
    {
        get
        {
            if (!IsPlaying || Duration <= TimeSpan.Zero)
            {
                return Position;
            }

            var elapsed = DateTimeOffset.UtcNow - LastUpdatedTime;
            if (elapsed < TimeSpan.Zero)
            {
                elapsed = TimeSpan.Zero;
            }

            var current = Position + elapsed;
            return current > Duration ? Duration : current;
        }
    }

    /// <summary>
    /// Current track progress as a normalized 0.0 to 1.0 value.
    /// </summary>
    public double ProgressFraction
    {
        get
        {
            if (Duration.TotalSeconds <= 0)
            {
                return 0.0;
            }

            return Math.Clamp(CurrentPosition.TotalSeconds / Duration.TotalSeconds, 0.0, 1.0);
        }
    }

    public static string FormatTime(TimeSpan time)
    {
        if (time <= TimeSpan.Zero)
        {
            return "0:00";
        }

        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:D2}:{time.Seconds:D2}"
            : $"{time.Minutes}:{time.Seconds:D2}";
    }
}
