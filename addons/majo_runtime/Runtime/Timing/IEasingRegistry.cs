#nullable enable

using Majo.Runtime.Core;

namespace Majo.Runtime.Timing;

public interface IEasingRegistry : IRuntimeService
{
    void Register(EasingDefinition definition);
    bool TryGet(EasingId id, out EasingDefinition? definition);
    EasingDefinition GetRequired(EasingId id);
    EasingDefinition Resolve(string name);
    bool Contains(EasingId id);
    void Seal();
}
