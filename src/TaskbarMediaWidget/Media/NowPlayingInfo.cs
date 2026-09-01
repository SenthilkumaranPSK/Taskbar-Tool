using System.Windows.Media.Imaging;
using Windows.Media.Control;

namespace TaskbarMediaWidget.Media;

/// <summary>Immutable snapshot of what to show for the currently active media session.</summary>
public sealed record NowPlayingInfo(
    string Title,
    string Artist,
    BitmapImage? Thumbnail,
    GlobalSystemMediaTransportControlsSessionPlaybackStatus PlaybackStatus,
    bool IsPreviousEnabled,
    bool IsPlayPauseEnabled,
    bool IsNextEnabled)
{
    public bool IsPlaying => PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
}
