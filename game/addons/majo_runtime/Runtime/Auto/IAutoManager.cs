#nullable enable

using System;
using Majo.Runtime.Core;

namespace Majo.Runtime.Auto;

public interface IAutoManager : IRuntimeService
{
    event Action<AutoModeChanged>? ModeChanged;

    event Action<AutoPauseChanged>? PauseChanged;

    event Action<Exception>? Failed;

    AutoMode Mode { get; }

    bool IsPaused { get; }

    void SetMode(AutoMode mode);

    void Toggle();
}
