#nullable enable

using System;

namespace Majo.Runtime.State;

public interface IStateDirtyNotifier
{
    event Action? StateDirty;
}
