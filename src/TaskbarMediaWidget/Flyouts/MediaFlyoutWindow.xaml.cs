using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using TaskbarMediaWidget.Core;
using TaskbarMediaWidget.Interop;
using TaskbarMediaWidget.Lyrics;
using TaskbarMediaWidget.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Point = System.Windows.Point;
using Cursors = System.Windows.Input.Cursors;

namespace TaskbarMediaWidget.Flyouts;

public partial class MediaFlyoutWindow : Window
{
    private const string PlayGlyph = "";
    private const string PauseGlyph = "";
    private const string MuteGlyph = "";
    private const string UnmuteGlyph = "";

    private readonly DispatcherTimer _progressTimer;
    private readonly LrcLibService _lyricsService = new();
    private NowPlayingInfo? _currentInfo;
    private bool _isDraggingSlider;
    private bool _isUpdatingVolumeInternally;
    private DateTime _lastDeactivatedTime = DateTime.MinValue;

    private int _currentTabIndex = 0; // 0: Player, 1: Lyrics, 2: Tools
    private LyricsData? _loadedLyrics;
    private string? _currentLyricsKey;
    private int _activeLyricIndex = -1;

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

        Deactivated += (_, _) =>
        {
            _lastDeactivatedTime = DateTime.UtcNow;
            Hide();
        };

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Hide();
            }
        };

        VolumeHelper.Current.VolumeChanged += OnExternalVolumeChanged;

        ApplyTheme(ThemeDetector.IsSystemLightTheme());
        SyncVolumeUi();
        UpdateActiveTabUi();
    }

    private void SyncVolumeUi()
    {
        _isUpdatingVolumeInternally = true;
        try
        {
            var vol = VolumeHelper.GetVolumePercent();
            var isMuted = VolumeHelper.IsMuted();
            MasterVolumeSlider.Value = vol;
            VolumePercentText.Text = $"{vol}%";
            VolumeMuteButton.Content = isMuted ? MuteGlyph : UnmuteGlyph;
            VolumeMuteButton.Opacity = isMuted ? 0.5 : 1.0;
        }
        finally
        {
            _isUpdatingVolumeInternally = false;
        }
    }

    private void OnExternalVolumeChanged(int volume, bool isMuted)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (_isUpdatingVolumeInternally) return;
            _isUpdatingVolumeInternally = true;
            try
            {
                MasterVolumeSlider.Value = volume;
                VolumePercentText.Text = $"{volume}%";
                VolumeMuteButton.Content = isMuted ? MuteGlyph : UnmuteGlyph;
                VolumeMuteButton.Opacity = isMuted ? 0.5 : 1.0;
            }
            finally
            {
                _isUpdatingVolumeInternally = false;
            }
        });
    }

    public void UpdateNowPlaying(NowPlayingInfo? info)
    {
        var trackChanged = _currentInfo?.Title != info?.Title || _currentInfo?.Artist != info?.Artist;
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

            ResetLyricsUi("Start media playback to load lyrics.");
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

        if (trackChanged)
        {
            _ = LoadLyricsAsync(info);
        }
    }

    private void ApplyAccentColor(System.Windows.Media.Color? accent)
    {
        var color = accent ?? System.Windows.Media.Color.FromRgb(0x60, 0xCD, 0xFF);
        var accentBrush = new SolidColorBrush(color);
        accentBrush.Freeze();

        Resources["AccentBrush"] = accentBrush;
        Resources["PlayButtonBgBrush"] = accentBrush;

        var luminance = (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255.0;
        var fg = luminance > 0.55 ? Colors.Black : Colors.White;
        var fgBrush = new SolidColorBrush(fg);
        fgBrush.Freeze();
        Resources["PlayButtonFgBrush"] = fgBrush;
    }

    private void OnProgressTimerTick(object? sender, EventArgs e)
    {
        if (_currentInfo is not null)
        {
            if (!_isDraggingSlider)
            {
                UpdateTimeline(_currentInfo);
            }

            if (_currentTabIndex == 1 && _loadedLyrics?.IsSynced == true)
            {
                HighlightCurrentLyric(_currentInfo.CurrentPosition);
            }
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

    private async Task LoadLyricsAsync(NowPlayingInfo info)
    {
        var key = $"{info.Title}|{info.Artist}";
        if (_currentLyricsKey == key) return;
        _currentLyricsKey = key;

        ResetLyricsUi($"Searching lyrics for \"{info.Title}\"...");

        var result = await _lyricsService.GetLyricsAsync(info.Title, info.Artist, info.Duration);
        if (_currentLyricsKey != key) return; // Stale

        _loadedLyrics = result;
        _activeLyricIndex = -1;

        if (result is null || (!result.IsSynced && string.IsNullOrWhiteSpace(result.PlainLyrics)))
        {
            ResetLyricsUi($"No lyrics found for \"{info.Title}\".");
            return;
        }

        LyricsHeaderStatus.Text = result.IsSynced ? "Synchronized Live Lyrics" : "Plain Lyrics";
        LyricsStackPanel.Children.Clear();

        if (result.IsSynced)
        {
            for (int i = 0; i < result.SyncedLines.Count; i++)
            {
                var line = result.SyncedLines[i];
                var tb = new TextBlock
                {
                    Text = line.Text,
                    FontSize = 13.5,
                    FontWeight = FontWeights.Normal,
                    Foreground = (Brush)Resources["SecondaryTextBrush"],
                    TextAlignment = TextAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 6, 0, 6),
                    Tag = i,
                    Cursor = Cursors.Hand,
                };
                tb.MouseDown += (_, _) => _ = RunSeekToLyric(line.Timestamp);
                LyricsStackPanel.Children.Add(tb);
            }

            HighlightCurrentLyric(info.CurrentPosition);
        }
        else
        {
            var plainLines = result.PlainLyrics.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var pl in plainLines)
            {
                LyricsStackPanel.Children.Add(new TextBlock
                {
                    Text = pl,
                    FontSize = 12.5,
                    Foreground = (Brush)Resources["SecondaryTextBrush"],
                    TextAlignment = TextAlignment.Center,
                    Margin = new Thickness(0, 4, 0, 4),
                });
            }
        }
    }

    private void ResetLyricsUi(string statusMessage)
    {
        _loadedLyrics = null;
        _activeLyricIndex = -1;
        LyricsHeaderStatus.Text = statusMessage;
        LyricsStackPanel.Children.Clear();
    }

    private async Task RunSeekToLyric(TimeSpan target)
    {
        SeekRequested?.Invoke(target);
        await Task.CompletedTask;
    }

    private void HighlightCurrentLyric(TimeSpan position)
    {
        if (_loadedLyrics is null || !_loadedLyrics.IsSynced || LyricsStackPanel.Children.Count == 0)
        {
            return;
        }

        var lines = _loadedLyrics.SyncedLines;
        var foundIndex = -1;

        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].Timestamp <= position)
            {
                foundIndex = i;
            }
            else
            {
                break;
            }
        }

        if (foundIndex == _activeLyricIndex || foundIndex < 0 || foundIndex >= LyricsStackPanel.Children.Count)
        {
            return;
        }

        // Dim previous
        if (_activeLyricIndex >= 0 && _activeLyricIndex < LyricsStackPanel.Children.Count)
        {
            if (LyricsStackPanel.Children[_activeLyricIndex] is TextBlock prevTb)
            {
                prevTb.Foreground = (Brush)Resources["SecondaryTextBrush"];
                prevTb.FontSize = 13.5;
                prevTb.FontWeight = FontWeights.Normal;
            }
        }

        // Highlight new
        _activeLyricIndex = foundIndex;
        if (LyricsStackPanel.Children[_activeLyricIndex] is TextBlock activeTb)
        {
            activeTb.Foreground = (Brush)Resources["AccentBrush"];
            activeTb.FontSize = 15.5;
            activeTb.FontWeight = FontWeights.SemiBold;

            // Auto-scroll into view smoothly
            try
            {
                var targetY = activeTb.TransformToAncestor(LyricsStackPanel).Transform(new Point(0, 0)).Y;
                var viewHeight = LyricsScrollViewer.ActualHeight > 0 ? LyricsScrollViewer.ActualHeight : 200;
                var scrollOffset = Math.Max(0, targetY - (viewHeight / 2) + 20);
                LyricsScrollViewer.ScrollToVerticalOffset(scrollOffset);
            }
            catch
            {
                // Layout pass not yet completed
            }
        }
    }

    // Tab Switching
    private void TabPlayerBtn_Click(object sender, RoutedEventArgs e) => SwitchTab(0);
    private void TabLyricsBtn_Click(object sender, RoutedEventArgs e) => SwitchTab(1);
    private void TabToolsBtn_Click(object sender, RoutedEventArgs e) => SwitchTab(2);

    private void SwitchTab(int index)
    {
        _currentTabIndex = index;
        UpdateActiveTabUi();

        if (index == 1 && _currentInfo is not null && _loadedLyrics?.IsSynced == true)
        {
            HighlightCurrentLyric(_currentInfo.CurrentPosition);
        }
    }

    private void UpdateActiveTabUi()
    {
        TabPlayerPanel.Visibility = _currentTabIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        TabLyricsPanel.Visibility = _currentTabIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        TabToolsPanel.Visibility = _currentTabIndex == 2 ? Visibility.Visible : Visibility.Collapsed;

        var activeBg = (Brush)Resources["ButtonHoverBrush"];
        var inactiveBg = Brushes.Transparent;

        TabPlayerBtn.Background = _currentTabIndex == 0 ? activeBg : inactiveBg;
        TabLyricsBtn.Background = _currentTabIndex == 1 ? activeBg : inactiveBg;
        TabToolsBtn.Background = _currentTabIndex == 2 ? activeBg : inactiveBg;

        TabPlayerBtn.Foreground = (Brush)(_currentTabIndex == 0 ? Resources["PrimaryTextBrush"] : Resources["SecondaryTextBrush"]);
        TabLyricsBtn.Foreground = (Brush)(_currentTabIndex == 1 ? Resources["PrimaryTextBrush"] : Resources["SecondaryTextBrush"]);
        TabToolsBtn.Foreground = (Brush)(_currentTabIndex == 2 ? Resources["PrimaryTextBrush"] : Resources["SecondaryTextBrush"]);
    }

    public void PositionAbove(bool fromTray = false)
    {
        Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        var width = ActualWidth > 0 ? ActualWidth : (DesiredSize.Width > 0 ? DesiredSize.Width : 365);
        var height = ActualHeight > 0 ? ActualHeight : (DesiredSize.Height > 0 ? DesiredSize.Height : 380);

        var workArea = SystemParameters.WorkArea;
        var targetTop = workArea.Bottom - height - 10;

        double targetLeft = fromTray
            ? workArea.Right - width - 12
            : workArea.Left + 12;

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
        SyncVolumeUi();
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
            return;
        }

        if ((DateTime.UtcNow - _lastDeactivatedTime).TotalMilliseconds < 300)
        {
            return;
        }

        ShowFlyout(fromTray);
    }

    public void ApplyTheme(bool isLightTheme)
    {
        var isTransparent = AppSettings.TransparencyMode;

        Resources["FlyoutBgBrush"] = new SolidColorBrush(isLightTheme
            ? (isTransparent ? System.Windows.Media.Color.FromArgb(0xD2, 0xF8, 0xF8, 0xFA) : System.Windows.Media.Color.FromArgb(0xF8, 0xF8, 0xF8, 0xF8))
            : (isTransparent ? System.Windows.Media.Color.FromArgb(0xBC, 0x14, 0x14, 0x18) : System.Windows.Media.Color.FromArgb(0xF4, 0x1E, 0x1E, 0x20)));

        Resources["FlyoutBorderBrush"] = new SolidColorBrush(isLightTheme
            ? (isTransparent ? System.Windows.Media.Color.FromArgb(0x38, 0x00, 0x00, 0x00) : System.Windows.Media.Color.FromArgb(0x28, 0x00, 0x00, 0x00))
            : (isTransparent ? System.Windows.Media.Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF) : System.Windows.Media.Color.FromArgb(0x2B, 0xFF, 0xFF, 0xFF)));

        Resources["PrimaryTextBrush"] = new SolidColorBrush(isLightTheme ? System.Windows.Media.Color.FromRgb(0x18, 0x18, 0x18) : Colors.White);
        Resources["SecondaryTextBrush"] = new SolidColorBrush(isLightTheme
            ? System.Windows.Media.Color.FromArgb(0xBB, 0x00, 0x00, 0x00)
            : System.Windows.Media.Color.FromArgb(0xB8, 0xFF, 0xFF, 0xFF));
        Resources["TertiaryTextBrush"] = new SolidColorBrush(isLightTheme
            ? System.Windows.Media.Color.FromArgb(0x7A, 0x00, 0x00, 0x00)
            : System.Windows.Media.Color.FromArgb(0x7A, 0xFF, 0xFF, 0xFF));

        Resources["ButtonHoverBrush"] = new SolidColorBrush(isLightTheme
            ? System.Windows.Media.Color.FromArgb(0x18, 0x00, 0x00, 0x00)
            : System.Windows.Media.Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));

        Resources["PillBadgeBgBrush"] = new SolidColorBrush(isLightTheme
            ? (isTransparent ? System.Windows.Media.Color.FromArgb(0x18, 0x00, 0x00, 0x00) : System.Windows.Media.Color.FromArgb(0x12, 0x00, 0x00, 0x00))
            : (isTransparent ? System.Windows.Media.Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF) : System.Windows.Media.Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)));

        UpdateActiveTabUi();
    }

    private void MasterVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isUpdatingVolumeInternally) return;
        var val = (int)Math.Round(e.NewValue);
        VolumePercentText.Text = $"{val}%";
        VolumeHelper.SetVolumePercent(val);
    }

    private void VolumeMuteButton_Click(object sender, RoutedEventArgs e)
    {
        VolumeHelper.ToggleMute();
        SyncVolumeUi();
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

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Hide();

    private void LaunchChrome_Click(object sender, RoutedEventArgs e) { QuickLaunchHelper.LaunchChrome(); Hide(); }
    private void LaunchExplorer_Click(object sender, RoutedEventArgs e) { QuickLaunchHelper.LaunchExplorer(); Hide(); }
    private void LaunchTerminal_Click(object sender, RoutedEventArgs e) { QuickLaunchHelper.LaunchTerminal(); Hide(); }
    private void LaunchSnippingTool_Click(object sender, RoutedEventArgs e) { QuickLaunchHelper.LaunchSnippingTool(); Hide(); }
    private void LaunchNotepad_Click(object sender, RoutedEventArgs e) { QuickLaunchHelper.LaunchNotepad(); Hide(); }
    private void LaunchCalculator_Click(object sender, RoutedEventArgs e) { QuickLaunchHelper.LaunchCalculator(); Hide(); }
    private void LaunchSoundSettings_Click(object sender, RoutedEventArgs e) { QuickLaunchHelper.TryStartUrl("ms-settings:sound"); Hide(); }
    private void LockWorkstation_Click(object sender, RoutedEventArgs e) { QuickLaunchHelper.LockWorkstation(); Hide(); }

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
        VolumeHelper.Current.VolumeChanged -= OnExternalVolumeChanged;
        base.OnClosed(e);
    }
}
