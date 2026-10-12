using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TaskbarMediaWidget.Core;
using TaskbarMediaWidget.Interop;

namespace TaskbarMediaWidget.Settings;

public partial class SettingsWindow : Window
{
    private readonly CoreAudioVolumeService _volumeService = new();
    private bool _isInitializing = true;

    public SettingsWindow()
    {
        InitializeComponent();
        ApplyTheme(ThemeDetector.IsSystemLightTheme());
        LoadSettings();
    }

    private void ApplyTheme(bool isLightTheme)
    {
        Resources["SettingsBgBrush"] = new System.Windows.Media.SolidColorBrush(isLightTheme
            ? System.Windows.Media.Color.FromArgb(0xF4, 0xF5, 0xF5, 0xF7)
            : System.Windows.Media.Color.FromArgb(0xF4, 0x1E, 0x1E, 0x20));

        Resources["SettingsCardBrush"] = new System.Windows.Media.SolidColorBrush(isLightTheme
            ? System.Windows.Media.Color.FromArgb(0x12, 0x00, 0x00, 0x00)
            : System.Windows.Media.Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF));

        Resources["SettingsBorderBrush"] = new System.Windows.Media.SolidColorBrush(isLightTheme
            ? System.Windows.Media.Color.FromArgb(0x1F, 0x00, 0x00, 0x00)
            : System.Windows.Media.Color.FromArgb(0x2B, 0xFF, 0xFF, 0xFF));

        Resources["PrimaryTextBrush"] = new System.Windows.Media.SolidColorBrush(isLightTheme
            ? System.Windows.Media.Color.FromRgb(0x1A, 0x1A, 0x1A)
            : System.Windows.Media.Colors.White);

        Resources["SecondaryTextBrush"] = new System.Windows.Media.SolidColorBrush(isLightTheme
            ? System.Windows.Media.Color.FromRgb(0x5A, 0x5A, 0x5A)
            : System.Windows.Media.Color.FromArgb(0xB8, 0xFF, 0xFF, 0xFF));
    }

    private void LoadSettings()
    {
        _isInitializing = true;
        try
        {
            ChkStartup.IsChecked = StartupRegistration.IsEnabled();
            ChkGlobalHotkey.IsChecked = AppSettings.GlobalHotkeyEnabled;
            ChkHideWhenIdle.IsChecked = AppSettings.HideWhenIdle;
            ChkDiscordRpc.IsChecked = AppSettings.DiscordRpcEnabled;

            ChkTransparency.IsChecked = AppSettings.TransparencyMode;
            ChkProgressBar.IsChecked = AppSettings.ShowProgressBar;
            ChkEqualizer.IsChecked = AppSettings.ShowEqualizer;
            ChkRealSpectrum.IsChecked = AppSettings.RealSpectrumEnabled;

            CmbLayout.SelectedIndex = (int)AppSettings.WidgetLayout;

            SldOffset.Value = AppSettings.TaskbarOffsetPx;
            TxtOffsetVal.Text = $"{AppSettings.TaskbarOffsetPx} px";

            TxtIgnoredApps.Text = AppSettings.IgnoredApps;

            LoadAudioDevices();
        }
        finally
        {
            _isInitializing = false;
        }
    }

    private void LoadAudioDevices()
    {
        CmbAudioDevices.Items.Clear();
        var devices = _volumeService.GetAudioPlaybackDevices();
        int selectedIndex = 0;

        for (var i = 0; i < devices.Count; i++)
        {
            var d = devices[i];
            var item = new ComboBoxItem { Content = d.Name, Tag = d.Id };
            CmbAudioDevices.Items.Add(item);
            if (d.IsDefault)
            {
                selectedIndex = i;
            }
        }

        if (CmbAudioDevices.Items.Count > 0)
        {
            CmbAudioDevices.SelectedIndex = selectedIndex;
        }
    }

    private void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            DragMove();
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ChkStartup_Click(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        var enable = ChkStartup.IsChecked == true;
        StartupRegistration.TrySetEnabled(enable);
    }

    private void ChkGlobalHotkey_Click(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        AppSettings.GlobalHotkeyEnabled = ChkGlobalHotkey.IsChecked == true;
    }

    private void ChkHideWhenIdle_Click(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        AppSettings.HideWhenIdle = ChkHideWhenIdle.IsChecked == true;
    }

    private void ChkDiscordRpc_Click(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        AppSettings.DiscordRpcEnabled = ChkDiscordRpc.IsChecked == true;
    }

    private void ChkTransparency_Click(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        AppSettings.TransparencyMode = ChkTransparency.IsChecked == true;
    }

    private void ChkProgressBar_Click(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        AppSettings.ShowProgressBar = ChkProgressBar.IsChecked == true;
    }

    private void ChkEqualizer_Click(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        AppSettings.ShowEqualizer = ChkEqualizer.IsChecked == true;
    }

    private void ChkRealSpectrum_Click(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        AppSettings.RealSpectrumEnabled = ChkRealSpectrum.IsChecked == true;
    }

    private void CmbLayout_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;
        if (CmbLayout.SelectedItem is ComboBoxItem item && int.TryParse(item.Tag?.ToString(), out var layout))
        {
            AppSettings.WidgetLayout = (WidgetLayoutStyle)layout;
        }
    }

    private void SldOffset_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtOffsetVal is not null)
        {
            var val = (int)Math.Round(e.NewValue);
            TxtOffsetVal.Text = $"{val} px";
            if (!_isInitializing)
            {
                AppSettings.TaskbarOffsetPx = val;
            }
        }
    }

    private void TxtIgnoredApps_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isInitializing) return;
        AppSettings.IgnoredApps = TxtIgnoredApps.Text.Trim();
    }

    private void CmbAudioDevices_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;
        if (CmbAudioDevices.SelectedItem is ComboBoxItem item && item.Tag is string deviceId)
        {
            _volumeService.SwitchDefaultAudioDevice(deviceId);
        }
    }
}
