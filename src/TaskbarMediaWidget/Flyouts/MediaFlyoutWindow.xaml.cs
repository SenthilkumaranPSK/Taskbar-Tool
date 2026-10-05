using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TaskbarMediaWidget.Interop;
using TaskbarMediaWidget.Media;

namespace TaskbarMediaWidget.Flyouts;

public partial class MediaFlyoutWindow : Window
{
    private const string PlayGlyph = "";
    private const string PauseGlyph = "";

    private readonly DispatcherTimer _progressTimer;
    private NowPlayingInfo? _currentInfo;
    private bool _isDraggingSlider;

    public event EventHandler? PreviousRequested;
    public event EventHandler? PlayPauseRequested;
    public event EventHandler? NextRequested;
    public event EventHandler? ShuffleRequested;
    public event EventHandler? RepeatRequested;
    public event Action<TimeSpan>? SeekRequested;

    public MediaFlyoutWindow()
    {
        InitializeComponent();

        _progressTimer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _progressTimer.Tick += OnProgressTimerTick;

        Deactivated += (_, _) => Hide();
        ApplyTheme(ThemeDetector.IsSystemLightTheme());
    }

    public void UpdateNowPlaying(NowPlayingInfo? info)
    {
        _currentInfo = info;

        if (info is null)
        {
            TrackTitleText.Text = "Not playing";
            ArtistText.Text = "No active media session";
            AlbumText.Text = string.Empty;
            SourceAppText.Text = "Media Player";
            CoverArtImage.Source = null;
            ArtFallbackGlyph.Visibility = Visibility.Visible;
            SeekSlider.IsEnabled = false;
            SeekSlider.Value = 0;
            CurrentTimeText.Text = "0:00";
            TotalTimeText.Text = "0:00";
            PlayPauseButton.Content = PlayGlyph;
            _progressTimer.Stop();
            return;
        }

        TrackTitleText.Text = string.IsNullOrWhiteSpace(info.Title) ? "Unknown Track" : info.Title;
        ArtistText.Text = string.IsNullOrWhiteSpace(info.Artist) ? "Unknown Artist" : info.Artist;
        AlbumText.Text = info.AlbumTitle;
        SourceAppText.Text = info.SourceApp;

        if (info.Thumbnail is not null)
        {
            CoverArtImage.Source = info.Thumbnail;
            ArtFallbackGlyph.Visibility = Visibility.Collapsed;
        }
        else
        {
            CoverArtImage.Source = null;
            ArtFallbackGlyph.Visibility = Visibility.Visible;
        }

        PlayPauseButton.Content = info.IsPlaying ? PauseGlyph : PlayGlyph;
        PlayPauseButton.IsEnabled = info.IsPlayPauseEnabled;
        PrevButton.IsEnabled = info.IsPreviousEnabled;
        NextButton.IsEnabled = info.IsNextEnabled;

        ShuffleButton.Opacity = (info.IsShuffleActive == true) ? 1.0 : 0.4;
        RepeatButton.Opacity = (info.AutoRepeatMode is not null and not Windows.Media.MediaPlaybackAutoRepeatMode.None) ? 1.0 : 0.4;

        if (!_isDraggingSlider)
        {
            UpdateTimeline(info);
        }

        if (info.IsPlaying && IsVisible)
        {
            if (!_progressTimer.IsEnabled)
            {
                _progressTimer.Start();
            }
        }
        else
        {
            _progressTimer.Stop();
        }

        if (info.AccentColor is { } accent)
        {
            Resources["AccentBrush"] = new SolidColorBrush(accent);
        }
    }

    private void OnProgressTimerTick(object? sender, EventArgs e)
    {
        if (_currentInfo is not null && !_isDraggingSlider)
        {
            UpdateTimeline(_currentInfo);
        }
    }

    private void UpdateTimeline(NowPlayingInfo info)
    {
        var duration = info.Duration.TotalSeconds;
        SeekSlider.IsEnabled = info.CanSeek && duration > 0;

        if (duration > 0)
        {
            SeekSlider.Maximum = duration;
            SeekSlider.Value = info.CurrentPosition.TotalSeconds;
            CurrentTimeText.Text = NowPlayingInfo.FormatTime(info.CurrentPosition);
            TotalTimeText.Text = NowPlayingInfo.FormatTime(info.Duration);
        }
        else
        {
            SeekSlider.Maximum = 100;
            SeekSlider.Value = 0;
            CurrentTimeText.Text = "0:00";
            TotalTimeText.Text = "0:00";
        }
    }

    public void PositionAbove(int screenX, int screenY)
    {
        Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        var width = DesiredSize.Width > 0 ? DesiredSize.Width : 350;
        var height = DesiredSize.Height > 0 ? DesiredSize.Height : 220;

        var screenWidth = SystemParameters.PrimaryScreenWidth;
        var screenHeight = SystemParameters.PrimaryScreenHeight;

        var targetLeft = Math.Max(12, Math.Min(screenX, screenWidth - width - 12));
        var targetTop = Math.Max(12, screenY - height - 8);

        Left = targetLeft;
        Top = targetTop;
    }

    public void ShowFlyout(int screenX, int screenY)
    {
        ApplyTheme(ThemeDetector.IsSystemLightTheme());
        PositionAbove(screenX, screenY);
        Show();
        Activate();

        if (_currentInfo?.IsPlaying == true)
        {
            _progressTimer.Start();
        }
    }

    public void ToggleFlyout(int screenX, int screenY)
    {
        if (IsVisible)
        {
            Hide();
        }
        else
        {
            ShowFlyout(screenX, screenY);
        }
    }

    public void ApplyTheme(bool isLightTheme)
    {
        Resources["FlyoutBgBrush"] = new SolidColorBrush(isLightTheme
            ? System.Windows.Media.Color.FromArgb(0xF4, 0xF5, 0xF5, 0xF5)
            : System.Windows.Media.Color.FromArgb(0xF4, 0x1E, 0x1E, 0x1E));

        Resources["FlyoutBorderBrush"] = new SolidColorBrush(isLightTheme
            ? System.Windows.Media.Color.FromArgb(0x33, 0x00, 0x00, 0x00)
            : System.Windows.Media.Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));

        Resources["PrimaryTextBrush"] = new SolidColorBrush(isLightTheme ? Colors.Black : Colors.White);
        Resources["SecondaryTextBrush"] = new SolidColorBrush(isLightTheme
            ? System.Windows.Media.Color.FromArgb(0xA0, 0x00, 0x00, 0x00)
            : System.Windows.Media.Color.FromArgb(0xA0, 0xFF, 0xFF, 0xFF));

        Resources["HoverBrush"] = new SolidColorBrush(isLightTheme
            ? System.Windows.Media.Color.FromArgb(0x18, 0x00, 0x00, 0x00)
            : System.Windows.Media.Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
    }

    private void SeekSlider_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _isDraggingSlider = true;
    }

    private void SeekSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        _isDraggingSlider = false;
        SeekRequested?.Invoke(TimeSpan.FromSeconds(SeekSlider.Value));
    }

    private void PrevButton_Click(object sender, RoutedEventArgs e) => PreviousRequested?.Invoke(this, EventArgs.Empty);

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e) => PlayPauseRequested?.Invoke(this, EventArgs.Empty);

    private void NextButton_Click(object sender, RoutedEventArgs e) => NextRequested?.Invoke(this, EventArgs.Empty);

    private void ShuffleButton_Click(object sender, RoutedEventArgs e) => ShuffleRequested?.Invoke(this, EventArgs.Empty);

    private void RepeatButton_Click(object sender, RoutedEventArgs e) => RepeatRequested?.Invoke(this, EventArgs.Empty);

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Hide();

    protected override void OnClosed(EventArgs e)
    {
        _progressTimer.Stop();
        base.OnClosed(e);
    }
}
