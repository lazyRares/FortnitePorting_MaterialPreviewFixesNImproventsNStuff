using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using FluentAvalonia.UI.Controls;
using FortnitePorting.Models.Nodes;
using FortnitePorting.WindowModels;
using Nodify;
using Node = FortnitePorting.Models.Nodes.Node;

namespace FortnitePorting.Framework;

public abstract class NodeGraphPreviewWindowBase<TWindow, TModel, TTree> : PreviewWindowBase<TWindow, TModel>
    where TWindow : NodeGraphPreviewWindowBase<TWindow, TModel, TTree>
    where TModel : NodeGraphPreviewWindowModelBase<TTree>
    where TTree : NodeTree, new()
{
    protected bool IsNodePress;
    protected NodifyEditor? GraphEditor;

    protected NodeGraphPreviewWindowBase()
    {
    }

    protected override void OnLoaded(Avalonia.Interactivity.RoutedEventArgs e)
    {
        base.OnLoaded(e);
        GraphEditor ??= this.FindControl<NodifyEditor>("Editor");
    }

    protected void OnTabClosed(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        RemoveTabAndCloseIfEmpty(WindowModel.Trees, args.Item);
    }

    protected void OnTabSelectionChanged(object? sender, SelectionChangedEventArgs args)
    {
        CenterViewport();
    }

    protected void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Home)
        {
            CenterViewport();
            return;
        }

        if (e.Key == Key.F)
        {
            FrameSelection();
            return;
        }

        switch (e.Key)
        {
            case Key.H:
                HideSelected();
                break;
            case Key.U:
                UnhideSelectedOrAll();
                break;
        }
    }

    // H / toolbar: hide every selected node (fades it + its wires to ~10% without removing it).
    protected void HideSelected()
    {
        var tree = WindowModel.SelectedTree;
        if (tree is null) return;
        foreach (var node in tree.NodeCache.Items.Where(node => node.IsSelected)) node.IsHidden = true;
    }

    // U / toolbar: unhide the selection, or -- if nothing is selected -- unhide everything,
    // so a heavily-dimmed graph can always be recovered.
    protected void UnhideSelectedOrAll()
    {
        var tree = WindowModel.SelectedTree;
        if (tree is null) return;

        var selected = tree.NodeCache.Items.Where(node => node.IsSelected).ToArray();
        var targets = selected.Length > 0 ? selected : tree.NodeCache.Items.ToArray();
        foreach (var node in targets) node.IsHidden = false;
    }

    // F key: zoom/pan so the selected node(s) fill the view (whole graph if nothing selected).
    protected void FrameSelection()
    {
        if (GraphEditor is null) return;
        var tree = WindowModel.SelectedTree;
        if (tree is null) return;

        var nodes = tree.NodeCache.Items.Where(node => node.IsSelected).ToArray();
        if (nodes.Length == 0) nodes = tree.NodeCache.Items.ToArray();
        if (nodes.Length == 0) return;

        var minX = nodes.Min(node => node.Location.X);
        var minY = nodes.Min(node => node.Location.Y);
        var maxX = nodes.Max(node => node.Location.X);
        var maxY = nodes.Max(node => node.Location.Y);
        var center = new Point((minX + maxX) / 2, (minY + maxY) / 2);

        // rough node footprint + breathing room so framed nodes aren't flush to the edges
        const double margin = 280;
        var width = maxX - minX + margin;
        var height = maxY - minY + margin;

        // ViewportSize is in graph units at the current zoom, so screen = size * zoom is constant.
        var screenWidth = GraphEditor.ViewportSize.Width * GraphEditor.ViewportZoom;
        var screenHeight = GraphEditor.ViewportSize.Height * GraphEditor.ViewportZoom;
        if (screenWidth <= 0 || screenHeight <= 0)
        {
            CenterViewport();
            return;
        }

        var zoom = Math.Clamp(Math.Min(screenWidth / width, screenHeight / height), 0.15, GraphEditor.MaxViewportZoom);
        GraphEditor.ViewportZoom = zoom;
        GraphEditor.ViewportLocation = new Point(
            center.X - screenWidth / zoom / 2,
            center.Y - screenHeight / zoom / 2);
    }

    protected void CenterViewport()
    {
        if (GraphEditor is null) return;

        var nodes = WindowModel.SelectedTree?.NodeCache.Items.ToArray() ?? [];
        if (nodes.Length == 0) return;

        var avgX = nodes.Sum(node => node.Location.X) / nodes.Length;
        var avgY = nodes.Sum(node => node.Location.Y) / nodes.Length;

        GraphEditor.ViewportLocation = new Point(avgX - GraphEditor.ViewportSize.Width / 2, avgY - GraphEditor.ViewportSize.Height / 2);
    }

    protected void OnSearchSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (GraphEditor is null) return;
        if (sender is not ListBox listBox) return;
        if (listBox.SelectedItem is not BaseNode selectedNode) return;
        if (WindowModel.SelectedTree is null) return;

        WindowModel.SelectedTree.SelectedNode = selectedNode;
        if (!IsNodePress)
        {
            GraphEditor.ViewportLocation = new Point(
                selectedNode.Location.X - GraphEditor.ViewportSize.Width / 2,
                selectedNode.Location.Y - GraphEditor.ViewportSize.Height / 2);
        }

        IsNodePress = false;
    }

    protected void FocusLinkedNode(Node node)
    {
        if (GraphEditor is null || node.LinkedNode is null) return;

        GraphEditor.ViewportZoom = 1;
        GraphEditor.ViewportLocation = new Point(
            node.LinkedNode.Location.X - GraphEditor.ViewportSize.Width / 2,
            node.LinkedNode.Location.Y - GraphEditor.ViewportSize.Height / 2);
        GraphEditor.SelectedItem = node.LinkedNode;
    }
}
