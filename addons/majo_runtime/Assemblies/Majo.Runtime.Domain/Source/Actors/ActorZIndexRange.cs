#nullable enable

using System;

namespace Majo.Runtime.Actors;

public static class ActorZIndexRange
{
    public const int Minimum = -4096;

    public const int Maximum = 4096;

    public static void Validate(int zIndex)
    {
        if (zIndex is < Minimum or > Maximum)
        {
            throw new ArgumentOutOfRangeException(
                nameof(zIndex),
                $"Actor z-index must be between {Minimum} and {Maximum}.");
        }
    }
}

