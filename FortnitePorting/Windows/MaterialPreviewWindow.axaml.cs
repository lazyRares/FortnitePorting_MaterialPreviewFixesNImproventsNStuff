using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Material;
using FortnitePorting.Framework;
using FortnitePorting.Models.Nodes;
using FortnitePorting.Models.Nodes.Material;
using FortnitePorting.WindowModels;
using Nodify;

namespace FortnitePorting.Windows;

public partial class MaterialPreviewWindow : NodeGraphPreviewWindowBase<MaterialPreviewWindow, MaterialPreviewWindowModel, MaterialNodeTree>
{
    private GridLength _searchColumnWidth = new(1, GridUnitType.Star);

    private readonly Dictionary<GraphPin, Control> _pinMarkers = new();
    private NodeTree? _hookedTree;

    public MaterialPreviewWindow()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e); // resolves GraphEditor

        if (GraphEditor is not null)
        {
            // Reposition the pins whenever the viewport pans or zooms.
            GraphEditor.GetObservable(NodifyEditor.ViewportZoomProperty).Subscribe(_ => PositionPins());
            GraphEditor.GetObservable(NodifyEditor.ViewportLocationProperty).Subscribe(_ => PositionPins());
        }

        WindowModel.PropertyChanged += OnWindowModelPropertyChanged;
        HookTree(WindowModel.SelectedTree);
    }

    private void OnWindowModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Switching material tabs swaps in that graph's own pin set.
        if (e.PropertyName == nameof(WindowModel.SelectedTree))
            HookTree(WindowModel.SelectedTree);
    }

    private void HookTree(NodeTree? tree)
    {
        if (ReferenceEquals(tree, _hookedTree)) return;
        if (_hookedTree is not null) _hookedTree.Pins.CollectionChanged -= OnPinsChanged;
        _hookedTree = tree;
        if (_hookedTree is not null) _hookedTree.Pins.CollectionChanged += OnPinsChanged;
        RebuildPinMarkers();
    }

    private void OnPinsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildPinMarkers();

    private void OnEditorPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (GraphEditor is null) return;
        if (!e.GetCurrentPoint(GraphEditor).Properties.IsRightButtonPressed) return;
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;
        if (WindowModel.SelectedTree is not { } tree) return;

        // MouseLocation is already in graph space, so the pin sticks to the graph, not the screen.
        tree.AddPin(GraphEditor.MouseLocation);
        e.Handled = true;
    }

    private void RebuildPinMarkers()
    {
        if (PinOverlay is null) return;
        PinOverlay.Children.Clear();
        _pinMarkers.Clear();

        if (WindowModel.SelectedTree is not { } tree) return;

        foreach (var pin in tree.Pins)
        {
            var marker = CreatePinMarker(pin);
            _pinMarkers[pin] = marker;
            PinOverlay.Children.Add(marker);
        }

        PositionPins();
    }

    private Control CreatePinMarker(GraphPin pin)
    {
        var marker = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(11),
            Background = new SolidColorBrush(Color.Parse("#F2B33D")),
            BorderBrush = new SolidColorBrush(Color.Parse("#663A1E")),
            BorderThickness = new Thickness(2),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new TextBlock
            {
                Text = pin.Number.ToString(),
                Foreground = new SolidColorBrush(Color.Parse("#2A1A05")),
                FontWeight = FontWeight.Bold,
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        ToolTip.SetTip(marker, "Click to remove this pin");
        marker.PointerPressed += (_, args) =>
        {
            WindowModel.SelectedTree?.Pins.Remove(pin);
            args.Handled = true;
        };
        return marker;
    }

    private void PositionPins()
    {
        if (GraphEditor is null) return;
        var viewportLocation = GraphEditor.ViewportLocation;
        var zoom = GraphEditor.ViewportZoom;

        foreach (var (pin, marker) in _pinMarkers)
        {
            var screenX = (pin.Location.X - viewportLocation.X) * zoom;
            var screenY = (pin.Location.Y - viewportLocation.Y) * zoom;
            Canvas.SetLeft(marker, screenX - marker.Width / 2);
            Canvas.SetTop(marker, screenY - marker.Height / 2);
        }
    }

    private void OnSort(object? sender, TappedEventArgs e)
    {
        var tree = WindowModel.SelectedTree;
        if (tree is null) return;

        // Gentle in-place de-overlap only -- the full layered relayout scrambled graphs too much.
        var selection = tree.NodeCache.Items.Where(node => node.IsSelected).ToArray();
        tree.Tidy(gentle: true, selection);
    }

    private void OnCenterView(object? sender, RoutedEventArgs e) => CenterViewport();
    private void OnFrameSelection(object? sender, RoutedEventArgs e) => FrameSelection();
    private void OnHideSelected(object? sender, RoutedEventArgs e) => HideSelected();
    private void OnUnhide(object? sender, RoutedEventArgs e) => UnhideSelectedOrAll();

    private void OnTogglePanel(object? sender, RoutedEventArgs e)
    {
        var column = MainSplitGrid.ColumnDefinitions[2];
        var collapsing = column.Width.Value > 0;
        if (collapsing)
        {
            _searchColumnWidth = column.Width;
            column.Width = new GridLength(0);
            // Collapse AND hide the contents -- a 0-width column still leaks its text at
            // the edge and stays scrollable, so the panel itself must be made invisible.
            SearchPanel.IsVisible = false;
            TogglePanelButton.Content = "<";
        }
        else
        {
            column.Width = _searchColumnWidth;
            SearchPanel.IsVisible = true;
            TogglePanelButton.Content = ">";
        }
    }

    public static void Preview(UObject obj)
    {
        var window = WindowManager.GetOrShowPreview(() => new MaterialPreviewWindow());

        if (window.WindowModel.Trees.FirstOrDefault(mat => mat.Asset?.Name.Equals(obj.Name) ?? false) is
            { } existing)
        {
            window.WindowModel.SelectedTree = existing;
            return;
        }
        
        window.WindowModel.Load(obj);
    }

    private void OnNodePressed(object? sender, PointerPressedEventArgs e)
    {
        IsNodePress = true;
        
        if (e.ClickCount != 2) return;
        if (sender is not Control control) return;
        if (control.DataContext is not MaterialNode node) return;

        if (node.Package is not null && !node.Package.IsNull)
        {
            var package = node.Package.Load();
            switch (package)
            {
                case UMaterial material:
                {
                    Preview(material);
                    break;
                }
                case UMaterialFunction materialFunction:
                {
                    Preview(materialFunction);
                    break;
                }
            }
        }

        if (node.Subgraph is not null)
        {
            WindowModel.Load(node.Subgraph as MaterialNodeTree);
        }

        FocusLinkedNode(node);
    }
}
