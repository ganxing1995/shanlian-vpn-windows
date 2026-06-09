using System.Windows;
using System.Windows.Controls;
using System.Reflection;
using ShanlianVpn.Windows.Services;

namespace ShanlianVpn.Windows.Views;

public partial class AccountPage : Page
{
    private readonly Action _logout;

    public AccountPage(Action logout)
    {
        _logout = logout;
        InitializeComponent();
        EmailTextBlock.Text = AppState.CurrentUser?.Email ?? "--";
        DeviceTextBlock.Text = $"Windows PC（{AppState.DeviceShortCode}）";
        VersionTextBlock.Text = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
    }

    private void LogoutButton_Click(object sender, RoutedEventArgs e) => _logout();
}
