using System.Reflection;
using Majo.Runtime.Input;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class InputManagerTests
{
    [Fact]
    public void StableInputApiDoesNotExposeGodotOrYarnTypes()
    {
        var stableTypes = typeof(IInputManager).Assembly.GetExportedTypes()
            .Where(type => type.Namespace == "Majo.Runtime.Input");

        foreach (var type in stableTypes)
        {
            foreach (var memberType in GetPublicMemberTypes(type))
            {
                var namespaceName = Unwrap(memberType).Namespace ?? string.Empty;
                Assert.DoesNotContain("Godot", namespaceName, StringComparison.Ordinal);
                Assert.DoesNotContain("Yarn", namespaceName, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void LogicalActionIdsAreValidatedAndHaveStableGodotNames()
    {
        Assert.Throws<ArgumentException>(() => new InputActionId(" "));
        Assert.Equal("custom", new InputActionId(" custom ").Value);
        Assert.Equal("majo_advance", MajoInputActions.ToGodotAction(MajoInputActions.Advance));
        Assert.Equal(
            "majo_option_up",
            MajoInputActions.ToGodotAction(MajoInputActions.OptionUp));
        Assert.Equal("majo_debug", MajoInputActions.ToGodotAction(MajoInputActions.Debug));
        Assert.Equal(MajoInputActions.Debug, MajoInputActions.FromGodotAction("majo_debug"));
        Assert.Equal("majo_settings", MajoInputActions.ToGodotAction(MajoInputActions.Settings));
        Assert.Equal("majo_stepBack", MajoInputActions.ToGodotAction(MajoInputActions.StepBack));
        Assert.Equal(
            "majo_stepForward",
            MajoInputActions.ToGodotAction(MajoInputActions.StepForward));
        Assert.Equal(MajoInputActions.StepBack, MajoInputActions.Rollback);
        Assert.Equal("majo_travel", MajoInputActions.ToGodotAction(MajoInputActions.Travel));
        Assert.Equal(15, MajoInputActions.All.Count);
    }

    [Fact(Timeout = 5_000)]
    public async Task NewestNormalScopeOwnsMatchingActionAndReleaseRestoresPreviousOwner()
    {
        var harness = await InputHarness.CreateAsync();
        var firstEvents = new List<InputActivation>();
        var secondEvents = new List<InputActivation>();
        using var first = harness.Manager.AcquireScope(
            new object(),
            new InputScopeOptions([MajoInputActions.Advance]));
        first.Activated += firstEvents.Add;
        var second = harness.Manager.AcquireScope(
            new object(),
            new InputScopeOptions([MajoInputActions.Advance]));
        second.Activated += secondEvents.Add;

        harness.Backend.Activate(MajoInputActions.Advance, 1);
        second.Enabled = false;
        harness.Backend.Activate(MajoInputActions.Advance, 2);
        second.Enabled = true;
        second.Dispose();
        harness.Backend.Activate(MajoInputActions.Advance, 3);

        Assert.Equal(2, firstEvents.Count);
        Assert.Single(secondEvents);
        Assert.Equal([2UL, 3UL], firstEvents.Select(activation => activation.Frame));
        await harness.ShutdownAsync();
    }

    [Fact(Timeout = 5_000)]
    public async Task ModalScopeCapturesAllowlistedActionsAndBlocksEverythingElse()
    {
        var harness = await InputHarness.CreateAsync();
        var dialogueEvents = new List<InputActivation>();
        var menuEvents = new List<InputActivation>();
        using var dialogue = harness.Manager.AcquireScope(
            new object(),
            new InputScopeOptions());
        dialogue.Activated += dialogueEvents.Add;
        var menu = harness.Manager.AcquireScope(
            new object(),
            new InputScopeOptions(
                [MajoInputActions.Cancel, MajoInputActions.Confirm],
                isModal: true,
                name: "menu"));
        menu.Activated += menuEvents.Add;

        harness.Backend.Activate(MajoInputActions.Confirm, 1);
        harness.Backend.Activate(MajoInputActions.Advance, 2);
        menu.Dispose();
        harness.Backend.Activate(MajoInputActions.Advance, 3);

        Assert.Equal("menu", menu.Name);
        Assert.True(menu.IsModal);
        Assert.Single(menuEvents);
        Assert.Single(dialogueEvents);
        Assert.Equal(3UL, dialogueEvents[0].Frame);
        await harness.ShutdownAsync();
    }

    [Fact(Timeout = 5_000)]
    public async Task MuterBlocksSelectedActionsAndCanBeDisabledOrReleased()
    {
        var harness = await InputHarness.CreateAsync();
        var events = new List<InputActivation>();
        using var scope = harness.Manager.AcquireScope(new object());
        scope.Activated += events.Add;
        var muter = harness.Manager.AcquireMuter(
            new object(),
            new InputMuteOptions([MajoInputActions.Advance], "transition"));

        harness.Backend.Activate(MajoInputActions.Advance, 1);
        harness.Backend.Activate(MajoInputActions.Cancel, 2);
        muter.Enabled = false;
        harness.Backend.Activate(MajoInputActions.Advance, 3);
        muter.Enabled = true;
        muter.Dispose();
        harness.Backend.Activate(MajoInputActions.Advance, 4);

        Assert.Equal("transition", muter.Name);
        Assert.Equal(InputHandleKind.Muter, muter.Kind);
        Assert.Equal([2UL, 3UL, 4UL], events.Select(activation => activation.Frame));
        await harness.ShutdownAsync();
    }

    [Fact(Timeout = 5_000)]
    public async Task SameActionIsDispatchedOnlyOncePerFrame()
    {
        var harness = await InputHarness.CreateAsync();
        var events = new List<InputActivation>();
        var dispatched = new List<InputActivation>();
        using var scope = harness.Manager.AcquireScope(new object());
        scope.Activated += events.Add;
        harness.Manager.Dispatched += dispatched.Add;

        harness.Backend.Activate(MajoInputActions.Confirm, 20, InputDeviceKind.Keyboard);
        harness.Backend.Activate(MajoInputActions.Confirm, 20, InputDeviceKind.Mouse);
        harness.Backend.Activate(MajoInputActions.Advance, 20, InputDeviceKind.Keyboard);
        harness.Backend.Activate(MajoInputActions.Confirm, 21, InputDeviceKind.Gamepad);

        Assert.Equal(3, events.Count);
        Assert.Equal(3, dispatched.Count);
        Assert.Equal(
            [MajoInputActions.Confirm, MajoInputActions.Advance, MajoInputActions.Confirm],
            events.Select(activation => activation.Action));
        await harness.ShutdownAsync();
    }

    [Fact(Timeout = 5_000)]
    public async Task ManagerEnableSwitchDoesNotReplaySuppressedInput()
    {
        var harness = await InputHarness.CreateAsync();
        var events = new List<InputActivation>();
        using var scope = harness.Manager.AcquireScope(new object());
        scope.Activated += events.Add;

        harness.Manager.Enabled = false;
        harness.Backend.Activate(MajoInputActions.Advance, 1);
        harness.Manager.Enabled = true;
        harness.Backend.Activate(MajoInputActions.Advance, 1);

        Assert.Single(events);
        await harness.ShutdownAsync();
    }

    [Fact(Timeout = 5_000)]
    public async Task ConfiguredDisabledStateIsRestoredByReset()
    {
        var backend = new FakeInputBackend();
        var manager = new InputManager(backend, enabled: false);
        await backend.InitializeAsync(CancellationToken.None);
        await manager.InitializeAsync(CancellationToken.None);
        using var scope = manager.AcquireScope(new object());
        var count = 0;
        scope.Activated += _ => count++;

        backend.Activate(MajoInputActions.Advance, 1);
        manager.Enabled = true;
        backend.Activate(MajoInputActions.Advance, 2);
        await manager.ResetAsync(CancellationToken.None);

        Assert.Equal(1, count);
        Assert.False(manager.Enabled);
        await manager.ShutdownAsync(CancellationToken.None);
        await backend.ShutdownAsync(CancellationToken.None);
    }

    [Fact(Timeout = 5_000)]
    public async Task ReleaseOwnerDisposesEveryOwnedHandleOnly()
    {
        var harness = await InputHarness.CreateAsync();
        var owner = new object();
        var otherOwner = new object();
        var scope = harness.Manager.AcquireScope(owner);
        var muter = harness.Manager.AcquireMuter(owner);
        var other = harness.Manager.AcquireScope(otherOwner);

        harness.Manager.ReleaseOwner(owner);

        Assert.True(scope.IsDisposed);
        Assert.True(muter.IsDisposed);
        Assert.False(other.IsDisposed);
        Assert.Equal(1, harness.Manager.ActiveHandleCount);
        await harness.ShutdownAsync();
    }

    [Fact(Timeout = 5_000)]
    public async Task ResetAndShutdownLeaveNoHandlesOrBackendSubscriptions()
    {
        var harness = await InputHarness.CreateAsync();
        var first = harness.Manager.AcquireScope(new object());
        var firstCount = 0;
        first.Activated += _ => firstCount++;

        await harness.Manager.ResetAsync(CancellationToken.None);

        Assert.True(first.IsDisposed);
        Assert.Equal(0, harness.Manager.ActiveHandleCount);
        var second = harness.Manager.AcquireScope(new object());
        var secondCount = 0;
        second.Activated += _ => secondCount++;
        harness.Backend.Activate(MajoInputActions.Advance, 1);
        await harness.Manager.ShutdownAsync(CancellationToken.None);
        harness.Backend.Activate(MajoInputActions.Advance, 2);

        Assert.Equal(0, firstCount);
        Assert.Equal(1, secondCount);
        Assert.True(second.IsDisposed);
        Assert.Equal(0, harness.Manager.ActiveHandleCount);
        Assert.Throws<InvalidOperationException>(() =>
            harness.Manager.AcquireScope(new object()));
        await harness.Backend.ShutdownAsync(CancellationToken.None);
    }

    [Fact(Timeout = 5_000)]
    public async Task ScopeCanReleaseItselfDuringDispatchWithoutDeadlockOrReplay()
    {
        var harness = await InputHarness.CreateAsync();
        var owner = new object();
        var scope = harness.Manager.AcquireScope(owner);
        var count = 0;
        scope.Activated += _ =>
        {
            count++;
            scope.Dispose();
        };

        harness.Backend.Activate(MajoInputActions.Advance, 1);
        harness.Backend.Activate(MajoInputActions.Advance, 2);

        Assert.Equal(1, count);
        Assert.True(scope.IsDisposed);
        await harness.ShutdownAsync();
    }

    private sealed class InputHarness
    {
        private InputHarness(FakeInputBackend backend, InputManager manager)
        {
            Backend = backend;
            Manager = manager;
        }

        public FakeInputBackend Backend { get; }

        public InputManager Manager { get; }

        public static async Task<InputHarness> CreateAsync()
        {
            var backend = new FakeInputBackend();
            var manager = new InputManager(backend);
            await backend.InitializeAsync(CancellationToken.None);
            await manager.InitializeAsync(CancellationToken.None);
            return new InputHarness(backend, manager);
        }

        public async Task ShutdownAsync()
        {
            await Manager.ShutdownAsync(CancellationToken.None);
            await Backend.ShutdownAsync(CancellationToken.None);
        }
    }

    private static IEnumerable<Type> GetPublicMemberTypes(Type type)
    {
        const BindingFlags flags = BindingFlags.Public |
            BindingFlags.Instance |
            BindingFlags.Static |
            BindingFlags.DeclaredOnly;
        foreach (var property in type.GetProperties(flags))
        {
            yield return property.PropertyType;
        }

        foreach (var method in type.GetMethods(flags))
        {
            yield return method.ReturnType;
            foreach (var parameter in method.GetParameters())
            {
                yield return parameter.ParameterType;
            }
        }

        foreach (var eventInfo in type.GetEvents(flags))
        {
            if (eventInfo.EventHandlerType is not null)
            {
                yield return eventInfo.EventHandlerType;
            }
        }
    }

    private static Type Unwrap(Type type)
    {
        if (type.IsArray)
        {
            return Unwrap(type.GetElementType()!);
        }

        if (type.IsGenericType)
        {
            return type.GetGenericArguments().Select(Unwrap).FirstOrDefault(candidate =>
                (candidate.Namespace ?? string.Empty).Contains(
                    "Godot",
                    StringComparison.Ordinal) ||
                (candidate.Namespace ?? string.Empty).Contains(
                    "Yarn",
                    StringComparison.Ordinal)) ?? type;
        }

        return type;
    }
}
