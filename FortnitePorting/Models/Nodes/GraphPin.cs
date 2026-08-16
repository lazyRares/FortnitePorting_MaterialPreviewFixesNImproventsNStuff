using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FortnitePorting.Models.Nodes;

// A place-marker dropped with Shift+right-click. Lives in graph-space (Location) but is
// drawn as a constant-size overlay marker so it stays findable at any zoom level.
public partial class GraphPin : ObservableObject
{
    [ObservableProperty] private Point _location;
    [ObservableProperty] private int _number;

    public GraphPin(Point location, int number)
    {
        _location = location;
        _number = number;
    }
}
