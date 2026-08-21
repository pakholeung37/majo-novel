#nullable enable

using Majo.Runtime.Core;
using Majo.Runtime.Dialogue.Commands;

namespace Majo.Runtime.Dialogue.Yarn;

public interface IYarnCommandAdapter : IRuntimeService
{
    RuntimeCommandException? LastFailure { get; }
}
