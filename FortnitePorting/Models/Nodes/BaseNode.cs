using System;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.Utils;

namespace FortnitePorting.Models.Nodes;

public abstract partial class BaseNode(string expressionName, bool isEngineNode = true) : ObservableObject
{
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DisplayName)), NotifyPropertyChangedFor(nameof(ExpressionDisplayName))] private string _expressionName = expressionName;
    public string ExpressionDisplayName => isEngineNode ? ExpressionName.SubstringBefore("_") : ExpressionName.Replace("_", " ");
    
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DisplayName))] private string _label = expressionName;
    public string DisplayName => Label.Equals(ExpressionName) && isEngineNode ? ExpressionName.Replace(ExpressionPrefix, string.Empty).SubstringBefore("_") : Label;
    
    [ObservableProperty] private Point _location;
    
    [ObservableProperty, NotifyPropertyChangedFor(nameof(BorderBrush))] private bool _isSelected;
    public SolidColorBrush BorderBrush => new(IsSelected ? Color.Parse("#d77601") : Color.Parse("#99121212"));

    // Hidden nodes stay in the graph but fade to near-transparent (see NodeOpacity),
    // so you can declutter without losing your place. Toggled with H / U in the editor.
    [ObservableProperty, NotifyPropertyChangedFor(nameof(NodeOpacity)), NotifyPropertyChangedFor(nameof(Interactable))] private bool _isHidden;

    // Hidden nodes become click-through: you can't select them and clicks pass to whatever
    // is behind them, so a faded node never gets in the way. Bound to the container's IsHitTestVisible.
    public bool Interactable => !IsHidden;

    // Dimmed = not related to the currently focused node (NodeTree sets this on selection)
    // so the selected node and its direct neighbours stand out. Hidden always wins.
    [ObservableProperty, NotifyPropertyChangedFor(nameof(NodeOpacity))] private bool _isDimmed;

    public double NodeOpacity => IsHidden ? 0.1 : IsDimmed ? 0.35 : 1.0;
    
    [ObservableProperty] private ObservableCollection<NodeProperty> _properties = [];

    // Marks a final-output node (the material root, or a function's output nodes).
    // "Clean Up Graph" keeps only nodes that can reach a root; see NodeTree.CleanUp.
    public bool IsRoot;

    // Canvas draw order: comment frames sit behind nodes so they never obscure or
    // steal a click from the nodes they group. Overridden lower by NodeComment.
    public virtual int CanvasZIndex => 10;

    protected abstract string ExpressionPrefix { get; }
}

public abstract partial class Node(string expressionName = "", bool isExpressionName = true) : BaseNode(expressionName, isExpressionName)
{
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HeaderBrush))] private Color? _headerColor;

    // The category this node's colour comes from (Vector/Scalar/Texture/... or "Default"),
    // set only when the colour wasn't assigned explicitly. Lets the options flyout override
    // category colours live. Nodes with an explicit HeaderColor (reroutes, function I/O, the
    // material output) keep it and ignore the category.
    public string? Category;

    // Flat header fill (no gradient), nudged a touch more saturated so category colours read
    // clearly. Explicit colour wins; otherwise the (overridable) category colour is used.
    public Brush HeaderBrush
    {
        get
        {
            var color = HeaderColor
                ?? (Category is { } category ? MaterialViewerSettings.Instance.GetHeaderColor(category) : (Color?) null);
            return new SolidColorBrush(color is { } value ? Saturate(value, 1.18f) : Colors.Gray);
        }
    }

    public void RefreshHeaderBrush() => OnPropertyChanged(nameof(HeaderBrush));

    private static Color Saturate(Color color, float factor)
    {
        var hsl = color.ToHsl();
        return new HslColor(hsl.A, hsl.H, Math.Clamp(hsl.S * factor, 0, 1), hsl.L).ToRgb();
    }
    
    // Flat node body (no gradient) — a single subtle translucent dark surface.
    [ObservableProperty] private Brush _backgroundBrush = new SolidColorBrush(Color.Parse("#C21A1A1C"));

    [ObservableProperty] private object? _content;
    [ObservableProperty] private object? _footerContent;

    [ObservableProperty] private ObservableCollection<NodeSocket> _inputs = [];
    [ObservableProperty] private ObservableCollection<NodeSocket> _outputs = [];

    public FPackageIndex? Package;
    public NodeTree? Subgraph;
    public Node? LinkedNode;
    
    public NodeSocket AddInput(NodeSocket socket)
    {
        socket.Parent = this;
        Inputs.Add(socket);
        return socket;
    }
    
    public NodeSocket AddOutput(NodeSocket socket)
    {
        socket.Parent = this;
        Outputs.Add(socket);
        return socket;
    }
    
    public NodeSocket AddInput(string socketName)
    {
        return AddInput(new NodeSocket(socketName));
    }
    
    public NodeSocket AddOutput(string socketName)
    {
        return AddOutput(new NodeSocket(socketName));
    }
    
    public NodeSocket? GetInput(string socketName)
    {
        return Inputs.FirstOrDefault(input => input.Name.Equals(socketName, StringComparison.OrdinalIgnoreCase));
    }
    
    public NodeSocket? GetOutput(string socketName)
    {
        return Outputs.FirstOrDefault(output => output.Name.Equals(socketName, StringComparison.OrdinalIgnoreCase));
    }
}

public partial class NodeComment(string expressionName, bool isExpressionName = true) : BaseNode(expressionName, isExpressionName)
{
    [ObservableProperty] private Size _size;
    
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeaderBrush))]
    [NotifyPropertyChangedFor(nameof(BackgroundBrush))]
    [NotifyPropertyChangedFor(nameof(CommentBorderBrush))]
    private Color? _commentColor;

    private static readonly Color FallbackColor = Color.Parse("#B7B7B7");
    private Color BaseColor => CommentColor ?? FallbackColor;

    // Title-bar tint: readable but not heavy.
    public Brush HeaderBrush => new SolidColorBrush(new Color(0xB0, BaseColor.R, BaseColor.G, BaseColor.B));

    // Body fill: very subtle so the frame reads as a region, not a solid block that
    // buries the nodes inside it (the old 0x50 fill looked opaque and "leaked").
    public Brush BackgroundBrush => new SolidColorBrush(new Color(0x1F, BaseColor.R, BaseColor.G, BaseColor.B));

    // Crisp edge so the region stays legible even with the faint fill.
    public Brush CommentBorderBrush => new SolidColorBrush(new Color(0xC8, BaseColor.R, BaseColor.G, BaseColor.B));

    // Frames render behind nodes.
    public override int CanvasZIndex => 0;

    protected override string ExpressionPrefix => string.Empty;
}

public partial class NodeProperty : ObservableObject
{
    [ObservableProperty] private string _key;
    [ObservableProperty] private object _value;
}
