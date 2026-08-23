#nullable enable

using System;
using Majo.Runtime.Core;

namespace Majo.Runtime.Input;

public interface IInputHandle : IDisposable
{
    event Action<InputActivation>? Activated;

    object Owner { get; }

    InputHandleKind Kind { get; }

    string? Name { get; }

    bool IsModal { get; }

    bool Enabled { get; set; }

    bool IsDisposed { get; }
}

public interface IInputManager : IRuntimeService
{
    event Action<InputActivation>? Dispatched;

    bool Enabled { get; set; }

    int ActiveHandleCount { get; }

    IInputHandle AcquireScope(object owner, InputScopeOptions? options = null);

    IInputHandle AcquireMuter(object owner, InputMuteOptions? options = null);

    void ReleaseOwner(object owner);
}
