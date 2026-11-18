#nullable enable

using System;

namespace Majo.Runtime.Input;

/// <summary>Optional backend capability for observing the lifetime of an input owner.</summary>
public interface IInputOwnerLifetime
{
    IDisposable? ObserveOwner(object owner, Action released);
}
