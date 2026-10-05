using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using TaskbarMediaWidget.Core;
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
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Hide();
            }
        };

        ApplyTheme(ThemeDetector.IsSystemLightTheme());
    }

    public void UpdateNowPlaying(NowPlayingInfo? info)
    {
        _currentInfo = info;

        if (info is null)
        {
            TrackTitleText.Text = "Not Playing";
            ArtistText.Text = "Start media playback to see controls";
            AlbumText.Text = string.Empty;
            SourceAppText.Text = "Media";
            CoverArtImage.Source = null;
            ArtFallbackGlyph.Visibility = Visibility.Visible;
            CoverArtGlow.Visibility = Visibility.Collapsed;
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
        SourceAppText.Text = string.IsNullOrWhiteSpace(info.SourceApp) ? "Media Player" : info.SourceApp;

        if (info.Thumbnail is not null)
        {
            CoverArtImage.Source = info.Thumbnail;
            ArtFallbackGlyph.Visibility = Visibility.Collapsed;
            CoverArtGlow.Visibility = Visibility.Visible;
        }
        else
        {
            CoverArtImage.Source = null;
            ArtFallbackGlyph.Visibility = Visibility.Visible;
            CoverArtGlow.Visibility = Visibility.Collapsed;
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

        ApplyAccentColor(info.AccentColor);
    }

    private void ApplyAccentColor(System.Windows.Media.Color? accent)
    {
        var color = accent ?? System.Windows.Media.Color.FromRgb(0x60, 0xCD, 0xFF);
        var accentBrush = new SolidColorBrush(color);
        accentBrush.Freeze();

        Resources["AccentBrush"] = accentBrush;
        Resources["PlayButtonBgBrush"] = accentBrush;

        // Choose foreground for play button based on accent brightness
        var luminance = (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255.0;
        var fg = luminance > 0.55 ? Colors.Black : Colors.White;
        var fgBrush = new SolidColorBrush(fg);
        fgBrush.Freeze();
        Resources["PlayButtonFgBrush"] = fgBrush;
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

    /// <summary>
    /// Computes window position in logical DIPs directly from SystemParameters.WorkArea
    /// so the flyout is always 100% visible right above the taskbar, regardless of display DPI scale.
    /// </summary>
    public void PositionAbove(bool fromTray = false)
    {
        Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        var width = ActualWidth > 0 ? ActualWidth : (DesiredSize.Width > 0 ? DesiredSize.Width : 360);
        var height = ActualHeight > 0 ? ActualHeight : (DesiredSize.Height > 0 ? DesiredSize.Height : 360);

        var workArea = SystemParameters.WorkArea;

        // Exactly 10px above the taskbar (bottom of the work area in DIPs)
        var targetTop = workArea.Bottom - height - 10;

        double targetLeft;
        if (fromTray)
        {
            targetLeft = workArea.Right - width - 12;
        }
        else
        {
            targetLeft = workArea.Left + 12;
        }

        Left = Math.Max(workArea.Left + 8, Math.Min(targetLeft, workArea.Right - width - 8));
        Top = Math.Max(workArea.Top + 8, targetTop);
    }

    public void UpdateHardwareStats(int cpuPercent, int ramPercent)
    {
        HwStatsText.Text = $"CPU {cpuPercent}%  •  RAM {ramPercent}%";
    }

    public void ShowFlyout(bool fromTray = false)
    {
        ApplyTheme(ThemeDetector.IsSystemLightTheme());
        PositionAbove(fromTray);

        Opacity = 0;
        Show();
        Activate();

        var anim = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        BeginAnimation(OpacityProperty, anim);

        if (_currentInfo?.IsPlaying == true)
        {
            _progressTimer.Start();
        }
    }

    public void ToggleFlyout(bool fromTray = false)
    {
        if (IsVisible)
        {
            Hide();
        }
        else
        {
            ShowFlyout(fromTray);
        }
    }

    public void ApplyTheme(bool isLightTheme)
    {
        Resources["FlyoutBgBrush"] = new SolidColorBrush(isLightTheme
            ? System.Windows.Media.Color.FromArgb(0xF8, 0xF8, 0xF8, 0xF8)
            : System.Windows.Media.Color.FromArgb(0xF4, 0x1E, 0x1E, 0x20));

        Resources["FlyoutBorderBrush"] = new SolidColorBrush(isLightTheme
            ? System.Windows.Media.Color.FromArgb(0x28, 0x00, 0x00, 0x00)
            : System.Windows.Media.Color.FromArgb(0x2B, 0xFF, 0xFF, 0xFF));

        Resources["PrimaryTextBrush"] = new SolidColorBrush(isLightTheme ? System.Windows.Media.Color.FromRgb(0x18, 0x18, 0x18) : Colors.White);
        Resources["SecondaryTextBrush"] = new SolidColorBrush(isLightTheme
            ? System.Windows.Media.Color.FromArgb(0xBB, 0x00, 0x00, 0x00)
            : System.Windows.Media.Color.FromArgb(0xB8, 0xFF, 0xFF, 0xFF));
        Resources["TertiaryTextBrush"] = new SolidColorBrush(isLightTheme
            ? System.Windows.Media.Color.FromArgb(0x7A, 0x00, 0x00, 0x00)
            : System.Windows.Media.Color.FromArgb(0x7A, 0xFF, 0xFF, 0xFF));

        Resources["ButtonHoverBrush"] = new SolidColorBrush(isLightTheme
            ? System.Windows.Media.Color.FromArgb(0x14, 0x00, 0x00, 0x00)
            : System.Windows.Media.Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF));

        Resources["PillBadgeBgBrush"] = new SolidColorBrush(isLightTheme
            ? System.Windows.Media.Color.FromArgb(0x12, 0x00, 0x00, 0x00)
            : System.Windows.Media.Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));
    }

    private void SeekSlider_PreviewMouseDown(object sender, MouseButtonEventArgs e) => _isDraggingSlider = true;

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

    private void VolumeMuteButton_Click(object sender, RoutedEventArgs e) => VolumeHelper.ToggleMute();

    private void VolumeDownButton_Click(object sender, RoutedEventArgs e) => VolumeHelper.VolumeDown();

    private void VolumeUpButton_Click(object sender, RoutedEventArgs e) => VolumeHelper.VolumeUp();

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Hide();

    private void LaunchChrome_Click(object sender, RoutedEventArgs e)
    {
        QuickLaunchHelper.LaunchChrome();
        Hide();
    }

    private void LaunchExplorer_Click(object sender, RoutedEventArgs e)
    {
        QuickLaunchHelper.LaunchExplorer();
        Hide();
    }

    private void LaunchTerminal_Click(object sender, RoutedEventArgs e)
    {
        QuickLaunchHelper.LaunchTerminal();
        Hide();
    }

    private void LaunchSnippingTool_Click(object sender, RoutedEventArgs e)
    {
        QuickLaunchHelper.LaunchSnippingTool();
        Hide();
    }

    private void LaunchNotepad_Click(object sender, RoutedEventArgs e)
    {
        QuickLaunchHelper.LaunchNotepad();
        Hide();
    }

    private void LaunchCalculator_Click(object sender, RoutedEventArgs e)
    {
        QuickLaunchHelper.LaunchCalculator();
        Hide();
    }

    private void LaunchTaskManager_Click(object sender, RoutedEventArgs e)
    {
        QuickLaunchHelper.LaunchTaskManager();
        Hide();
    }

    private void LockWorkstation_Click(object sender, RoutedEventArgs e)
    {
        QuickLaunchHelper.LockWorkstation();
        Hide();
    }

    private void HwBadge_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            QuickLaunchHelper.LaunchTaskManager();
            Hide();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _progressTimer.Stop();
        base.OnClosed(e);
    }
}
