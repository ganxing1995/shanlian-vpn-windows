using System.Windows;
using ShanlianVpn.Windows.Models;
using ShanlianVpn.Windows.Services;

namespace ShanlianVpn.Windows.Views;

public partial class RegisterWindow : Window
{
    private readonly AuthService _authService = new();

    public RegisterWindow()
    {
        InitializeComponent();
    }

    private async void RegisterButton_Click(object sender, RoutedEventArgs e)
    {
        RegisterButton.IsEnabled = false;
        MessageTextBlock.Text = "";

        try
        {
            await _authService.RegisterAsync(EmailTextBox.Text.Trim(), PasswordBox.Password);
            System.Windows.MessageBox.Show("注册成功，请登录", "闪连 VPN", MessageBoxButton.OK, MessageBoxImage.Information);
            Close();
        }
        catch (ApiException ex)
        {
            MessageTextBlock.Text = ex.Message;
        }
        finally
        {
            RegisterButton.IsEnabled = true;
        }
    }
}



