#nullable enable

using System;
using System.Collections.Generic;

namespace Majo.Runtime.Dialogue.Commands;

public enum CommandParameterSetRule
{
    AtMostOne,
    ExactlyOne,
    AtLeastOne,
}

public sealed record CommandParameterSetSpec(
    CommandParameterSetRule Rule,
    IReadOnlyList<string> Parameters);
