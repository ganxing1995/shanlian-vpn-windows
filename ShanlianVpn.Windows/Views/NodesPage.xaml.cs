using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using ShanlianVpn.Windows.Models;
using ShanlianVpn.Windows.Services;
using Ellipse = System.Windows.Shapes.Ellipse;
using Polygon = System.Windows.Shapes.Polygon;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfButton = System.Windows.Controls.Button;
using WpfColor = System.Windows.Media.Color;

namespace ShanlianVpn.Windows.Views;

public partial class NodesPage : Page
{
    private const string ConnectedText = "\u5df2\u8fde\u63a5";
    private readonly Action _onSelected;
    private readonly NodeService _nodeService = new();
    private readonly LatencyService _latencyService = new();
    private readonly Dictionary<string, NodeVisualRefs> _nodeVisuals = new();
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
        _nodeVisuals.Clear();
        NodesStackPanel.Children.Clear();

        if (!SubscriptionGate.CanConnect(AppState.Subscription))
        {
            NodesStackPanel.Children.Add(CreateEmptyState("\u5f00\u901a\u8ba2\u9605\u540e\u5373\u53ef\u67e5\u770b\u8282\u70b9\u5217\u8868\u548c\u5ef6\u8fdf\u6570\u636e\u3002"));
            render.Stop();
            SafeLogger.Performance("navigation_switch_ms", render.ElapsedMilliseconds);
            return;
        }

        if (AppState.Nodes.Count == 0)
        {
            NodesStackPanel.Children.Add(CreateEmptyState("\u6682\u65e0\u53ef\u7528\u8282\u70b9"));
            render.Stop();
            SafeLogger.Performance("navigation_switch_ms", render.ElapsedMilliseconds);
            return;
        }

        var groupedNodes = AppState.Nodes
            .GroupBy(GetContinentName)
            .OrderBy(group => GetContinentOrder(group.Key))
            .ThenBy(group => group.Key, StringComparer.Ordinal);

        foreach (var group in groupedNodes)
        {
            var orderedNodes = group
                .OrderBy(GetCountryDisplayName)
                .ThenBy(node => node.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            NodesStackPanel.Children.Add(CreateGroupSection(group.Key, orderedNodes));
        }

        render.Stop();
        SafeLogger.Performance("navigation_switch_ms", render.ElapsedMilliseconds);
    }

    private UIElement CreateEmptyState(string message)
    {
        return new Border
        {
            Background = ResourceBrush("CardBrush", 17, 31, 54),
            BorderBrush = ResourceBrush("CardBorderBrush", 36, 52, 79),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(18),
            Child = new TextBlock
            {
                Text = message,
                Foreground = ResourceBrush("SoftTextBrush", 168, 179, 199),
                TextWrapping = TextWrapping.Wrap
            }
        };
    }

    private UIElement CreateGroupSection(string groupName, IReadOnlyList<VpnNode> nodes)
    {
        var section = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };

        var header = new Grid { Margin = new Thickness(2, 0, 2, 6) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        header.Children.Add(new TextBlock
        {
            Text = groupName,
            Foreground = ResourceBrush("BrightTextBrush", 245, 248, 255),
            FontSize = 18,
            FontWeight = FontWeights.SemiBold
        });

        var countText = new TextBlock
        {
            Text = $"{nodes.Count} \u4e2a\u8282\u70b9",
            Foreground = ResourceBrush("SoftTextBrush", 168, 179, 199),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12
        };
        Grid.SetColumn(countText, 1);
        header.Children.Add(countText);

        var listStack = new UniformGrid
        {
            Columns = 3,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch
        };

        for (var index = 0; index < nodes.Count; index++)
        {
            listStack.Children.Add(CreateNodeRow(nodes[index], index == nodes.Count - 1));
        }

        section.Children.Add(header);
        section.Children.Add(listStack);
        return section;
    }

    private UIElement CreateNodeRow(VpnNode node, bool isLast)
    {
        var isCurrent = AppState.SelectedNode?.Id == node.Id;
        AppState.NodeLatencies.TryGetValue(node.Id, out var latency);

        var normalBackground = WpfBrushes.Transparent;
        var hoverBackground = ResourceBrush("NavHoverBrush", 24, 40, 66);
        var selectedBackground = ResourceBrush("ModeBadgeBrush", 23, 43, 72);

        var rootGrid = new Grid();
        rootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
        rootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        rootGrid.Children.Add(new Border
        {
            Background = isCurrent ? ResourceBrush("AccentBrush", 27, 110, 243) : WpfBrushes.Transparent,
            CornerRadius = new CornerRadius(6, 0, 0, 6)
        });

        var rowBorder = new Border
        {
            Background = isCurrent ? selectedBackground : normalBackground,
            BorderBrush = WpfBrushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0, 10, 10, 0),
            Padding = new Thickness(10, 8, 10, 8)
        };
        Grid.SetColumn(rowBorder, 1);

        var rowGrid = new Grid();
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var flagBadge = new Border
        {
            Width = 40,
            Height = 26,
            CornerRadius = new CornerRadius(7),
            Background = Brush(255, 255, 255),
            BorderBrush = ResourceBrush("CardBorderBrush", 36, 52, 79),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = CreateFlagVisual(node)
        };
        rowGrid.Children.Add(flagBadge);

        var textStack = new StackPanel
        {
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(textStack, 1);
        textStack.Children.Add(new TextBlock
        {
            Text = GetCountryDisplayName(node),
            Foreground = ResourceBrush("BrightTextBrush", 245, 248, 255),
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        var latencyText = new TextBlock
        {
            Text = GetLatencyLine(node.Id, latency, isCurrent),
            Foreground = GetLatencyForeground(node.Id, latency, isCurrent),
            Margin = new Thickness(0, 2, 0, 0),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold
        };
        textStack.Children.Add(latencyText);
        rowGrid.Children.Add(textStack);

        var rightStack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right
        };

        ApplyNodeState(node, latency, latencyText);

        rightStack.Children.Add(new TextBlock
        {
            Text = isCurrent ? "\u5f53\u524d\u4f7f\u7528" : "\u70b9\u51fb\u5207\u6362",
            Foreground = ResourceBrush("SoftTextBrush", 168, 179, 199),
            Margin = new Thickness(0, 0, 0, 0),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            FontSize = 12
        });
        Grid.SetColumn(rightStack, 2);
        rowGrid.Children.Add(rightStack);

        rowBorder.Child = rowGrid;
        rootGrid.Children.Add(rowBorder);

        var button = new WpfButton
        {
            Content = rootGrid,
            Background = WpfBrushes.Transparent,
            BorderBrush = WpfBrushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            FocusVisualStyle = null,
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            MinWidth = 220,
            Margin = new Thickness(0, 0, 10, 10)
        };

        if (!isCurrent)
        {
            button.MouseEnter += (_, _) => rowBorder.Background = hoverBackground;
            button.MouseLeave += (_, _) => rowBorder.Background = normalBackground;
        }

        button.Click += (_, _) =>
        {
            AppState.SelectedNode = node;
            SafeLogger.Info("node_selected");
            _onSelected();
            RenderNodes();
        };

        _nodeVisuals[node.Id] = new NodeVisualRefs(latencyText);
        return button;
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshButton.IsEnabled = false;
        RefreshButton.Content = "\u5237\u65b0\u4e2d...";

        try
        {
            await RefreshLatenciesAsync(forceNodesRefresh: true);
        }
        finally
        {
            RefreshButton.IsEnabled = true;
            RefreshButton.Content = "\u5237\u65b0\u8282\u70b9";
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

            using var throttler = new SemaphoreSlim(1);
            var tasks = AppState.Nodes.Select(node => RefreshNodeLatencyAsync(node, throttler, cancellationToken)).ToArray();
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
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
                if (_nodeVisuals.TryGetValue(node.Id, out var refs))
                {
                    ApplyNodeState(node, latency, refs.LatencyText);
                }
            });
        }
        finally
        {
            throttler.Release();
        }
    }

    private void ApplyNodeState(VpnNode node, int? latency, TextBlock latencyText)
    {
        var isCurrent = AppState.SelectedNode?.Id == node.Id;
        latencyText.Text = GetLatencyLine(node.Id, latency, isCurrent);
        latencyText.Foreground = GetLatencyForeground(node.Id, latency, isCurrent);
    }

    private static string GetLatencyLine(string nodeId, int? latency, bool isCurrent)
    {
        if (!AppState.NodeLatencies.ContainsKey(nodeId))
        {
            return "\u5ef6\u8fdf\uff1a\u68c0\u6d4b\u4e2d";
        }

        if (latency.HasValue)
        {
            return $"{latency.Value} ms";
        }

        return isCurrent && AppState.ConnectionStatus == ConnectedText
            ? "\u5df2\u8fde\u63a5\uff0c\u7a0d\u540e\u91cd\u6d4b"
            : "\u5f85\u91cd\u8bd5";
    }

    private System.Windows.Media.Brush GetLatencyForeground(string nodeId, int? latency, bool isCurrent)
    {
        if (isCurrent && AppState.ConnectionStatus == ConnectedText)
        {
            return ResourceBrush("ConnectedBrush", 34, 197, 94);
        }

        if (!AppState.NodeLatencies.ContainsKey(nodeId))
        {
            return ResourceBrush("SoftTextBrush", 168, 179, 199);
        }

        if (!latency.HasValue)
        {
            return ResourceBrush("WarningBrush", 245, 158, 11);
        }

        return latency.Value switch
        {
            <= 299 => ResourceBrush("ConnectedBrush", 34, 197, 94),
            <= 599 => ResourceBrush("WarningBrush", 245, 158, 11),
            _ => ResourceBrush("DangerBrush", 239, 68, 68)
        };
    }

    private static string GetContinentName(VpnNode node)
    {
        var countryCode = GetCountryCode(node);
        return countryCode switch
        {
            "US" or "CA" or "MX" => "\u5317\u7f8e\u6d32",
            "JP" or "KR" or "SG" or "HK" or "TW" or "CN" or "MO" or "MY" or "TH" or "VN" or "ID" or "PH" or "IN" => "\u4e9a\u6d32",
            "GB" or "DE" or "FR" or "NL" or "IT" or "ES" or "SE" or "NO" or "FI" or "PL" or "CH" or "AT" or "BE" or "IE" or "PT" or "DK" or "CZ" => "\u6b27\u6d32",
            "AU" or "NZ" => "\u5927\u6d0b\u6d32",
            "BR" or "AR" or "CL" or "CO" or "PE" => "\u5357\u7f8e\u6d32",
            "AE" or "SA" or "IL" or "QA" or "KW" or "OM" or "BH" => "\u4e2d\u4e1c",
            "ZA" or "EG" or "NG" or "KE" or "MA" => "\u975e\u6d32",
            _ => "\u5176\u4ed6\u5730\u533a"
        };
    }

    private static int GetContinentOrder(string continentName) => continentName switch
    {
        "\u4e9a\u6d32" => 0,
        "\u5317\u7f8e\u6d32" => 1,
        "\u6b27\u6d32" => 2,
        "\u5927\u6d0b\u6d32" => 3,
        "\u5357\u7f8e\u6d32" => 4,
        "\u4e2d\u4e1c" => 5,
        "\u975e\u6d32" => 6,
        _ => 7
    };

    private static string GetCountryDisplayName(VpnNode node)
    {
        var countryCode = GetCountryCode(node);
        return countryCode switch
        {
            "US" => "\u7f8e\u56fd",
            "JP" => "\u65e5\u672c",
            "SG" => "\u65b0\u52a0\u5761",
            "HK" => "\u9999\u6e2f",
            "KR" => "\u97e9\u56fd",
            "TW" => "\u53f0\u6e7e",
            "CA" => "\u52a0\u62ff\u5927",
            "GB" => "\u82f1\u56fd",
            "DE" => "\u5fb7\u56fd",
            "FR" => "\u6cd5\u56fd",
            "NL" => "\u8377\u5170",
            "AU" => "\u6fb3\u5927\u5229\u4e9a",
            "NZ" => "\u65b0\u897f\u5170",
            "IN" => "\u5370\u5ea6",
            "AE" => "\u963f\u8054\u914b",
            "BR" => "\u5df4\u897f",
            _ => NormalizeCountryName(node.Country)
        };
    }

    private static string NormalizeCountryName(string? country)
    {
        if (string.IsNullOrWhiteSpace(country))
        {
            return "\u672a\u77e5\u5730\u533a";
        }

        return country switch
        {
            var name when name.Contains("United States", StringComparison.OrdinalIgnoreCase) || name.Contains("USA", StringComparison.OrdinalIgnoreCase) || name.Contains("\u7f8e\u56fd", StringComparison.OrdinalIgnoreCase) => "\u7f8e\u56fd",
            var name when name.Contains("Japan", StringComparison.OrdinalIgnoreCase) || name.Contains("\u65e5\u672c", StringComparison.OrdinalIgnoreCase) => "\u65e5\u672c",
            var name when name.Contains("Singapore", StringComparison.OrdinalIgnoreCase) || name.Contains("\u65b0\u52a0\u5761", StringComparison.OrdinalIgnoreCase) => "\u65b0\u52a0\u5761",
            var name when name.Contains("Hong Kong", StringComparison.OrdinalIgnoreCase) || name.Contains("\u9999\u6e2f", StringComparison.OrdinalIgnoreCase) => "\u9999\u6e2f",
            var name when name.Contains("Korea", StringComparison.OrdinalIgnoreCase) || name.Contains("\u97e9\u56fd", StringComparison.OrdinalIgnoreCase) => "\u97e9\u56fd",
            var name when name.Contains("Taiwan", StringComparison.OrdinalIgnoreCase) || name.Contains("\u53f0\u6e7e", StringComparison.OrdinalIgnoreCase) => "\u53f0\u6e7e",
            var name when name.Contains("Canada", StringComparison.OrdinalIgnoreCase) || name.Contains("\u52a0\u62ff\u5927", StringComparison.OrdinalIgnoreCase) => "\u52a0\u62ff\u5927",
            var name when name.Contains("United Kingdom", StringComparison.OrdinalIgnoreCase) || name.Contains("Britain", StringComparison.OrdinalIgnoreCase) || name.Contains("\u82f1\u56fd", StringComparison.OrdinalIgnoreCase) => "\u82f1\u56fd",
            var name when name.Contains("Germany", StringComparison.OrdinalIgnoreCase) || name.Contains("\u5fb7\u56fd", StringComparison.OrdinalIgnoreCase) => "\u5fb7\u56fd",
            var name when name.Contains("France", StringComparison.OrdinalIgnoreCase) || name.Contains("\u6cd5\u56fd", StringComparison.OrdinalIgnoreCase) => "\u6cd5\u56fd",
            var name when name.Contains("Netherlands", StringComparison.OrdinalIgnoreCase) || name.Contains("\u8377\u5170", StringComparison.OrdinalIgnoreCase) => "\u8377\u5170",
            var name when name.Contains("Australia", StringComparison.OrdinalIgnoreCase) || name.Contains("\u6fb3\u5927\u5229\u4e9a", StringComparison.OrdinalIgnoreCase) => "\u6fb3\u5927\u5229\u4e9a",
            var name when name.Contains("New Zealand", StringComparison.OrdinalIgnoreCase) || name.Contains("\u65b0\u897f\u5170", StringComparison.OrdinalIgnoreCase) => "\u65b0\u897f\u5170",
            var name when name.Contains("India", StringComparison.OrdinalIgnoreCase) || name.Contains("\u5370\u5ea6", StringComparison.OrdinalIgnoreCase) => "\u5370\u5ea6",
            var name when name.Contains("UAE", StringComparison.OrdinalIgnoreCase) || name.Contains("Emirates", StringComparison.OrdinalIgnoreCase) || name.Contains("\u963f\u8054\u914b", StringComparison.OrdinalIgnoreCase) => "\u963f\u8054\u914b",
            var name when name.Contains("Brazil", StringComparison.OrdinalIgnoreCase) || name.Contains("\u5df4\u897f", StringComparison.OrdinalIgnoreCase) => "\u5df4\u897f",
            _ => country
        };
    }

    private static string GetCountryCode(VpnNode node)
    {
        if (!string.IsNullOrWhiteSpace(node.CountryCode))
        {
            var rawCode = node.CountryCode.Trim().ToUpperInvariant();
            if (rawCode.Length == 2)
            {
                return rawCode;
            }

            if (CountryCodeAliases.TryGetValue(rawCode, out var mappedCode))
            {
                return mappedCode;
            }
        }

        var country = node.Country ?? string.Empty;
        foreach (var pair in CountryNameAliases)
        {
            if (country.Contains(pair.Key, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value;
            }
        }

        return string.Empty;
    }

    private static UIElement CreateFlagVisual(VpnNode node) => CreateFlagVisual(GetCountryCode(node));

    private static UIElement CreateFlagVisual(string countryCode) => countryCode switch
    {
        "US" => CreateUsFlag(),
        "JP" => CreateJapanFlag(),
        "SG" => CreateSingaporeFlag(),
        "HK" => CreateHongKongFlag(),
        "KR" => CreateKoreaFlag(),
        "TW" => CreateTaiwanFlag(),
        "CA" => CreateCanadaFlag(),
        "GB" => CreateUkFlag(),
        "DE" => CreateGermanyFlag(),
        "FR" => CreateFranceFlag(),
        "NL" => CreateNetherlandsFlag(),
        "AU" => CreateAustraliaFlag(),
        "NZ" => CreateNewZealandFlag(),
        "IN" => CreateIndiaFlag(),
        "AE" => CreateUaeFlag(),
        "BR" => CreateBrazilFlag(),
        _ => CreateFallbackFlag(countryCode)
    };

    private static UIElement CreateUsFlag()
    {
        var grid = CreateHorizontalFlag(
            Brush(191, 10, 48),
            Brush(255, 255, 255),
            Brush(191, 10, 48),
            Brush(255, 255, 255),
            Brush(191, 10, 48),
            Brush(255, 255, 255));

        grid.Children.Add(new Border
        {
            Width = 16,
            Height = 12,
            Background = Brush(0, 40, 104),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        });

        return grid;
    }

    private static UIElement CreateJapanFlag()
    {
        var grid = new Grid { Background = Brush(255, 255, 255) };
        grid.Children.Add(new Ellipse
        {
            Width = 12,
            Height = 12,
            Fill = Brush(188, 0, 45),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        return grid;
    }

    private static UIElement CreateSingaporeFlag()
    {
        var grid = CreateHorizontalFlag(Brush(220, 38, 38), Brush(255, 255, 255));
        grid.Children.Add(new Ellipse
        {
            Width = 10,
            Height = 10,
            Fill = Brush(255, 255, 255),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(6, 3, 0, 0)
        });
        grid.Children.Add(new Ellipse
        {
            Width = 7,
            Height = 7,
            Fill = Brush(220, 38, 38),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(8, 4.5, 0, 0)
        });
        return grid;
    }

    private static UIElement CreateHongKongFlag()
    {
        var grid = new Grid { Background = Brush(220, 38, 38) };
        grid.Children.Add(new Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = Brush(255, 255, 255),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        return grid;
    }

    private static UIElement CreateKoreaFlag()
    {
        var grid = new Grid { Background = Brush(255, 255, 255) };
        grid.Children.Add(new Ellipse
        {
            Width = 12,
            Height = 12,
            Fill = Brush(220, 38, 38),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, -3, 0, 0)
        });
        grid.Children.Add(new Ellipse
        {
            Width = 12,
            Height = 12,
            Fill = Brush(37, 99, 235),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 3, 0, 0)
        });
        return grid;
    }

    private static UIElement CreateTaiwanFlag()
    {
        var grid = new Grid { Background = Brush(220, 38, 38) };
        grid.Children.Add(new Border
        {
            Width = 18,
            Height = 12,
            Background = Brush(30, 64, 175),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        });
        return grid;
    }

    private static UIElement CreateCanadaFlag() => CreateVerticalFlag(Brush(220, 38, 38), Brush(255, 255, 255), Brush(220, 38, 38));

    private static UIElement CreateUkFlag()
    {
        var grid = new Grid { Background = Brush(30, 64, 175) };
        grid.Children.Add(new Border
        {
            Width = 6,
            Background = Brush(255, 255, 255),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center
        });
        grid.Children.Add(new Border
        {
            Height = 6,
            Background = Brush(255, 255, 255),
            VerticalAlignment = VerticalAlignment.Center
        });
        grid.Children.Add(new Border
        {
            Width = 3,
            Background = Brush(220, 38, 38),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center
        });
        grid.Children.Add(new Border
        {
            Height = 3,
            Background = Brush(220, 38, 38),
            VerticalAlignment = VerticalAlignment.Center
        });
        return grid;
    }

    private static UIElement CreateGermanyFlag() => CreateHorizontalFlag(Brush(17, 24, 39), Brush(220, 38, 38), Brush(245, 158, 11));
    private static UIElement CreateFranceFlag() => CreateVerticalFlag(Brush(37, 99, 235), Brush(255, 255, 255), Brush(220, 38, 38));
    private static UIElement CreateNetherlandsFlag() => CreateHorizontalFlag(Brush(220, 38, 38), Brush(255, 255, 255), Brush(37, 99, 235));

    private static UIElement CreateAustraliaFlag()
    {
        var grid = new Grid { Background = Brush(30, 64, 175) };
        grid.Children.Add(new Ellipse
        {
            Width = 6,
            Height = 6,
            Fill = Brush(255, 255, 255),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        return grid;
    }

    private static UIElement CreateNewZealandFlag()
    {
        var grid = new Grid { Background = Brush(30, 64, 175) };
        grid.Children.Add(new Ellipse
        {
            Width = 6,
            Height = 6,
            Fill = Brush(239, 68, 68),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        return grid;
    }

    private static UIElement CreateIndiaFlag()
    {
        var grid = CreateHorizontalFlag(Brush(245, 158, 11), Brush(255, 255, 255), Brush(34, 197, 94));
        grid.Children.Add(new Ellipse
        {
            Width = 8,
            Height = 8,
            Stroke = Brush(37, 99, 235),
            StrokeThickness = 1.2,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        return grid;
    }

    private static UIElement CreateUaeFlag()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.8, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2.2, GridUnitType.Star) });

        var left = new Border { Background = Brush(220, 38, 38) };
        Grid.SetColumn(left, 0);
        grid.Children.Add(left);

        var right = CreateHorizontalFlag(Brush(34, 197, 94), Brush(255, 255, 255), Brush(17, 24, 39));
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        return grid;
    }

    private static UIElement CreateBrazilFlag()
    {
        var grid = new Grid { Background = Brush(22, 163, 74) };
        grid.Children.Add(new Polygon
        {
            Fill = Brush(245, 158, 11),
            Points = new PointCollection
            {
                new System.Windows.Point(23, 4),
                new System.Windows.Point(37, 15),
                new System.Windows.Point(23, 26),
                new System.Windows.Point(9, 15)
            }
        });
        grid.Children.Add(new Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = Brush(37, 99, 235),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        return grid;
    }

    private static UIElement CreateFallbackFlag(string countryCode)
    {
        var grid = new Grid { Background = Brush(241, 245, 249) };
        grid.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(countryCode) ? "?" : countryCode,
            Foreground = Brush(15, 23, 42),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        return grid;
    }

    private static Grid CreateHorizontalFlag(params SolidColorBrush[] colors)
    {
        var grid = new Grid();
        foreach (var _ in colors)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        }

        for (var index = 0; index < colors.Length; index++)
        {
            var stripe = new Border { Background = colors[index] };
            Grid.SetRow(stripe, index);
            grid.Children.Add(stripe);
        }

        return grid;
    }

    private static Grid CreateVerticalFlag(params SolidColorBrush[] colors)
    {
        var grid = new Grid();
        foreach (var _ in colors)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        for (var index = 0; index < colors.Length; index++)
        {
            var stripe = new Border { Background = colors[index] };
            Grid.SetColumn(stripe, index);
            grid.Children.Add(stripe);
        }

        return grid;
    }

    private void MessageText(string message)
    {
        NodesStackPanel.Children.Insert(0, new TextBlock
        {
            Text = message,
            Foreground = ResourceBrush("DangerBrush", 239, 68, 68),
            Margin = new Thickness(0, 0, 0, 12)
        });
    }

    private static SolidColorBrush ResourceBrush(string key, byte r, byte g, byte b) =>
        System.Windows.Application.Current.TryFindResource(key) as SolidColorBrush
        ?? new SolidColorBrush(WpfColor.FromRgb(r, g, b));

    private static SolidColorBrush Brush(byte r, byte g, byte b) => new(WpfColor.FromRgb(r, g, b));

    private sealed record NodeVisualRefs(TextBlock LatencyText);

    private static readonly Dictionary<string, string> CountryCodeAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["USA"] = "US",
        ["JPN"] = "JP",
        ["SGP"] = "SG",
        ["HKG"] = "HK",
        ["KOR"] = "KR",
        ["TWN"] = "TW",
        ["CAN"] = "CA",
        ["GBR"] = "GB",
        ["DEU"] = "DE",
        ["FRA"] = "FR",
        ["NLD"] = "NL",
        ["AUS"] = "AU",
        ["NZL"] = "NZ",
        ["IND"] = "IN",
        ["ARE"] = "AE",
        ["BRA"] = "BR"
    };

    private static readonly Dictionary<string, string> CountryNameAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["United States"] = "US",
        ["USA"] = "US",
        ["\u7f8e\u56fd"] = "US",
        ["Japan"] = "JP",
        ["\u65e5\u672c"] = "JP",
        ["Singapore"] = "SG",
        ["\u65b0\u52a0\u5761"] = "SG",
        ["Hong Kong"] = "HK",
        ["\u9999\u6e2f"] = "HK",
        ["Korea"] = "KR",
        ["\u97e9\u56fd"] = "KR",
        ["Taiwan"] = "TW",
        ["\u53f0\u6e7e"] = "TW",
        ["Canada"] = "CA",
        ["\u52a0\u62ff\u5927"] = "CA",
        ["United Kingdom"] = "GB",
        ["Britain"] = "GB",
        ["\u82f1\u56fd"] = "GB",
        ["Germany"] = "DE",
        ["\u5fb7\u56fd"] = "DE",
        ["France"] = "FR",
        ["\u6cd5\u56fd"] = "FR",
        ["Netherlands"] = "NL",
        ["\u8377\u5170"] = "NL",
        ["Australia"] = "AU",
        ["\u6fb3\u5927\u5229\u4e9a"] = "AU",
        ["New Zealand"] = "NZ",
        ["\u65b0\u897f\u5170"] = "NZ",
        ["India"] = "IN",
        ["\u5370\u5ea6"] = "IN",
        ["UAE"] = "AE",
        ["Emirates"] = "AE",
        ["\u963f\u8054\u914b"] = "AE",
        ["Brazil"] = "BR",
        ["\u5df4\u897f"] = "BR"
    };
}

