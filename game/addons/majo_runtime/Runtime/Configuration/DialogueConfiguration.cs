using Godot;
using Majo.Runtime.Core;

namespace Majo.Runtime.Configuration;

[GlobalClass]
public partial class DialogueConfiguration : Resource
{
    public const string DefaultProjectIdValue = "main";
    public const string DefaultStartNodeValue = "Start";

    [Export]
    public string DefaultProjectId { get; set; } = DefaultProjectIdValue;

    [Export]
    public string DefaultStartNode { get; set; } = DefaultStartNodeValue;

    [Export]
    public string RequiredYarnPluginVersion { get; set; } =
        MajoRuntimeMetadata.RequiredYarnPluginVersion;

    [Export]
    public string RequiredYarnRuntimeVersion { get; set; } =
        MajoRuntimeMetadata.RequiredYarnRuntimeVersion;
}
