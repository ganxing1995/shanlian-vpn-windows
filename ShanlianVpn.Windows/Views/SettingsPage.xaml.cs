using System.Windows;
using System.Windows.Controls;
using ShanlianVpn.Windows.Models;
using ShanlianVpn.Windows.Services;

namespace ShanlianVpn.Windows.Views;

public partial class SettingsPage : Page
{
    private readonly Action? _onSettingsChanged;
    private bool _isInitializing = true;

    public SettingsPage(Action? onSettingsChanged = null)
    {
        _onSettingsChanged = onSettingsChanged;
        InitializeComponent();
        LoadSettings();
        _isInitializing = false;
    }

    private void LoadSettings()
    {
        var settings = UserSettingsService.Current;
        settings.LaunchOnStartup = StartupRegistrationService.IsEnabled();
        UserSettingsService.Save(settings);

        SpeedModeRadioButton.IsChecked = settings.ConnectionMode == ConnectionMode.Speed;
        GlobalModeRadioButton.IsChecked = settings.ConnectionMode == ConnectionMode.Global;
        DarkThemeRadioButton.IsChecked = settings.ThemeMode == ThemeMode.Dark;
        LightThemeRadioButton.IsChecked = settings.ThemeMode == ThemeMode.Light;
        SystemThemeRadioButton.IsChecked = settings.ThemeMode == ThemeMode.System;
        LaunchOnStartupCheckBox.IsChecked = settings.LaunchOnStartup;
        AutoConnectCheckBox.IsChecked = settings.AutoConnect;
    }

    private void SpeedModeRadioButton_Checked(object sender, RoutedEventArgs e) =>
        UpdateConnectionMode(ConnectionMode.Speed);

    private void GlobalModeRadioButton_Checked(object sender, RoutedEventArgs e) =>
        UpdateConnectionMode(ConnectionMode.Global);

    private void DarkThemeRadioButton_Checked(object sender, RoutedEventArgs e) =>
        UpdateTheme(ThemeMode.Dark);

    private void LightThemeRadioButton_Checked(object sender, RoutedEventArgs e) =>
        UpdateTheme(ThemeMode.Light);

    private void SystemThemeRadioButton_Checked(object sender, RoutedEventArgs e) =>
        UpdateTheme(ThemeMode.System);

    private void LaunchOnStartupCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        try
        {
            var enabled = LaunchOnStartupCheckBox.IsChecked == true;
            StartupRegistrationService.SetEnabled(enabled);
            UserSettingsService.Current.LaunchOnStartup = enabled;
            UserSettingsService.Save();
        }
        catch
        {
            System.Windows.MessageBox.Show("开机启动设置失败，请稍后重试。", "闪连 VPN", MessageBoxButton.OK, MessageBoxImage.Information);
            LoadSettings();
        }
    }

    private void AutoConnectCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        UserSettingsService.Current.AutoConnect = AutoConnectCheckBox.IsChecked == true;
        UserSettingsService.Save();
    }

    private void UpdateConnectionMode(ConnectionMode mode)
    {
        if (_isInitializing)
        {
            return;
        }

        UserSettingsService.Current.ConnectionMode = mode;
        UserSettingsService.Save();
        _onSettingsChanged?.Invoke();
    }

    private void UpdateTheme(ThemeMode themeMode)
    {
        if (_isInitializing)
        {
            return;
        }

        UserSettingsService.Current.ThemeMode = themeMode;
        UserSettingsService.Save();
        ThemeService.Apply(themeMode);
        _onSettingsChanged?.Invoke();
    }
}
