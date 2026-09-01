using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using TaskbarMediaWidget.Media;

namespace TaskbarMediaWidget.Controls;

public partial class NowPlayingWidgetControl : System.Windows.Controls.UserControl
{
    private const double ScrollPixelsPerSecond = 30;

    public event EventHandler? PreviousRequested;
    public event EventHandler? PlayPauseRequested;
    public event EventHandler? NextRequested;

    public NowPlayingWidgetControl()
    {
        InitializeComponent();
    }

    /// <summary>Updates the widget's content. Pass null to represent "nothing playing."</summary>
    public void SetNowPlaying(NowPlayingInfo? info)
    {
        if (info is null)
        {
            Visibility = Visibility.Collapsed;
            return;
        }

        Visibility = Visibility.Visible;

        TitleText.Text = string.IsNullOrWhiteSpace(info.Title) ? "Not playing" : info.Title;
        ArtistText.Text = info.Artist;
        CoverArt.Source = info.Thumbnail;

        PreviousButton.IsEnabled = info.IsPreviousEnabled;
        NextButton.IsEnabled = info.IsNextEnabled;
        PlayPauseButton.IsEnabled = info.IsPlayPauseEnabled;
        PlayPauseButton.Content = info.IsPlaying ? "⏸" : "▶";

        RestartMarqueeIfNeeded();
    }

    /// <summary>Natural width of the widget's content, for the host window to size itself to.</summary>
    public System.Windows.Size MeasureNaturalSize()
    {
        Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        return DesiredSize;
    }

    private void RestartMarqueeIfNeeded()
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
