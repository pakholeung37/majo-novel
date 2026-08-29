#nullable enable

using System.Collections.Generic;
using System.Text;
using Yarn.Markup;

namespace Majo.Runtime.Dialogue.Yarn;

/// <summary>
/// Replaces a self-closing <c>[br /]</c> marker with a line-feed character.
/// </summary>
public sealed class MajoLineBreakMarkupReplacer : IAttributeMarkerProcessor
{
    public ReplacementMarkerResult ProcessReplacementMarker(
        MarkupAttribute marker,
        StringBuilder childBuilder,
        List<MarkupAttribute> childAttributes,
        string localeCode)
    {
        childBuilder.Append('\n');
        return new ReplacementMarkerResult(0);
    }
}
