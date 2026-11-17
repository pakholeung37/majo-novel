#nullable enable

using System;
using System.Threading;
using Godot;

namespace Majo.Runtime.Input.Godot;

public sealed class GodotInputOwnerLifetime : IInputOwnerLifetime
{
    public IDisposable? ObserveOwner(object owner, Action released)
    {
        ArgumentNullException.ThrowIfNull(released);
        return owner is Node node ? new Registration(node, released) : null;
    }

    private sealed class Registration : IDisposable
    {
        private readonly Node node;
        private readonly Action released;
        private int disposed;
        public Registration(Node node, Action released)
        {
            this.node = node;
            this.released = released;
            node.TreeExiting += released;
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0 && GodotObject.IsInstanceValid(node))
                node.TreeExiting -= released;
        }
    }
}
