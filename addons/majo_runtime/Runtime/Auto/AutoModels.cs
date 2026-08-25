#nullable enable

namespace Majo.Runtime.Auto;

public enum AutoMode
{
    Manual,
    Auto,
}

public sealed record AutoModeChanged(AutoMode Previous, AutoMode Current);

public sealed record AutoPauseChanged(bool IsPaused);
