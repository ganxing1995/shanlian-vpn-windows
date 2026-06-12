using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ShanlianVpn.Windows.Models;
using ShanlianVpn.Windows.Services;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;

namespace ShanlianVpn.Windows.Views;

public partial class NodesPage : Page
{
    private readonly Action _onSelected;
    private readonly NodeService _nodeService = new();
    private readonly LatencyService _latencyService = new();
    private readonly Dictionary<string, TextBlock> _latencyTextBlocks = new();
    private CancellationTokenSource? _latencyRefreshCts;

    public NodesPage(Action onSelected)
    {
        _onSelected = onSelected;
        InitializeComponent();
        RenderNodes();
        _ = RefreshLatenciesAsync(forceNodesRefresh: false);
    }

    private void RenderNodes()
    {
        var render = Stopwatch.StartNew();
        _latencyTextBlocks.Clear();
        NodesStackPanel.Children.Clear();

        if (AppState.Nodes.Count == 0)
        {
            NodesStackPanel.Children.Add(new TextBlock
            {
                Text = "暂无可用线路",
                Foreground = new SolidColorBrush(WpfColor.FromRgb(102, 112, 133))
            });
            render.Stop();
            SafeLogger.Performance("navigation_switch_ms", render.ElapsedMilliseconds);
            return;
        }

        foreach (var node in AppState.Nodes)
        {
            NodesStackPanel.Children.Add(CreateNodeButton(node));
        }

        render.Stop();
        SafeLogger.Performance("navigation_switch_ms", render.ElapsedMilliseconds);
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
            Foreground = new SolidColorBrush(WpfColor.FromRgb(16, 24, 40))
        });

        var latencyText = new TextBlock
        {
            Text = FormatLatency(node.Id, latency),
            Margin = new Thickness(0, 8, 0, 0),
            Foreground = new SolidColorBrush(WpfColor.FromRgb(102, 112, 133))
        };
        _latencyTextBlocks[node.Id] = latencyText;
        textStack.Children.Add(latencyText);

        var actionText = new TextBlock
        {
            Text = isCurrent ? "当前线路" : "点击切换",
            Foreground = new SolidColorBrush(isCurrent ? WpfColor.FromRgb(27, 110, 243) : WpfColor.FromRgb(102, 112, 133)),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(actionText, 1);

        grid.Children.Add(textStack);
        grid.Children.Add(actionText);

        var button = new System.Windows.Controls.Button
        {
            Content = grid,
            Background = WpfBrushes.White,
            BorderBrush = new SolidColorBrush(WpfColor.FromRgb(234, 236, 240)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(16),
            Margin = new Thickness(0, 0, 0, 12),
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch
        };

        button.Click += (_, _) =>
        {
            AppState.SelectedNode = node;
            SafeLogger.Info("node_selected");
            _onSelected();
            RenderNodes();
        };

        return button;
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshButton.IsEnabled = false;
        RefreshButton.Content = "检测中...";

        try
        {
            await RefreshLatenciesAsync(forceNodesRefresh: true);
        }
        finally
        {
            RefreshButton.IsEnabled = true;
            RefreshButton.Content = "刷新线路和延迟";
        }
    }

    private async Task RefreshLatenciesAsync(bool forceNodesRefresh)
    {
        var refresh = Stopwatch.StartNew();
        _latencyRefreshCts?.Cancel();
        _latencyRefreshCts = new CancellationTokenSource();
        var cancellationToken = _latencyRefreshCts.Token;

        try
        {
            if (forceNodesRefresh || AppState.Nodes.Count == 0)
            {
                AppState.Nodes = await _nodeService.GetNodesAsync(forceNodesRefresh);
                AppState.SelectedNode ??= AppState.Nodes.FirstOrDefault();
                await Dispatcher.InvokeAsync(RenderNodes);
            }

            using var throttler = new SemaphoreSlim(2);
            var tasks = AppState.Nodes.Select(node => RefreshNodeLatencyAsync(node, throttler, cancellationToken)).ToArray();
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
            // A newer refresh superseded this one.
        }
        catch (ApiException ex)
        {
            MessageText(ex.Message);
        }
        finally
        {
            refresh.Stop();
            SafeLogger.Performance("node_latency_refresh_ms", refresh.ElapsedMilliseconds);
        }
    }

    private async Task RefreshNodeLatencyAsync(VpnNode node, SemaphoreSlim throttler, CancellationToken cancellationToken)
    {
        await throttler.WaitAsync(cancellationToken);
        try
        {
            int? latency = null;
            try
            {
                var config = await _nodeService.GetNodeConfigAsync(node.Id);
                latency = await _latencyService.MeasureAsync(config, cancellationToken);
            }
            catch
            {
                latency = null;
            }

            AppState.NodeLatencies[node.Id] = latency;
            await Dispatcher.InvokeAsync(() =>
            {
                if (_latencyTextBlocks.TryGetValue(node.Id, out var textBlock))
                {
                    textBlock.Text = FormatLatency(node.Id, latency);
                }
            });
        }
        finally
        {
            throttler.Release();
        }
    }

    private static string FormatLatency(string nodeId, int? latency) =>
        latency.HasValue ? $"延迟：{latency.Value} ms" : AppState.NodeLatencies.ContainsKey(nodeId) ? "延迟：-- ms" : "延迟：-- ms";

    private void MessageText(string message)
    {
        NodesStackPanel.Children.Insert(0, new TextBlock
        {
            Text = message,
            Foreground = new SolidColorBrush(WpfColor.FromRgb(180, 35, 24)),
            Margin = new Thickness(0, 0, 0, 12)
        });
    }
}
