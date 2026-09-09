#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Godot;

namespace Majo.Runtime.Localization;

public sealed class FontCatalog : IFontCatalog
{
    private readonly Dictionary<string, Font> fonts = new(StringComparer.OrdinalIgnoreCase);
    private double fontScale = 1.0;
    private int lineSpacing;
    private Font? defaultFont;

    public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public event Action? TypographyChanged;

    public Font? DefaultFont
    {
        get => defaultFont;
        set
        {
            defaultFont = value;
            TypographyChanged?.Invoke();
        }
    }

    public double FontScale
    {
        get => fontScale;
        set
        {
            if (value <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "Font scale must be greater than zero.");
            }
            if (Math.Abs(fontScale - value) > 0.001)
            {
                fontScale = value;
                TypographyChanged?.Invoke();
            }
        }
    }

    public int LineSpacing
    {
        get => lineSpacing;
        set
        {
            if (lineSpacing != value)
            {
                lineSpacing = value;
                TypographyChanged?.Invoke();
            }
        }
    }

    public void RegisterFont(string locale, Font font)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(locale);
        ArgumentNullException.ThrowIfNull(font);
        fonts[locale] = font;
        TypographyChanged?.Invoke();
    }

    public Font? ResolveFont(string locale, IReadOnlyList<string> fallbackChain)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(locale);
        ArgumentNullException.ThrowIfNull(fallbackChain);

        foreach (var candidate in fallbackChain)
        {
            if (fonts.TryGetValue(candidate, out var font))
            {
                return font;
            }
        }

        return defaultFont;
    }

    public void SetTypography(double fontScale, int lineSpacing)
    {
        if (fontScale <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fontScale), "Font scale must be greater than zero.");
        }

        var changed = Math.Abs(this.fontScale - fontScale) > 0.001 || this.lineSpacing != lineSpacing;
        this.fontScale = fontScale;
        this.lineSpacing = lineSpacing;

        if (changed)
        {
            TypographyChanged?.Invoke();
        }
    }
}
