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
        TunTextBlock.Text = AppState.ConnectionStatus == "已连接" ? "正常" : "已准备";
        RouteTextBlock.Text = AppState.ConnectionStatus == "已连接" ? "正常" : "已准备";
        DnsTextBlock.Text = AppState.ConnectionStatus == "已连接" ? "正常" : "已准备";
        HttpsTextBlock.Text = AppState.ConnectionStatus == "已连接" ? "正常" : "已准备";
    }

    private void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        System.Windows.Clipboard.SetText(DiagnosticsService.BuildSafeDiagnostics());
        System.Windows.MessageBox.Show("高级诊断信息已复制。", "闪连 VPN", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
