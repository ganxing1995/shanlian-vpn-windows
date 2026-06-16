using System.Windows;
using System.Windows.Controls;
using ShanlianVpn.Windows.Services;

namespace ShanlianVpn.Windows.Views;

public partial class DiagnosticsPage : Page
{
    public DiagnosticsPage()
    {
        InitializeComponent();
        Render();
    }

    private void Render()
    {
        SummaryTextBox.Text = DiagnosticsService.BuildSafeDiagnostics();
        TunTextBlock.Text = AppState.ConnectionStatus == "已连接" ? "OK" : "Ready";
        RouteTextBlock.Text = AppState.ConnectionStatus == "已连接" ? "OK" : "Ready";
        DnsTextBlock.Text = AppState.ConnectionStatus == "已连接" ? "OK" : "Ready";
        HttpsTextBlock.Text = AppState.ConnectionStatus == "已连接" ? "OK" : "Ready";
    }

    private void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        System.Windows.Clipboard.SetText(DiagnosticsService.BuildSafeDiagnostics());
        System.Windows.MessageBox.Show("Safe diagnostics copied.", "Shanlian VPN", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
