using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ShanlianVpn.Windows.Models;
using ShanlianVpn.Windows.Services;
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

        if (!SubscriptionGate.CanConnect(AppState.Subscription))
        {
            NodesStackPanel.Children.Add(new TextBlock
            {
                Text = SubscriptionGate.BlockerMessage(AppState.Subscription),
                Foreground = Brush(168, 179, 199),
                TextWrapping = TextWrapping.Wrap
            });
            render.Stop();
            SafeLogger.Performance("navigation_switch_ms", render.ElapsedMilliseconds);
            return;
        }

        if (AppState.Nodes.Count == 0)
        {
            NodesStackPanel.Children.Add(new TextBlock
            {
                Text = "暂无可用节点",
                Foreground = Brush(168, 179, 199)
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
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(54) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var badge = new Border
        {
            Width = 42,
            Height = 42,
            CornerRadius = new CornerRadius(8),
            Background = Brush(21, 36, 60),
            Child = new TextBlock
            {
                Text = CountryCode(node),
                Foreground = Brush(245, 248, 255),
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        grid.Children.Add(badge);

        var textStack = new StackPanel { Margin = new Thickness(0, 0, 18, 0) };
        Grid.SetColumn(textStack, 1);
        AppState.NodeLatencies.TryGetValue(node.Id, out var latency);
        textStack.Children.Add(new TextBlock
        {
            Text = node.DisplayCountry,
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush(245, 248, 255)
        });

        var latencyText = new TextBlock
        {
            Text = FormatLatency(node.Id, latency),
            Margin = new Thickness(0, 7, 0, 0),
            Foreground = Brush(168, 179, 199)
        };
        _latencyTextBlocks[node.Id] = latencyText;
        textStack.Children.Add(latencyText);
        grid.Children.Add(textStack);

        var actionText = new TextBlock
        {
            Text = isCurrent ? "已选择" : "选择",
            Foreground = isCurrent ? Brush(88, 166, 255) : Brush(168, 179, 199),
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(actionText, 2);
        grid.Children.Add(actionText);

        var button = new System.Windows.Controls.Button
        {
            Content = grid,
            Background = Brush(17, 31, 54),
            BorderBrush = isCurrent ? Brush(88, 166, 255) : Brush(36, 52, 79),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(18),
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
        RefreshButton.Content = "检查中...";

        try
        {
            await RefreshLatenciesAsync(forceNodesRefresh: true);
        }
        finally
        {
            RefreshButton.IsEnabled = true;
            RefreshButton.Content = "刷新";
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
            if (!SubscriptionGate.CanConnect(AppState.Subscription))
            {
                NodeService.ClearCache();
                AppState.Nodes = [];
                AppState.SelectedNode = null;
                AppState.NodeLatencies.Clear();
                await Dispatcher.InvokeAsync(RenderNodes);
                return;
            }

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
        if (!SubscriptionGate.CanConnect(AppState.Subscription))
        {
            return;
        }

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

    private static string CountryCode(VpnNode node)
    {
        if (!string.IsNullOrWhiteSpace(node.CountryCode))
        {
            return node.CountryCode.Length <= 3 ? node.CountryCode.ToUpperInvariant() : node.CountryCode[..3].ToUpperInvariant();
        }

        return string.IsNullOrWhiteSpace(node.DisplayCountry) ? "VPN" : node.DisplayCountry[..Math.Min(2, node.DisplayCountry.Length)].ToUpperInvariant();
    }

    private void MessageText(string message)
    {
        NodesStackPanel.Children.Insert(0, new TextBlock
        {
            Text = message,
            Foreground = Brush(239, 68, 68),
            Margin = new Thickness(0, 0, 0, 12)
        });
    }

    private static SolidColorBrush Brush(byte r, byte g, byte b) => new(WpfColor.FromRgb(r, g, b));
}
