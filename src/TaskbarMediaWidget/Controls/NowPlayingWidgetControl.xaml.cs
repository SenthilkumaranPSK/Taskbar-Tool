using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using TaskbarMediaWidget.Media;

namespace TaskbarMediaWidget.Controls;

public partial class NowPlayingWidgetControl : System.Windows.Controls.UserControl
{
    private const double ScrollPixelsPerSecond = 30;
    private const string PlayGlyph = "";
    private const string PauseGlyph = "";

    private string? _lastTitle;
    private string? _lastArtist;
    private System.Windows.Media.Color? _accentColor;
    private bool _isLightTheme;

    public event EventHandler? PreviousRequested;
    public event EventHandler? PlayPauseRequested;
    public event EventHandler? NextRequested;

    public NowPlayingWidgetControl()
    {
        InitializeComponent();
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
        if (info is null)
        {
            Visibility = Visibility.Collapsed;
            StopMarquee();
            ApplyAccent(null);
            _lastTitle = null;
            _lastArtist = null;
            return;
        }

        Visibility = Visibility.Visible;

        var title = string.IsNullOrWhiteSpace(info.Title) ? "Not playing" : info.Title;
        var isNewTrack = title != _lastTitle || info.Artist != _lastArtist;

        TitleText.Text = title;
        ArtistText.Text = info.Artist;
        CoverArt.Source = info.Thumbnail;

        PreviousButton.IsEnabled = info.IsPreviousEnabled;
        NextButton.IsEnabled = info.IsNextEnabled;
        PlayPauseButton.IsEnabled = info.IsPlayPauseEnabled;
        PlayPauseButton.Content = info.IsPlaying ? PauseGlyph : PlayGlyph;
        PlayPauseButton.SetValue(AutomationProperties.NameProperty, info.IsPlaying ? "Pause" : "Play");

        ApplyAccent(info.AccentColor);

        if (isNewTrack)
        {
            _lastTitle = title;
            _lastArtist = info.Artist;
            ContentVersion++;
        }

        // A storyboard with RepeatBehavior.Forever keeps WPF's composition thread busy at display
        // refresh rate for as long as it runs — pointless (and not free on battery) while paused
        // or hidden, so only keep it running while actually playing. Only reset scroll position
        // to 0 on an actual track change, otherwise a play/pause toggle (which also calls
        // SetNowPlaying) would yank mid-scroll text back to the start.
        if (!info.IsPlaying)
        {
            StopMarquee();
        }
        else if (isNewTrack)
        {
            RestartMarquee();
        }
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

        // The tint's alpha differs per theme, so the current accent has to be rebuilt, not kept.
        ApplyAccent(_accentColor, force: true);
    }

    /// <summary>
    /// Tints the pill from the album art's dominant color. Called on every SetNowPlaying (which
    /// Chromium-based browsers trigger many times per track), so it early-outs unless the color
    /// actually changed — otherwise every stray property-changed event would allocate and freeze
    /// a fresh brush pair for an identical result.
    /// </summary>
    private void ApplyAccent(System.Windows.Media.Color? accent, bool force = false)
    {
        if (!force && Nullable.Equals(_accentColor, accent))
        {
            return;
        }

        _accentColor = accent;

        if (accent is not { } color)
        {
            PillBorder.Background = System.Windows.Media.Brushes.Transparent;
            PillBorder.BorderBrush = System.Windows.Media.Brushes.Transparent;
            return;
        }

        // Kept deliberately faint: this sits on top of the real taskbar, and anything stronger
        // reads as a foreign panel bolted onto the shell rather than part of it. A light taskbar
        // needs slightly less alpha than a dark one for the same perceived weight.
        var (fillStart, fillEnd, edge) = _isLightTheme
            ? ((byte)0x3C, (byte)0x0E, (byte)0x59)
            : ((byte)0x4A, (byte)0x12, (byte)0x66);

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

    private void PreviousButton_Click(object sender, RoutedEventArgs e) => PreviousRequested?.Invoke(this, EventArgs.Empty);

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e) => PlayPauseRequested?.Invoke(this, EventArgs.Empty);

    private void NextButton_Click(object sender, RoutedEventArgs e) => NextRequested?.Invoke(this, EventArgs.Empty);
}
