using System.Windows;
using ShanlianVpn.Windows.Models;
using ShanlianVpn.Windows.Services;

namespace ShanlianVpn.Windows.Views;

public partial class LoginWindow : Window
{
    private readonly AuthService _authService = new();

    public LoginWindow()
    {
        InitializeComponent();
    }

    private async void LoginButton_Click(object sender, RoutedEventArgs e)
    {
        ErrorTextBlock.Visibility = Visibility.Collapsed;
        LoginButton.IsEnabled = false;
        LoginButton.Content = "登录中...";

        try
        {
            AppState.CurrentUser = await _authService.LoginAsync(EmailTextBox.Text.Trim(), PasswordBox.Password);
            var mainWindow = new MainWindow();
            System.Windows.Application.Current.MainWindow = mainWindow;
            mainWindow.Show();
            Close();
        }
        catch (ApiException ex)
        {
            ErrorTextBlock.Text = ex.Message;
            ErrorTextBlock.Visibility = Visibility.Visible;
        }
        catch
        {
            ErrorTextBlock.Text = "服务器错误，请稍后重试";
            ErrorTextBlock.Visibility = Visibility.Visible;
        }
        finally
        {
            LoginButton.IsEnabled = true;
            LoginButton.Content = "登录";
        }
    }

    private void RegisterButton_Click(object sender, RoutedEventArgs e)
    {
        new RegisterWindow { Owner = this }.ShowDialog();
    }

    private void ChangePasswordButton_Click(object sender, RoutedEventArgs e)
    {
        new ChangePasswordWindow(() =>
        {
            TokenStore.Clear();
        })
        { Owner = this }.ShowDialog();
    }
}


