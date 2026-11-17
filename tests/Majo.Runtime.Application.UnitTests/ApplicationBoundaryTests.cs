using Majo.Runtime.Dialogue;
using Majo.Runtime.Input;
using Majo.Runtime.Save;
using Majo.Runtime.Script;
using Majo.Runtime.State;
using Xunit;

namespace Majo.Runtime.Application.UnitTests;

public sealed class ApplicationBoundaryTests
{
    [Fact]
    public void ApplicationReferencesOnlyPureContractsAndPlatformLibraries()
    {
        var app = typeof(StateManager).Assembly;
        Assert.Equal("Majo.Runtime.Application", app.GetName().Name);
        foreach (var type in new[] { typeof(InputManager), typeof(DialoguePlayer), typeof(ScriptPlayback), typeof(SaveSlotManager) })
            Assert.Equal(app, type.Assembly);
        Assert.All(app.GetReferencedAssemblies(), reference => Assert.True(
            reference.Name is "Majo.Runtime.Core" or "Majo.Runtime.Commands" or "Majo.Runtime.Persistence" or "netstandard" ||
                reference.Name!.StartsWith("System.", StringComparison.Ordinal), $"Application references '{reference.Name}'."));
        Assert.DoesNotContain(typeof(ApplicationBoundaryTests).Assembly.GetReferencedAssemblies(), reference =>
            reference.Name!.Contains("Godot", StringComparison.Ordinal) || reference.Name.Contains("Yarn", StringComparison.Ordinal) ||
            reference.Name is "Majo Novel" or "Majo.Runtime.Engine" or "Majo.Demos");
    }

    [Fact]
    public async Task OwnerLifetimeCapabilityReleasesAllOwnedHandlesWithoutAnEngine()
    {
        var lifetime = new OwnerLifetime();
        var manager = new InputManager(new FakeInputBackend(), ownerLifetime: lifetime);
        await manager.InitializeAsync(default);
        var owner = new object();
        var scope = manager.AcquireScope(owner);
        var mute = manager.AcquireMuter(owner);
        using var other = manager.AcquireScope(new object());
        lifetime.Release(owner);
        Assert.True(scope.IsDisposed);
        Assert.True(mute.IsDisposed);
        Assert.False(other.IsDisposed);
        Assert.Equal(1, manager.ActiveHandleCount);
        await manager.ShutdownAsync(default);
        Assert.Empty(lifetime.Callbacks);
    }

    private sealed class OwnerLifetime : IInputOwnerLifetime
    {
        public Dictionary<object, Action> Callbacks { get; } = [];
        public IDisposable ObserveOwner(object owner, Action released)
        {
            Callbacks.Add(owner, released);
            return new Registration(() => Callbacks.Remove(owner));
        }
        public void Release(object owner) => Callbacks[owner]();
        private sealed class Registration(Action detach) : IDisposable
        {
            public void Dispose() => detach();
        }
    }
}
