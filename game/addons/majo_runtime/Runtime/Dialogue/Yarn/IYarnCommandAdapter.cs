#nullable enable

using Majo.Runtime.Core;
using Majo.Runtime.Dialogue.Commands;

namespace Majo.Runtime.Dialogue.Yarn;

public interface IYarnCommandAdapter : IRuntimeService
{
    event System.Action<RuntimeCommandException>? CommandFailed;

    RuntimeCommandException? LastFailure { get; }
}
