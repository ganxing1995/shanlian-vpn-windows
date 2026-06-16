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
                Text = "开通订阅后可查看地区和线路延迟。",
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
                Text = "暂无可用地区",
                Foreground = Brush(168, 179, 199)
            });
            render.Stop();
            SafeLogger.Performance("navigation_switch_ms", render.ElapsedMilliseconds);
            return;
        }

        foreach (var group in AppState.Nodes.GroupBy(node => ToRegionName(node.DisplayCountry)))
        {
            NodesStackPanel.Children.Add(CreateGroupHeader(group.Key, group.First().CountryCode));
            foreach (var node in group)
            {
                NodesStackPanel.Children.Add(CreateNodeButton(node));
            }
        }

        render.Stop();
        SafeLogger.Performance("navigation_switch_ms", render.ElapsedMilliseconds);
    }

    private System.Windows.Controls.Button CreateNodeButton(VpnNode node)
    {
        var isCurrent = AppState.SelectedNode?.Id == node.Id;
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var badge = new Border
        {
            Width = 52,
            Height = 52,
            CornerRadius = new CornerRadius(12),
            Background = Brush(21, 36, 60),
            Child = new TextBlock
            {
                Text = GetFlagEmoji(node.CountryCode),
                Foreground = Brush(245, 248, 255),
                FontSize = 24,
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
            Text = ToRegionName(node.DisplayCountry),
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush(245, 248, 255)
        });
        textStack.Children.Add(new TextBlock
        {
            Text = GetLocationLabel(node),
            Margin = new Thickness(0, 5, 0, 0),
            Foreground = Brush(168, 179, 199)
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

        var rightStack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center
        };
        rightStack.Children.Add(new TextBlock
        {
            Text = GetNodeStatus(node.Id, latency),
            Foreground = isCurrent ? Brush(88, 166, 255) : Brush(168, 179, 199),
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right
        });
        rightStack.Children.Add(new TextBlock
        {
            Text = isCurrent ? "当前使用" : "点击切换",
            Foreground = Brush(168, 179, 199),
            Margin = new Thickness(0, 6, 0, 0),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right
        });
        Grid.SetColumn(rightStack, 2);
        grid.Children.Add(rightStack);

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
        RefreshButton.Content = "刷新中...";

        try
        {
            await RefreshLatenciesAsync(forceNodesRefresh: true);
        }
        finally
        {
            RefreshButton.IsEnabled = true;
            RefreshButton.Content = "刷新地区";
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
        latency.HasValue ? $"延迟：{latency.Value} ms" : AppState.NodeLatencies.ContainsKey(nodeId) ? "延迟：检测中" : "延迟：检测中";

    private static string ToRegionName(string? region)
    {
        if (string.IsNullOrWhiteSpace(region))
        {
            return "自动优选";
        }

        if (region.Contains("United States", StringComparison.OrdinalIgnoreCase) || region.Contains("USA", StringComparison.OrdinalIgnoreCase))
        {
            return "美国";
        }

        if (region.Contains("Japan", StringComparison.OrdinalIgnoreCase))
        {
            return "日本";
        }

        if (region.Contains("Singapore", StringComparison.OrdinalIgnoreCase))
        {
            return "新加坡";
        }

        if (region.Contains("Hong Kong", StringComparison.OrdinalIgnoreCase))
        {
            return "香港";
        }

        if (region.Contains("Korea", StringComparison.OrdinalIgnoreCase))
        {
            return "韩国";
        }

        if (region.Contains("Taiwan", StringComparison.OrdinalIgnoreCase))
        {
            return "台湾";
        }

        return region;
    }

    private static string GetFlagEmoji(string countryCode) => countryCode.ToUpperInvariant() switch
    {
        "US" or "USA" => "🇺🇸",
        "JP" or "JPN" => "🇯🇵",
        "SG" or "SGP" => "🇸🇬",
        "HK" or "HKG" => "🇭🇰",
        "KR" or "KOR" => "🇰🇷",
        "TW" or "TWN" => "🇹🇼",
        _ => "🌐"
    };

    private static string GetLocationLabel(VpnNode node)
    {
        if (string.IsNullOrWhiteSpace(node.Name))
        {
            return "自动选择当前可用线路";
        }

        var region = ToRegionName(node.DisplayCountry);
        return node.Name.Contains(region, StringComparison.OrdinalIgnoreCase)
            ? "自动选择当前可用线路"
            : node.Name;
    }

    private static string GetNodeStatus(string nodeId, int? latency)
    {
        if (!AppState.NodeLatencies.ContainsKey(nodeId))
        {
            return "检测中";
        }

        if (!latency.HasValue)
        {
            return "不可用";
        }

        return latency.Value switch
        {
            <= 160 => "可用",
            <= 300 => "较慢",
            _ => "维护中"
        };
    }

    private static UIElement CreateGroupHeader(string regionName, string countryCode)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        grid.Children.Add(new TextBlock
        {
            Text = GetFlagEmoji(countryCode),
            FontSize = 18,
            VerticalAlignment = VerticalAlignment.Center
        });

        var title = new TextBlock
        {
            Text = regionName,
            Foreground = Brush(245, 248, 255),
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(title, 1);
        grid.Children.Add(title);
        return grid;
    }

    private static string CountryCode(VpnNode node)
    {
        if (!string.IsNullOrWhiteSpace(node.CountryCode))
        {
            return node.CountryCode.Length <= 3 ? node.CountryCode.ToUpperInvariant() : node.CountryCode[..3].ToUpperInvariant();
        }

        var region = ToRegionName(node.DisplayCountry);
        return string.IsNullOrWhiteSpace(region) ? "地区" : region[..Math.Min(2, region.Length)].ToUpperInvariant();
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
