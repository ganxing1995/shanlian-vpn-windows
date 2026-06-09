using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ShanlianVpn.Windows.Models;
using ShanlianVpn.Windows.Services;

namespace ShanlianVpn.Windows.Views;

public partial class NodesPage : Page
{
    private readonly Action _onSelected;

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
                Foreground = new SolidColorBrush(Color.FromRgb(102, 112, 133))
            });
            return;
        }

        foreach (var node in AppState.Nodes)
        {
            NodesStackPanel.Children.Add(CreateNodeButton(node));
        }
    }

    private Button CreateNodeButton(VpnNode node)
    {
        var isCurrent = AppState.SelectedNode?.Id == node.Id;
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var textStack = new StackPanel();
        textStack.Children.Add(new TextBlock
        {
            Text = node.DisplayCountry,
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(16, 24, 40))
        });
        textStack.Children.Add(new TextBlock
        {
            Text = "延迟：-- ms",
            Margin = new Thickness(0, 8, 0, 0),
            Foreground = new SolidColorBrush(Color.FromRgb(102, 112, 133))
        });

        var actionText = new TextBlock
        {
            Text = isCurrent ? "当前线路" : "点击切换",
            Foreground = new SolidColorBrush(isCurrent ? Color.FromRgb(27, 110, 243) : Color.FromRgb(102, 112, 133)),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(actionText, 1);

        grid.Children.Add(textStack);
        grid.Children.Add(actionText);

        var button = new Button
        {
            Content = grid,
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(234, 236, 240)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(16),
            Margin = new Thickness(0, 0, 0, 12),
            HorizontalContentAlignment = HorizontalAlignment.Stretch
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
}

