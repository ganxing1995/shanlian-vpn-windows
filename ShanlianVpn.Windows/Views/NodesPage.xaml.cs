using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ShanlianVpn.Windows.Models;
using ShanlianVpn.Windows.Services;

namespace ShanlianVpn.Windows.Views;

public partial class NodesPage : Page
{
    private readonly Action _onSelected;
    private readonly NodeService _nodeService = new();
    private readonly LatencyService _latencyService = new();

    public NodesPage(Action onSelected)
    {
        _onSelected = onSelected;
        InitializeComponent();
        RenderNodes();
    }

    private void RenderNodes()
    {
        NodesStackPanel.Children.Clear();

        if (AppState.Nodes.Count == 0)
        {
            NodesStackPanel.Children.Add(new TextBlock
            {
                Text = "暂无可用线路",
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(102, 112, 133))
            });
            return;
        }

        foreach (var node in AppState.Nodes)
        {
            NodesStackPanel.Children.Add(CreateNodeButton(node));
        }
    }

    private System.Windows.Controls.Button CreateNodeButton(VpnNode node)
    {
        var isCurrent = AppState.SelectedNode?.Id == node.Id;
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var textStack = new StackPanel();
        AppState.NodeLatencies.TryGetValue(node.Id, out var latency);
        textStack.Children.Add(new TextBlock
        {
            Text = node.DisplayCountry,
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(16, 24, 40))
        });
        textStack.Children.Add(new TextBlock
        {
            Text = latency.HasValue ? $"延迟：{latency.Value} ms" : AppState.NodeLatencies.ContainsKey(node.Id) ? "延迟：检测失败" : "延迟：-- ms",
            Margin = new Thickness(0, 8, 0, 0),
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(102, 112, 133))
        });

        var actionText = new TextBlock
        {
            Text = isCurrent ? "当前线路" : "点击切换",
            Foreground = new SolidColorBrush(isCurrent ? System.Windows.Media.Color.FromRgb(27, 110, 243) : System.Windows.Media.Color.FromRgb(102, 112, 133)),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(actionText, 1);

        grid.Children.Add(textStack);
        grid.Children.Add(actionText);

        var button = new System.Windows.Controls.Button
        {
            Content = grid,
            Background = System.Windows.Media.Brushes.White,
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(234, 236, 240)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(16),
            Margin = new Thickness(0, 0, 0, 12),
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch
        };

        button.Click += async (_, _) =>
        {
            try
            {
                await _nodeService.GetNodeConfigAsync(node.Id);
                AppState.SelectedNode = node;
                SafeLogger.Info("node_selected");
                _onSelected();
                RenderNodes();
            }
            catch (ApiException ex)
            {
                System.Windows.MessageBox.Show(ex.Message, "闪连 VPN", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        };

        return button;
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshButton.IsEnabled = false;
        RefreshButton.Content = "检测中...";

        try
        {
            AppState.Nodes = await _nodeService.GetNodesAsync();
            foreach (var node in AppState.Nodes)
            {
                try
                {
                    var config = await _nodeService.GetNodeConfigAsync(node.Id);
                    AppState.NodeLatencies[node.Id] = await _latencyService.MeasureAsync(config);
                }
                catch
                {
                    AppState.NodeLatencies[node.Id] = null;
                }
            }

            RenderNodes();
        }
        catch (ApiException ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "闪连 VPN", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        finally
        {
            RefreshButton.IsEnabled = true;
            RefreshButton.Content = "刷新线路和延迟";
        }
    }
}


