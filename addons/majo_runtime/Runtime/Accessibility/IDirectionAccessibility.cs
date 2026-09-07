#nullable enable

using Majo.Runtime.Core;

namespace Majo.Runtime.Accessibility;

public interface IDirectionAccessibility : IRuntimeService
{
    bool ReduceMotion { get; set; }
}
