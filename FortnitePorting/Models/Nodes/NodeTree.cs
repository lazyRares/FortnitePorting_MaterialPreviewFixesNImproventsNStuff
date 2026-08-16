using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.Utils;
using DynamicData;
using DynamicData.Binding;
using FortnitePorting.Services;
using FortnitePorting.Shared.Extensions;
using FortnitePorting.Windows;
using Newtonsoft.Json;
using ReactiveUI;

namespace FortnitePorting.Models.Nodes;

public partial class NodeTree : ObservableObject
{
    [ObservableProperty] private string _treeName;
    [ObservableProperty] private UObject? _asset;
    
    // The search panel's list -- filtered by SearchFilter, sorted by name.
    [ObservableProperty] private ReadOnlyObservableCollection<BaseNode> _nodes = new([]);

    // The graph editor's node list -- an OBSERVABLE mirror of the cache (unfiltered),
    // so that adding/removing nodes at runtime (e.g. Clean Up Graph) updates the canvas.
    // Binding the editor straight to NodeCache.Items wouldn't, since Items is a snapshot.
    [ObservableProperty] private ReadOnlyObservableCollection<BaseNode> _graphNodes = new([]);

    [ObservableProperty] private BaseNode? _selectedNode;

    // Backing store for the editor's multi-selection (rubber-band / shift-click).
    // SelectedNode above stays the "primary" (last-clicked) node that drives the inspector.
    [ObservableProperty] private ObservableCollection<object> _selectedNodes = [];
    
    [ObservableProperty] private ObservableCollection<NodeConnection> _connections = [];

    // Shift+RMB place-markers for this graph (rendered as a constant-size overlay).
    [ObservableProperty] private ObservableCollection<GraphPin> _pins = [];

    [ObservableProperty] private string _searchFilter = string.Empty;

    public GraphPin AddPin(Point location)
    {
        var number = Pins.Count == 0 ? 1 : Pins.Max(pin => pin.Number) + 1;
        var pin = new GraphPin(location, number);
        Pins.Add(pin);
        return pin;
    }

    protected virtual string[] IgnoredPropertyNames { get; set; } = [];
    protected virtual Type[] IgnoredPropertyTypes { get; set; } = [];

    public SourceCache<BaseNode, string> NodeCache { get; set; } = new(item => item.ExpressionName);
    
    private static Type[] JsonPropertyTypes =
    [
        typeof(FScriptStruct), typeof(FStructFallback), typeof(UScriptArray), typeof(UScriptMap), typeof(UScriptSet)
    ];

    public NodeTree()
    {
        var assetFilter = this
            .WhenAnyValue(viewModel => viewModel.SearchFilter)
            .Select(CreateAssetFilter);
        
        NodeCache.Connect()
            .ObserveOn(RxApp.TaskpoolScheduler)
            .Filter(assetFilter)
            .Sort(SortExpressionComparer<BaseNode>.Ascending(x => x.ExpressionName))
            .Bind(out var flatCollection)
            .Subscribe();

        Nodes = flatCollection;

        // Unfiltered, UI-thread mirror of the cache for the editor canvas.
        NodeCache.Connect()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Bind(out var graphCollection)
            .Subscribe();

        GraphNodes = graphCollection;

        // When a single node is focused, fade everything unrelated to it (see UpdateDimming).
        this.WhenAnyValue(tree => tree.SelectedNode).Subscribe(_ => UpdateDimming());

        // Re-apply (or clear) dimming when the option is toggled from the options flyout.
        MaterialViewerSettings.Instance.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MaterialViewerSettings.DimOnSelect)) UpdateDimming();
        };

        // Restyle node headers live when a category colour is changed in the options flyout.
        MaterialViewerSettings.Instance.HeaderColorsChanged += () =>
        {
            foreach (var node in NodeCache.Items.OfType<Node>()) node.RefreshHeaderBrush();
        };
    }

    public virtual void Load(UObject obj)
    {
        TreeName = obj.Name;
        Asset = obj;
    }

    /// <summary>
    /// Fully removes every node that can't reach a final-output node (see BaseNode.IsRoot) --
    /// i.e. anything that doesn't contribute to the material result. Comment frames are kept.
    /// Returns the number of nodes removed.
    /// </summary>
    public int CleanUp()
    {
        var roots = NodeCache.Items.Where(node => node.IsRoot).ToArray();
        if (roots.Length == 0) return 0; // no known output -> refuse to nuke the whole graph

        // Walk upstream from the roots: a node is kept if something it feeds eventually
        // reaches a root. Named-reroute usages link to their declaration via LinkedNode
        // rather than a NodeConnection, so that edge is followed explicitly.
        var reachable = new HashSet<BaseNode>();
        var stack = new Stack<BaseNode>(roots);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (!reachable.Add(current)) continue;

            foreach (var connection in Connections.Where(con => con.To.Parent.Equals(current)))
                stack.Push(connection.From.Parent);

            if (current is Node { LinkedNode: { } linked })
                stack.Push(linked);
        }

        var dead = NodeCache.Items
            .Where(node => node is not NodeComment && !reachable.Contains(node))
            .ToArray();
        if (dead.Length == 0) return 0;

        var deadSet = new HashSet<BaseNode>(dead);
        var deadConnections = Connections
            .Where(con => deadSet.Contains(con.From.Parent) || deadSet.Contains(con.To.Parent))
            .ToArray();

        Connections.RemoveMany(deadConnections);
        NodeCache.RemoveKeys(dead.Select(node => node.ExpressionName));

        if (SelectedNode is not null && deadSet.Contains(SelectedNode)) SelectedNode = null;

        return dead.Length;
    }

    /// <summary>
    /// Tidies node positions. With a non-empty <paramref name="selection"/> only those nodes
    /// move; otherwise the whole graph is arranged. <paramref name="gentle"/> just pushes
    /// overlapping nodes apart in place; otherwise nodes are re-laid into clean left-to-right
    /// dependency columns (output on the right). Comment frames re-hug their contents after.
    /// </summary>
    public void Tidy(bool gentle, IReadOnlyCollection<BaseNode>? selection)
    {
        var everyNode = NodeCache.Items.Where(node => node is not NodeComment).ToArray();
        var scope = (selection is { Count: > 0 }
            ? selection.Where(node => node is not NodeComment)
            : everyNode).ToArray();
        if (scope.Length == 0) return;

        // Snapshot which nodes each frame contains BEFORE moving anything, so a frame can be
        // resized to the new bounding box of the same members afterwards.
        var frames = NodeCache.Items.OfType<NodeComment>().ToArray();
        var membership = frames.ToDictionary(frame => frame, frame => MembersOf(frame, everyNode));

        if (gentle)
            DeOverlap(scope);
        else
            LayeredLayout(scope);

        foreach (var frame in frames)
            ResizeFrame(frame, membership[frame]);
    }

    // Left-to-right layered (Sugiyama-style) layout: rank nodes by longest path from an input
    // so sources land in the left column and the output in the right, order each column by the
    // artist's existing vertical position, then pack columns/rows with no overlap.
    private void LayeredLayout(BaseNode[] scope)
    {
        var inScope = new HashSet<BaseNode>(scope);
        var successors = scope.ToDictionary(node => node, _ => new HashSet<BaseNode>());
        var indegree = scope.ToDictionary(node => node, _ => 0);

        void AddEdge(BaseNode from, BaseNode to)
        {
            if (ReferenceEquals(from, to) || !inScope.Contains(from) || !inScope.Contains(to)) return;
            if (successors[from].Add(to)) indegree[to]++;
        }

        foreach (var connection in Connections)
            AddEdge(connection.From.Parent, connection.To.Parent);
        foreach (var node in scope)
            if (node is Node { LinkedNode: { } linked })
                AddEdge(linked, node); // named-reroute declaration feeds its usage

        // Longest-path ranking via Kahn's algorithm (cycles, if any, keep their default rank 0).
        var rank = scope.ToDictionary(node => node, _ => 0);
        var remaining = new Dictionary<BaseNode, int>(indegree);
        var queue = new Queue<BaseNode>(scope.Where(node => indegree[node] == 0));
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            foreach (var next in successors[node])
            {
                if (rank[next] < rank[node] + 1) rank[next] = rank[node] + 1;
                if (--remaining[next] == 0) queue.Enqueue(next);
            }
        }

        var baseX = scope.Min(node => node.Location.X);
        var baseY = scope.Min(node => node.Location.Y);
        const double gapX = 90, gapY = 45;

        var x = baseX;
        foreach (var column in scope.GroupBy(node => rank[node]).OrderBy(group => group.Key))
        {
            var ordered = column.OrderBy(node => node.Location.Y).ToArray();
            var columnWidth = ordered.Max(EstimateWidth);
            var y = baseY;
            foreach (var node in ordered)
            {
                node.Location = new Point(x, y);
                y += EstimateHeight(node) + gapY;
            }

            x += columnWidth + gapX;
        }
    }

    // In-place declutter: repeatedly separate any two overlapping nodes along the axis of
    // least penetration until nothing overlaps (or we hit the iteration cap).
    private void DeOverlap(BaseNode[] scope)
    {
        const double gap = 24;
        for (var iteration = 0; iteration < 80; iteration++)
        {
            var moved = false;
            for (var i = 0; i < scope.Length; i++)
            for (var j = i + 1; j < scope.Length; j++)
            {
                var a = scope[i];
                var b = scope[j];
                var ra = new Rect(a.Location, new Size(EstimateWidth(a) + gap, EstimateHeight(a) + gap));
                var rb = new Rect(b.Location, new Size(EstimateWidth(b) + gap, EstimateHeight(b) + gap));
                if (!ra.Intersects(rb)) continue;

                var overlapX = Math.Min(ra.Right - rb.Left, rb.Right - ra.Left);
                var overlapY = Math.Min(ra.Bottom - rb.Top, rb.Bottom - ra.Top);

                if (overlapX < overlapY)
                {
                    var push = overlapX / 2 + 0.5;
                    var (left, right) = a.Location.X <= b.Location.X ? (a, b) : (b, a);
                    left.Location = new Point(left.Location.X - push, left.Location.Y);
                    right.Location = new Point(right.Location.X + push, right.Location.Y);
                }
                else
                {
                    var push = overlapY / 2 + 0.5;
                    var (top, bottom) = a.Location.Y <= b.Location.Y ? (a, b) : (b, a);
                    top.Location = new Point(top.Location.X, top.Location.Y - push);
                    bottom.Location = new Point(bottom.Location.X, bottom.Location.Y + push);
                }

                moved = true;
            }

            if (!moved) break;
        }
    }

    private static List<BaseNode> MembersOf(NodeComment frame, IEnumerable<BaseNode> nodes)
    {
        var rect = new Rect(frame.Location, frame.Size);
        return nodes.Where(node => rect.Contains(node.Location)).ToList();
    }

    private static void ResizeFrame(NodeComment frame, List<BaseNode> members)
    {
        if (members.Count == 0) return;

        const double pad = 24, headerRoom = 28;
        var minX = members.Min(node => node.Location.X);
        var minY = members.Min(node => node.Location.Y);
        var maxX = members.Max(node => node.Location.X + EstimateWidth(node));
        var maxY = members.Max(node => node.Location.Y + EstimateHeight(node));

        frame.Location = new Point(minX - pad, minY - pad - headerRoom);
        frame.Size = new Size(maxX - minX + pad * 2, maxY - minY + pad * 2 + headerRoom);
    }

    // Rough on-canvas footprint of a node (positions are set before Avalonia measures the
    // containers, so we estimate from socket counts and the kind of inline content).
    private static double EstimateWidth(BaseNode node)
    {
        if (node is not Node contentNode) return 160;
        return contentNode.Content is Border or Image ? 190 : 165;
    }

    private static double EstimateHeight(BaseNode node)
    {
        if (node is not Node contentNode) return 60;

        var height = 34 + Math.Max(contentNode.Inputs.Count, contentNode.Outputs.Count) * 20;
        height += contentNode.Content switch
        {
            Border or Image => 150, // texture thumbnail
            null => 0,
            _ => 40                 // number box / toggle / colour viewer / code
        };
        return Math.Max(height, 56);
    }

    protected ObservableCollection<NodeProperty> CollectProperties(IPropertyHolder propertyHolder)
    {
        var properties = new ObservableCollection<NodeProperty>();
        foreach (var property in propertyHolder.Properties)
        {
            var targetData = property.Tag!.GenericValue!;
            if (property.Tag is null) continue;
            if (IgnoredPropertyNames.Contains(property.Name.Text)) continue;

            var propType = property.Tag.GenericValue?.GetType();
            if (property.Tag.GenericValue is FScriptStruct scriptStruct)
            {
                propType = scriptStruct.StructType.GetType();
            }
            
            if (propType is null) continue;
            if (IgnoredPropertyTypes.Contains(propType)) continue;

            if (JsonPropertyTypes.Contains(propType) || propType.IsArray)
            {
                var ownerName = propertyHolder switch
                {
                    UObject obj => obj.Owner?.Name.SubstringAfterLast("/") ?? "Material",
                    _ => "Material"
                };
                
                var nodeName = propertyHolder switch
                {
                    UObject obj => obj.Name,
                    _ => "Node"
                };
                
                targetData = new JsonPropertyContainer
                {
                    Name = $"{ownerName}:{nodeName}.{property.Name.Text}",
                    JsonData = JsonConvert.SerializeObject(targetData, Formatting.Indented)
                };
            }
            
            properties.Add(new NodeProperty
            {
                Key = property.Name.Text,
                Value = targetData
            });
        }

        return properties;
    }
    
    private Func<BaseNode, bool> CreateAssetFilter(string searchFilter)
    {
        return asset => FilterExtensions.Filter(asset.Label, searchFilter) || FilterExtensions.Filter(asset.ExpressionName, searchFilter);
    }

    // Fade every node that isn't the focused node or one of its direct neighbours (wired,
    // or linked via a named reroute). Comment frames stay full so they keep their context.
    // Only applies to a single-node focus -- a multi-select shouldn't dim the graph.
    private void UpdateDimming()
    {
        var focus = SelectedNode;
        var selectedCount = NodeCache.Items.Count(node => node.IsSelected);

        if (!MaterialViewerSettings.Instance.DimOnSelect || focus is null or NodeComment || selectedCount > 1)
        {
            foreach (var node in NodeCache.Items) node.IsDimmed = false;
            return;
        }

        var related = new HashSet<BaseNode> { focus };
        foreach (var connection in Connections)
        {
            if (ReferenceEquals(connection.From.Parent, focus)) related.Add(connection.To.Parent);
            if (ReferenceEquals(connection.To.Parent, focus)) related.Add(connection.From.Parent);
        }

        if (focus is Node { LinkedNode: { } linked }) related.Add(linked);
        foreach (var node in NodeCache.Items)
            if (node is Node { LinkedNode: { } usageLink } && ReferenceEquals(usageLink, focus))
                related.Add(node);

        foreach (var node in NodeCache.Items)
            node.IsDimmed = node is not NodeComment && !related.Contains(node);
    }
}

public partial class JsonPropertyContainer : ObservableObject
{
    [ObservableProperty] private string _name;
    [ObservableProperty] private string _jsonData;

    [RelayCommand]
    public async Task OpenProperties()
    {
        TaskService.RunDispatcher(() =>
        {
            PropertiesPreviewWindow.Preview(Name, JsonData);
        });
    }
}

