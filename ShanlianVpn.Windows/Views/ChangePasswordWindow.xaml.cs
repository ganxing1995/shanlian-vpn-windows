using System.Windows;
using ShanlianVpn.Windows.Models;
using ShanlianVpn.Windows.Services;

namespace ShanlianVpn.Windows.Views;

public partial class ChangePasswordWindow : Window
{
    private readonly AuthService _authService = new();
    private readonly Action _onChanged;

    public ChangePasswordWindow(Action onChanged)
    {
        _onChanged = onChanged;
        InitializeComponent();
    }

    private async void SubmitButton_Click(object sender, RoutedEventArgs e)
    {
        SubmitButton.IsEnabled = false;
        MessageTextBlock.Text = "";

        try
        {
            await _authService.ChangePasswordAsync(CurrentPasswordBox.Password, NewPasswordBox.Password, ConfirmPasswordBox.Password);
            _onChanged();
            System.Windows.MessageBox.Show("密码修改成功，请重新登录", "闪连 VPN", MessageBoxButton.OK, MessageBoxImage.Information);
            Close();
        }
        catch (ApiException ex)
        {
            MessageTextBlock.Text = ex.Message;
        }
        finally
        {
            SubmitButton.IsEnabled = true;
        }
    }
}



