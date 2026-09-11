#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Majo.Demos.Manosaba.Actors.Layered;

public sealed class LayeredCharacterManifest
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public int SchemaVersion { get; init; }

    public string Id { get; init; } = string.Empty;

    public LayeredCharacterCanvas Canvas { get; init; } = new();

    public bool Animated { get; init; }

    public string DefaultAppearance { get; init; } = string.Empty;

    public Dictionary<string, string> CompositionMap { get; init; } =
        new(StringComparer.Ordinal);

    public List<LayeredCharacterLayer> Layers { get; init; } = [];

    public static LayeredCharacterManifest Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var manifest = JsonSerializer.Deserialize<LayeredCharacterManifest>(json, JsonOptions) ??
            throw new InvalidDataException("Layered character manifest is empty.");
        manifest.Validate();
        return manifest;
    }

    public void Validate()
    {
        if (SchemaVersion != 1)
        {
            throw new InvalidDataException(
                $"Unsupported layered character schema version '{SchemaVersion}'.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(DefaultAppearance);
        if (!double.IsFinite(Canvas.Width) || Canvas.Width <= 0 ||
            !double.IsFinite(Canvas.Height) || Canvas.Height <= 0 ||
            !double.IsFinite(Canvas.PixelsPerUnit) || Canvas.PixelsPerUnit <= 0)
        {
            throw new InvalidDataException(
                $"Layered character '{Id}' requires a positive finite canvas and PPU.");
        }

        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var layer in Layers)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(layer.Path);
            ArgumentException.ThrowIfNullOrWhiteSpace(layer.Atlas);
            if (!paths.Add(layer.Path))
            {
                throw new InvalidDataException(
                    $"Layered character '{Id}' duplicates layer path '{layer.Path}'.");
            }

            if (layer.Region.Width <= 0 || layer.Region.Height <= 0)
            {
                throw new InvalidDataException(
                    $"Layered character layer '{layer.Path}' has an invalid atlas region.");
            }
        }
    }
}

public sealed class LayeredCharacterCanvas
{
    public double Width { get; init; } = 15;

    public double Height { get; init; } = 30;

    public double OffsetX { get; init; }

    public double OffsetY { get; init; }

    public double PixelsPerUnit { get; init; } = 100;
}

public sealed class LayeredCharacterLayer
{
    public string Path { get; init; } = string.Empty;

    public string Sprite { get; init; } = string.Empty;

    public string Atlas { get; init; } = string.Empty;

    public LayeredCharacterRegion Region { get; init; } = new();

    public LayeredCharacterPoint Pivot { get; init; } = new(0.5, 0.5);

    public LayeredCharacterPoint Position { get; init; } = new();

    public double RotationDegrees { get; init; }

    public LayeredCharacterPoint Scale { get; init; } = new(1, 1);

    public int SortingOrder { get; init; }

    public bool Enabled { get; init; }

    public string Material { get; init; } = string.Empty;

    public LayeredCharacterColor Color { get; init; } = new();

    public bool FlipX { get; init; }

    public bool FlipY { get; init; }
}

public sealed class LayeredCharacterRegion
{
    public int X { get; init; }

    public int Y { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }
}

public sealed class LayeredCharacterPoint
{
    public LayeredCharacterPoint()
    {
    }

    public LayeredCharacterPoint(double x, double y)
    {
        X = x;
        Y = y;
    }

    public double X { get; init; }

    public double Y { get; init; }
}

public sealed class LayeredCharacterColor
{
    public double R { get; init; } = 1;

    public double G { get; init; } = 1;

    public double B { get; init; } = 1;

    public double A { get; init; } = 1;
}
