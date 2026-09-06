using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;

namespace FortnitePorting.Models.Nodes;

// One overridable node-category header colour (with its factory default + a reset).
public partial class HeaderColorOption : ObservableObject
{
    public string Category { get; }
    public Color Default { get; }

    [ObservableProperty] private Color _color;

    public HeaderColorOption(string category, Color @default)
    {
        Category = category;
        Default = @default;
        _color = @default;
    }

    [RelayCommand] private void Reset() => Color = Default;
}

// A selectable viewer font: a display name plus the actual family (from an embedded asset).
public sealed record FontOption(string Name, FontFamily Font);

// Viewer-wide preferences edited from the toolbar's options (gear) flyout. In-memory for
// now (resets on restart). Each setting has its own reset command so the options UI can
// offer per-setting resets (no global reset).
public partial class MaterialViewerSettings : ObservableObject
{
    public static MaterialViewerSettings Instance { get; } = new();

    // Fade nodes unrelated to a single-selected node so it stands out.
    [ObservableProperty] private bool _dimOnSelect = true;

    // Straight wires vs. Nodify's angle-break bend near the ends.
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ConnectionSpacing))] private bool _straightConnections = true;
    public double ConnectionSpacing => StraightConnections ? 0 : 30;

    // Selectable viewer font (drives the window default + node/frame titles).
    public IReadOnlyList<FontOption> Fonts { get; } =
    [
        new("Viga", new FontFamily("avares://FortnitePorting/Assets/Fonts/Viga/Viga-Regular.otf#Viga")),
        new("Chakra Petch", new FontFamily("avares://FortnitePorting/Assets/Fonts/Chakra/ChakraPetch-Bold.otf#Chakra Petch"))
    ];

    [ObservableProperty, NotifyPropertyChangedFor(nameof(ViewerFont))] private FontOption? _selectedFont;
    public FontFamily ViewerFont => (SelectedFont ?? Fonts[0]).Font;

    // Per-category node header colours (defaults mirror MaterialNodeTree's mappings).
    public ObservableCollection<HeaderColorOption> HeaderColors { get; } =
    [
        new("Vector", Color.Parse("#877020")),
        new("Scalar", Color.Parse("#547a2f")),
        new("TextureCoordinate", Color.Parse("#8c1313")),
        new("Texture", Color.Parse("#136384")),
        new("Bool", Color.Parse("#561a1a")),
        new("Switch", Color.Parse("#561a1a")),
        new("MaterialFunction", Color.Parse("#466e86")),
        new("Composite", Color.Parse("#272827")),
        new("FunctionInput", Color.Parse("#7F0000")),
        new("FunctionOutput", Color.Parse("#7F0000")),
        new("MaterialOutput", Color.Parse("#786859")),
        new("Default", Color.Parse("#60815c"))
    ];

    private readonly Dictionary<string, HeaderColorOption> _headerColorsByCategory;

    // Raised whenever any category colour changes, so the graph can restyle live.
    public event Action? HeaderColorsChanged;

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FortnitePorting", "material_viewer_settings.json");

    private bool _loaded;

    private MaterialViewerSettings()
    {
        _headerColorsByCategory = HeaderColors.ToDictionary(option => option.Category);
        SelectedFont = Fonts[0]; // Viga by default

        foreach (var option in HeaderColors)
            option.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName != nameof(HeaderColorOption.Color)) return;
                HeaderColorsChanged?.Invoke();
                Save();
            };

        PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(DimOnSelect) or nameof(StraightConnections) or nameof(SelectedFont)) Save();
        };

        Load();
        _loaded = true;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;

            var json = JObject.Parse(File.ReadAllText(SettingsPath));
            if (json.Value<bool?>("DimOnSelect") is { } dim) DimOnSelect = dim;
            if (json.Value<bool?>("StraightConnections") is { } straight) StraightConnections = straight;
            if (json.Value<string>("Font") is { } fontName && Fonts.FirstOrDefault(font => font.Name == fontName) is { } font)
                SelectedFont = font;

            if (json["HeaderColors"] is JObject headerColors)
                foreach (var property in headerColors.Properties())
                    if (_headerColorsByCategory.TryGetValue(property.Name, out var option)
                        && Color.TryParse(property.Value?.ToString(), out var color))
                        option.Color = color;
        }
        catch (Exception e)
        {
            Log.Warning("Failed to load material viewer settings: {Message}", e.Message);
        }
    }

    private void Save()
    {
        if (!_loaded) return;

        try
        {
            var headerColors = new JObject();
            foreach (var option in HeaderColors.Where(option => option.Color != option.Default))
                headerColors[option.Category] = option.Color.ToString();

            var json = new JObject
            {
                ["DimOnSelect"] = DimOnSelect,
                ["StraightConnections"] = StraightConnections,
                ["Font"] = (SelectedFont ?? Fonts[0]).Name,
                ["HeaderColors"] = headerColors
            };

            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, json.ToString(Formatting.Indented));
        }
        catch (Exception e)
        {
            Log.Warning("Failed to save material viewer settings: {Message}", e.Message);
        }
    }

    public Color GetHeaderColor(string category) =>
        (_headerColorsByCategory.TryGetValue(category, out var option)
            ? option
            : _headerColorsByCategory["Default"]).Color;

    [RelayCommand] private void ResetDimOnSelect() => DimOnSelect = true;
    [RelayCommand] private void ResetStraightConnections() => StraightConnections = true;
    [RelayCommand] private void ResetFont() => SelectedFont = Fonts[0];
}
