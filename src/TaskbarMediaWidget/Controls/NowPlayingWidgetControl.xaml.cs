using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using TaskbarMediaWidget.Core;
using TaskbarMediaWidget.Media;

namespace TaskbarMediaWidget.Controls;

public partial class NowPlayingWidgetControl : System.Windows.Controls.UserControl
{
    private const double ScrollPixelsPerSecond = 30;
    private const string PlayGlyph = "";
    private const string PauseGlyph = "";

    private readonly DispatcherTimer _timelineTimer;
    private Storyboard? _equalizerStoryboard;

    private NowPlayingInfo? _currentInfo;
    private string? _lastTitle;
    private string? _lastArtist;
    private System.Windows.Media.Color? _accentColor;
    private bool _isLightTheme;
    private bool _isEqualizerAnimating;

    public event EventHandler? PreviousRequested;
    public event EventHandler? PlayPauseRequested;
    public event EventHandler? NextRequested;
    public event EventHandler? FlyoutToggleRequested;

    public NowPlayingWidgetControl()
    {
        InitializeComponent();

        _timelineTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _timelineTimer.Tick += OnTimelineTimerTick;

        ProgressBarTrack.SizeChanged += (_, _) =>
        {
            if (_currentInfo is not null && _currentInfo.Duration > TimeSpan.Zero)
            {
                UpdateProgressBar(_currentInfo.ProgressFraction);
            }
        };

        CreateEqualizerStoryboard();
    }

    /// <summary>
    /// Bumped whenever the displayed title/artist actually changes — i.e. whenever the natural
    /// size of the content could have changed. The host window uses this to skip re-measuring
    /// and re-applying its own size/position on poll ticks where nothing changed.
    /// </summary>
    public int ContentVersion { get; private set; }

    /// <summary>Updates the widget's content. Pass null to represent "nothing playing."</summary>
    public void SetNowPlaying(NowPlayingInfo? info)
    {
        _currentInfo = info;

        if (info is null)
        {
            if (AppSettings.HideWhenIdle)
            {
                Visibility = Visibility.Collapsed;
                StopMarquee();
                StopEqualizer();
                _timelineTimer.Stop();
                ApplyAccent(null);
                _lastTitle = null;
                _lastArtist = null;
                return;
            }

            // Standby mode: clean, compact docked launcher indicating the tool is active
            Visibility = Visibility.Visible;
            StopMarquee();
            StopEqualizer();
            _timelineTimer.Stop();
            ApplyAccent(null);

            ProgressBarTrack.Visibility = Visibility.Collapsed;
            MediaContentGrid.Visibility = Visibility.Collapsed;
            StandbyLauncherPanel.Visibility = Visibility.Visible;

            ToolTip = "Taskbar Tool • Standby Launcher\nClick shortcuts to open apps | Click icon for Flyout";

            if (_lastTitle != null)
            {
                _lastTitle = null;
                _lastArtist = null;
                ContentVersion++;
            }

            return;
        }

        Visibility = Visibility.Visible;
        StandbyLauncherPanel.Visibility = Visibility.Collapsed;
        MediaContentGrid.Visibility = Visibility.Visible;
        TransportPanel.Visibility = Visibility.Visible;

        var title = string.IsNullOrWhiteSpace(info.Title) ? "Not playing" : info.Title;
        var isNewTrack = title != _lastTitle || info.Artist != _lastArtist;

        TitleText.Text = title;
        ArtistText.Text = info.Artist;

        if (info.Thumbnail is not null)
        {
            CoverArt.Source = info.Thumbnail;
            ArtFallback.Visibility = Visibility.Collapsed;
        }
        else
        {
            CoverArt.Source = null;
            ArtFallback.Visibility = Visibility.Visible;
        }

        PreviousButton.IsEnabled = info.IsPreviousEnabled;
        NextButton.IsEnabled = info.IsNextEnabled;
        PlayPauseButton.IsEnabled = info.IsPlayPauseEnabled;
        PlayPauseButton.Content = info.IsPlaying ? PauseGlyph : PlayGlyph;
        PlayPauseButton.SetValue(AutomationProperties.NameProperty, info.IsPlaying ? "Pause" : "Play");

        ApplyAccent(info.AccentColor);

        // Tooltip feedback
        var timeStr = info.Duration > TimeSpan.Zero
            ? $"\n⏱ {NowPlayingInfo.FormatTime(info.CurrentPosition)} / {NowPlayingInfo.FormatTime(info.Duration)}"
            : string.Empty;
        ToolTip = $"🎵 {title}\n👤 {info.Artist}{timeStr}\n🔊 Scroll: Volume | Left Click: Open Controls";

        // Equalizer animation
        EqualizerBars.Visibility = Visibility.Visible;
        if (info.IsPlaying)
        {
            StartEqualizer();
        }
        else
        {
            PauseEqualizer();
        }

        // Track Progress Bar
        if (info.Duration > TimeSpan.Zero)
        {
            ProgressBarTrack.Visibility = Visibility.Visible;
            UpdateProgressBar(info.ProgressFraction);

            if (info.IsPlaying && !_timelineTimer.IsEnabled)
            {
                _timelineTimer.Start();
            }
            else if (!info.IsPlaying)
            {
                _timelineTimer.Stop();
            }
        }
        else
        {
            ProgressBarTrack.Visibility = Visibility.Collapsed;
            _timelineTimer.Stop();
        }

        if (isNewTrack)
        {
            _lastTitle = title;
            _lastArtist = info.Artist;
            ContentVersion++;
        }

        if (!info.IsPlaying)
        {
            StopMarquee();
        }
        else if (isNewTrack)
        {
            RestartMarquee();
        }
    }

    private void OnTimelineTimerTick(object? sender, EventArgs e)
    {
        if (_currentInfo is not null && _currentInfo.IsPlaying && _currentInfo.Duration > TimeSpan.Zero)
        {
            UpdateProgressBar(_currentInfo.ProgressFraction);
        }
    }

    private void UpdateProgressBar(double fraction)
    {
        var trackWidth = ProgressBarTrack.ActualWidth;
        if (trackWidth <= 0)
        {
            return;
        }

        ProgressBarFill.Width = Math.Clamp(trackWidth * fraction, 0, trackWidth);
    }

    /// <summary>Natural width of the widget's content, for the host window to size itself to.</summary>
    public System.Windows.Size MeasureNaturalSize()
    {
        Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        return DesiredSize;
    }

    /// <summary>Stops the scrolling marquee in place — call whenever the host window is hidden.</summary>
    public void StopMarquee() => TextStack.BeginAnimation(Canvas.LeftProperty, null);

    /// <summary>Swaps the light/dark text and hover brushes to match the taskbar's current theme.</summary>
    public void ApplyTheme(bool isLightTheme)
    {
        _isLightTheme = isLightTheme;
        Resources["PrimaryTextBrush"] = new SolidColorBrush(isLightTheme ? Colors.Black : Colors.White);
        Resources["SecondaryTextBrush"] = new SolidColorBrush(isLightTheme
            ? System.Windows.Media.Color.FromArgb(0xBB, 0x00, 0x00, 0x00)
            : System.Windows.Media.Color.FromArgb(0xBB, 0xFF, 0xFF, 0xFF));
        Resources["HoverBrush"] = new SolidColorBrush(isLightTheme
            ? System.Windows.Media.Color.FromArgb(0x22, 0x00, 0x00, 0x00)
            : System.Windows.Media.Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));

        Resources["PillBaseBgBrush"] = new SolidColorBrush(isLightTheme
            ? System.Windows.Media.Color.FromArgb(0x16, 0x00, 0x00, 0x00)
            : System.Windows.Media.Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF));

        Resources["PillHoverBgBrush"] = new SolidColorBrush(isLightTheme
            ? System.Windows.Media.Color.FromArgb(0x28, 0x00, 0x00, 0x00)
            : System.Windows.Media.Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF));

        Resources["PillBaseBorderBrush"] = new SolidColorBrush(isLightTheme
            ? System.Windows.Media.Color.FromArgb(0x22, 0x00, 0x00, 0x00)
            : System.Windows.Media.Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));

        ApplyAccent(_accentColor, force: true);
    }

    private void ApplyAccent(System.Windows.Media.Color? accent, bool force = false)
    {
        if (!force && Nullable.Equals(_accentColor, accent))
        {
            return;
        }

        _accentColor = accent;

        if (accent is not { } color)
        {
            Resources["AccentBrush"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x60, 0xCD, 0xFF));
            PillBorder.Background = (System.Windows.Media.Brush)Resources["PillBaseBgBrush"];
            PillBorder.BorderBrush = (System.Windows.Media.Brush)Resources["PillBaseBorderBrush"];
            return;
        }

        Resources["AccentBrush"] = new SolidColorBrush(color);

        var (fillStart, fillEnd, edge) = _isLightTheme
            ? ((byte)0x45, (byte)0x15, (byte)0x66)
            : ((byte)0x55, (byte)0x1C, (byte)0x77);

        var fill = new LinearGradientBrush
        {
            StartPoint = new System.Windows.Point(0, 0),
            EndPoint = new System.Windows.Point(1, 0),
        };
        fill.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(fillStart, color.R, color.G, color.B), 0));
        fill.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(fillEnd, color.R, color.G, color.B), 1));
        fill.Freeze();
        PillBorder.Background = fill;

        var stroke = new SolidColorBrush(System.Windows.Media.Color.FromArgb(edge, color.R, color.G, color.B));
        stroke.Freeze();
        PillBorder.BorderBrush = stroke;
    }

    private void CreateEqualizerStoryboard()
    {
        _equalizerStoryboard = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };

        var anim1 = new DoubleAnimation(4, 11, TimeSpan.FromMilliseconds(320)) { AutoReverse = true };
        Storyboard.SetTarget(anim1, EqBar1);
        Storyboard.SetTargetProperty(anim1, new PropertyPath(FrameworkElement.HeightProperty));
        _equalizerStoryboard.Children.Add(anim1);

        var anim2 = new DoubleAnimation(5, 13, TimeSpan.FromMilliseconds(260)) { AutoReverse = true };
        Storyboard.SetTarget(anim2, EqBar2);
        Storyboard.SetTargetProperty(anim2, new PropertyPath(FrameworkElement.HeightProperty));
        _equalizerStoryboard.Children.Add(anim2);

        var anim3 = new DoubleAnimation(3, 9, TimeSpan.FromMilliseconds(380)) { AutoReverse = true };
        Storyboard.SetTarget(anim3, EqBar3);
        Storyboard.SetTargetProperty(anim3, new PropertyPath(FrameworkElement.HeightProperty));
        _equalizerStoryboard.Children.Add(anim3);
    }

    private void StartEqualizer()
    {
        if (!_isEqualizerAnimating)
        {
            _equalizerStoryboard?.Begin(this, isControllable: true);
            _isEqualizerAnimating = true;
        }
    }

    private void PauseEqualizer()
    {
        if (_isEqualizerAnimating)
        {
            _equalizerStoryboard?.Stop(this);
            _isEqualizerAnimating = false;
        }

        EqBar1.Height = 3;
        EqBar2.Height = 4;
        EqBar3.Height = 2;
    }

    private void StopEqualizer()
    {
        if (_isEqualizerAnimating)
        {
            _equalizerStoryboard?.Stop(this);
            _isEqualizerAnimating = false;
        }
    }

    private void RestartMarquee()
    {
        TextStack.BeginAnimation(Canvas.LeftProperty, null);
        Canvas.SetLeft(TextStack, 0);

        Dispatcher.InvokeAsync(() =>
        {
            TextStack.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            var overflow = TextStack.DesiredSize.Width - TextClip.Width;
            if (overflow <= 0)
            {
                return;
            }

            var durationSeconds = overflow / ScrollPixelsPerSecond;
            var animation = new DoubleAnimation
            {
                From = 0,
                To = -overflow,
                Duration = TimeSpan.FromSeconds(durationSeconds),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                BeginTime = TimeSpan.FromSeconds(1.5),
            };
            TextStack.BeginAnimation(Canvas.LeftProperty, animation);
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta > 0)
        {
            VolumeHelper.VolumeUp();
        }
        else if (e.Delta < 0)
        {
            VolumeHelper.VolumeDown();
        }

        e.Handled = true;
    }

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle)
        {
            VolumeHelper.ToggleMute();
            e.Handled = true;
            return;
        }

        if (e.ChangedButton == MouseButton.Left)
        {
            // Do not trigger flyout if clicking on one of the transport buttons
            if (e.OriginalSource is DependencyObject dep && FindParentButton(dep) is not null)
            {
                return;
            }

            FlyoutToggleRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
    }

    private static System.Windows.Controls.Button? FindParentButton(DependencyObject? current)
    {
        while (current is not null)
        {
            if (current is System.Windows.Controls.Button btn)
            {
                return btn;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private void PreviousButton_Click(object sender, RoutedEventArgs e) => PreviousRequested?.Invoke(this, EventArgs.Empty);

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e) => PlayPauseRequested?.Invoke(this, EventArgs.Empty);

    private void NextButton_Click(object sender, RoutedEventArgs e) => NextRequested?.Invoke(this, EventArgs.Empty);

    public void UpdateHardwareStats(int cpuPercent, int ramPercent)
    {
        StandbyCpuText.Text = $"CPU {cpuPercent}%";
        StandbyCpuBadge.ToolTip = $"CPU: {cpuPercent}% | RAM: {ramPercent}%\nClick to open Task Manager";
    }

    private void StandbyChromeBtn_Click(object sender, RoutedEventArgs e) => QuickLaunchHelper.LaunchChrome();

    private void StandbyExplorerBtn_Click(object sender, RoutedEventArgs e) => QuickLaunchHelper.LaunchExplorer();

    private void StandbyTerminalBtn_Click(object sender, RoutedEventArgs e) => QuickLaunchHelper.LaunchTerminal();

    private void StandbySnipBtn_Click(object sender, RoutedEventArgs e) => QuickLaunchHelper.LaunchSnippingTool();

    private void StandbyCpuBadge_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            QuickLaunchHelper.LaunchTaskManager();
            e.Handled = true;
        }
    }

    private void OnMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_accentColor is null)
        {
            PillBorder.Background = (System.Windows.Media.Brush)Resources["PillHoverBgBrush"];
        }
    }

    private void OnMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_accentColor is null)
        {
            PillBorder.Background = (System.Windows.Media.Brush)Resources["PillBaseBgBrush"];
        }
    }
}
