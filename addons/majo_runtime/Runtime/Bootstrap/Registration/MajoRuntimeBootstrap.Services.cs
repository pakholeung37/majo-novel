#nullable enable

using Godot;
using Majo.Runtime.Core;

namespace Majo.Runtime;

public partial class MajoRuntimeBootstrap : Node
{
    private void RegisterDefaultServices(RuntimeServiceRegistry services)
    {
        RegisterCoreServices(services);
        RegisterAudioServices(services);
        RegisterActorAndStageServices(services);
        RegisterDirectionServices(services);
        RegisterDialogueServices(services);
        RegisterStateAndSaveServices(services);
    }
}
