using Godot;
using Majo.Runtime.Core;

namespace Majo.Runtime.Configuration;

[GlobalClass]
public partial class DialogueConfiguration : Resource
{
    [Export]
    public string RequiredYarnPluginVersion { get; set; } =
        MajoRuntimeMetadata.RequiredYarnPluginVersion;

    [Export]
    public string RequiredYarnRuntimeVersion { get; set; } =
        MajoRuntimeMetadata.RequiredYarnRuntimeVersion;
}
