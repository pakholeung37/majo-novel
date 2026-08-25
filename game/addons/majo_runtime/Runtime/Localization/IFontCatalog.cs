#nullable enable

using System;
using System.Collections.Generic;
using Godot;
using Majo.Runtime.Core;

namespace Majo.Runtime.Localization;

public interface IFontCatalog : IRuntimeService
{
    double FontScale { get; set; }
    int LineSpacing { get; set; }
    Font? DefaultFont { get; set; }

    event Action? TypographyChanged;

    Font? ResolveFont(string locale, IReadOnlyList<string> fallbackChain);
    void RegisterFont(string locale, Font font);
    void SetTypography(double fontScale, int lineSpacing);
}
