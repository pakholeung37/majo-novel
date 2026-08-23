#nullable enable

using System;
using Majo.Runtime.Core;

namespace Majo.Runtime.Input;

public interface IInputBackend : IRuntimeService
{
    event Action<InputActivation>? Activated;
}
