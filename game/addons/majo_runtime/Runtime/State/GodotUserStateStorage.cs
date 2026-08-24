#nullable enable

using Godot;

namespace Majo.Runtime.State;

public sealed class GodotUserStateStorage : FileStateStorage
{
    public GodotUserStateStorage(
        string storageNamespace,
        IStateStorageCommitObserver? observer = null)
        : base(ProjectSettings.GlobalizePath("user://"), storageNamespace, observer)
    {
    }
}
