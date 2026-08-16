using System.ComponentModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FortnitePorting.Models.Nodes;

public partial class NodeConnection : ObservableObject
{
    [ObservableProperty] private NodeSocket _from;
    [ObservableProperty] private NodeSocket _to;

    // Gold used to spotlight a selected node's connections.
    private static readonly SolidColorBrush SelectedBrush = new(Color.Parse("#F2B33D"));

    private bool EitherHidden => (From.Parent?.IsHidden ?? false) || (To.Parent?.IsHidden ?? false);
    private bool EitherSelected => (From.Parent?.IsSelected ?? false) || (To.Parent?.IsSelected ?? false);

    // Dim when an endpoint is hidden; full-strength when an endpoint is selected (so the
    // selected node's in/out wires pop across the graph); otherwise the normal half-visible.
    public double ConnectionOpacity => EitherHidden ? 0.1 : EitherSelected ? 1.0 : 0.5;

    // Gold when an endpoint node is selected, so its inputs AND outputs are obvious even
    // across the graph. Otherwise tint by the SOURCE socket's colour, so a wire leaving a
    // texture's R/G/B channel reads red/green/blue (RGB/RGBA/mask/math outputs stay grey).
    public IBrush StrokeBrush => EitherSelected ? SelectedBrush : new SolidColorBrush(From.SocketColor);

    public NodeConnection(NodeSocket from, NodeSocket to)
    {
        _from = from;
        _to = to;

        if (from.Parent is { } fromParent) fromParent.PropertyChanged += OnEndpointChanged;
        if (to.Parent is { } toParent) toParent.PropertyChanged += OnEndpointChanged;
    }

    private void OnEndpointChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(BaseNode.IsHidden) or nameof(BaseNode.IsSelected))
        {
            OnPropertyChanged(nameof(ConnectionOpacity));
            OnPropertyChanged(nameof(StrokeBrush));
        }
    }

    public override string ToString()
    {
        return $"{From.Name} ({From.Parent.ExpressionName}) -> {To.Name} ({To.Parent.ExpressionName})";
    }
}
