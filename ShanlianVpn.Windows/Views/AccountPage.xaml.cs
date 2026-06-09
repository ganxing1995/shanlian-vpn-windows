using System.Windows;
using System.Windows.Controls;
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
        DeviceCodeTextBlock.Text = AppState.DeviceShortCode;
    }

    private void LogoutButton_Click(object sender, RoutedEventArgs e) => _logout();
}

