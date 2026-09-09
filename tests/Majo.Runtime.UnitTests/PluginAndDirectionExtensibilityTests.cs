#nullable enable

using Godot;
using Majo.Runtime.Accessibility;
using Majo.Runtime.Core;
using Majo.Runtime.Diagnostics;
using Majo.Runtime.Dialogue.Commands;
using Majo.Runtime.Direction.Camera;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Transitions;
using Majo.Runtime.Operations;
using Majo.Runtime.Plugins;
using Majo.Runtime.Timing;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class PluginAndDirectionExtensibilityTests
{
    [Fact]
    public void DirectionTransitionIdSupportsEquivalenceAndComparison()
    {
        var cut = DirectionTransitionId.Cut;
        var fade = new DirectionTransitionId("fade");
        var custom = new DirectionTransitionId("  Wipe_Clock  ");

        Assert.Equal("cut", cut.Value);
        Assert.Equal("fade", fade.Value);
        Assert.Equal("wipe_clock", custom.Value);

        Assert.Equal(DirectionTransitionId.Cut, cut);
        Assert.Equal(DirectionTransitionId.Fade, fade);
        Assert.Equal(new DirectionTransitionId("wipe_clock"), custom);

        Assert.True(cut < fade);
        Assert.True(cut <= fade);
        Assert.True(fade > cut);
        Assert.True(fade >= cut);
    }

    [Fact]
    public void DirectionTransitionSpecValidatesCustomTransitions()
    {
        var spec = new DirectionTransitionSpec("glitch", 0.5, EasingId.CubicOut);
        Assert.Equal(new DirectionTransitionId("glitch"), spec.Kind);
        Assert.Equal(0.5, spec.DurationSeconds);
        Assert.Equal(EasingId.CubicOut, spec.Easing);
        Assert.Same(spec, spec.Validate());

        var emptySpec = new DirectionTransitionSpec(string.Empty, 0.5);
        Assert.Throws<ArgumentException>(() => emptySpec.Validate());

        var invalidCut = new DirectionTransitionSpec(DirectionTransitionId.Cut, 1.0);
        Assert.Throws<ArgumentException>(() => invalidCut.Validate());
    }

    [Fact]
    public void TransitionDriverRegistryManagesBuiltinAndCustomDrivers()
    {
        var registry = new TransitionDriverRegistry();

        Assert.True(registry.Contains(DirectionTransitionId.Cut));
        Assert.True(registry.Contains(DirectionTransitionId.Fade));
        Assert.True(registry.Contains(DirectionTransitionId.Dissolve));
        Assert.False(registry.Contains(new DirectionTransitionId("custom_wipe")));

        var customDriver = new TestTransitionDriver(new DirectionTransitionId("custom_wipe"));
        registry.Register(customDriver);

        Assert.True(registry.Contains("custom_wipe"));
        Assert.True(registry.TryGet("custom_wipe", out var resolved));
        Assert.Same(customDriver, resolved);
        Assert.Same(customDriver, registry.GetRequired("custom_wipe"));
        registry.Seal();
        Assert.Throws<InvalidOperationException>(() =>
            registry.Register(new TestTransitionDriver("late")));
        Assert.Throws<InvalidOperationException>(() =>
            registry.Register(new TestTransitionDriver(DirectionTransitionId.Cut)));
    }

    [Fact]
    public void CameraModifierRegistryRejectsDuplicatesAndLateRegistration()
    {
        var registry = new CameraModifierRegistry();
        Assert.Throws<InvalidOperationException>(() =>
            registry.Register(new PunchCameraModifier()));
        registry.Register(new PunchCameraModifier(), replace: true);
        registry.Seal();
        Assert.Throws<InvalidOperationException>(() =>
            registry.Register(new ZoomPulseCameraModifier("late")));
    }

    [Fact]
    public void CameraPoseDeltaCombinesCorrectly()
    {
        var delta1 = new CameraPoseDelta(new Vector2(10, 20), 1.2f, 5f);
        var delta2 = new CameraPoseDelta(new Vector2(-5, 10), 0.5f, -2f);

        var combined = delta1.Combine(delta2);

        Assert.Equal(new Vector2(5, 30), combined.CenterOffset);
        Assert.Equal(0.6f, combined.ZoomMultiplier, 3);
        Assert.Equal(3f, combined.RotationDegreesOffset, 3);
    }

    [Fact]
    public async Task CameraModifierManagerAppliesEffectsAndRespectsReduceMotion()
    {
        var appliedDeltas = new List<CameraPoseDelta>();
        var clearedViews = new List<ViewId>();

        var clock = new TestFrameClock();
        using var lifetime = new TestRuntimeLifetime();
        var scheduler = new OperationRunner(clock, lifetime);
        await scheduler.InitializeAsync(CancellationToken.None);
        var accessibility = new DirectionAccessibility(reduceMotion: false);

        var manager = new CameraModifierManager(
            (viewId, delta) => appliedDeltas.Add(delta),
            viewId => clearedViews.Add(viewId),
            scheduler,
            accessibility);

        await manager.InitializeAsync(CancellationToken.None);

        Assert.True(manager.Contains("shake"));
        Assert.True(manager.Contains("punch"));
        Assert.True(manager.Contains("pulse"));

        var cue = new CameraFxCue(
            ViewId.Main,
            "punch",
            new Dictionary<string, string> { ["strength"] = "10" },
            0.0);

        var handle = manager.ApplyEffect(cue);
        await handle.Completion;

        Assert.Contains(ViewId.Main, clearedViews);
    }

    [Fact]
    public void PluginManagerRegistersAndInitializesPlugins()
    {
        var pluginManager = new PluginManager();
        var testPlugin = new TestPlugin();

        pluginManager.Register(testPlugin);
        Assert.Single(pluginManager.Plugins);
        Assert.Throws<DuplicatePluginException>(() => pluginManager.Register(testPlugin));

        var services = new RuntimeServiceRegistry();
        pluginManager.ConfigureServices(services);
        Assert.True(testPlugin.ConfigureServicesCalled);

        var transitions = new TransitionDriverRegistry();
        using var lifetime = new TestRuntimeLifetime();
        var scheduler = new OperationRunner(new TestFrameClock(), lifetime);
        var commands = new RuntimeCommandRegistry();
        var logger = NullRuntimeLogger.Instance;

        var registration = new PluginRegistrationContext(
            commands,
            transitions,
            new CameraModifierRegistry());
        pluginManager.Configure(registration);
        var context = new PluginContext(new TestServiceResolver(), null, logger);

        pluginManager.InitializeAll(context);
        Assert.True(testPlugin.InitializeCalled);
        Assert.True(transitions.Contains("plugin_transition"));
    }

    [Fact]
    public void PluginConverterRegistrationFlowsIntoCatalogBinding()
    {
        var commands = new RuntimeCommandRegistry();
        var manager = new PluginManager();
        manager.Register(new ConverterPlugin());
        manager.Configure(new PluginRegistrationContext(
            commands,
            new TransitionDriverRegistry(),
            new CameraModifierRegistry()));

        var catalog = commands.BuildCatalog();
        var bound = catalog.Get<ConverterCommand.Arguments>("plugin convert");
        var result = bound.Bind(new CommandInput("plugin convert", new[] { "42" }));

        Assert.Equal(42, result.Value.Value);
    }

    [Fact]
    public void PluginManagerOrdersDependenciesAndSealsRegistration()
    {
        var manager = new PluginManager();
        var order = new List<string>();
        var dependency = new TrackingPlugin("dependency", order);
        var dependent = new TrackingPlugin("dependent", order, "dependency");
        manager.Register(dependent);
        manager.Register(dependency);

        manager.Configure(new PluginRegistrationContext(
            new RuntimeCommandRegistry(),
            new TransitionDriverRegistry(),
            new CameraModifierRegistry()));
        manager.SealRegistration();
        Assert.Throws<PluginRegistrationSealedException>(() => manager.Register(new TrackingPlugin("late", order)));

        manager.InitializeAll(new PluginContext(new TestServiceResolver(), null, NullRuntimeLogger.Instance));
        Assert.Equal(new[] { "dependency:init", "dependent:init" }, order);
    }

    [Fact]
    public void PluginManagerRejectsMissingDependency()
    {
        var manager = new PluginManager();
        manager.Register(new TrackingPlugin("dependent", new List<string>(), "missing"));
        Assert.Throws<PluginDependencyException>(() => manager.InitializeAll(
            new PluginContext(new TestServiceResolver(), null, NullRuntimeLogger.Instance)));
    }

    private sealed class TestTransitionDriver : ITransitionDriver
    {
        public TestTransitionDriver(DirectionTransitionId id) => Id = id;

        public DirectionTransitionId Id { get; }

        public IPreparedVisualTransition Prepare(VisualTransitionContext context) => new DummyPreparedTransition();

        private sealed class DummyPreparedTransition : IPreparedVisualTransition
        {
            public void ApplyProgress(float progress) { }
            public void Dispose() { }
        }
    }

    private sealed class TestPlugin : IMajoPlugin
    {
        public string Id => "test.plugin";
        public bool ConfigureServicesCalled { get; private set; }
        public bool InitializeCalled { get; private set; }

        public void ConfigureServices(RuntimeServiceRegistry services)
        {
            ConfigureServicesCalled = true;
        }

        public void Configure(PluginRegistrationContext context)
        {
            context.Transitions.Register(new TestTransitionDriver("plugin_transition"));
        }

        public void Initialize(PluginContext context) => InitializeCalled = true;
    }

    private sealed class TrackingPlugin : IMajoPlugin
    {
        private readonly List<string> order;
        public TrackingPlugin(string id, List<string> order, params string[] dependencies)
        {
            Id = id;
            this.order = order;
            Dependencies = dependencies;
        }

        public string Id { get; }
        public IReadOnlyCollection<string> Dependencies { get; }
        public void Initialize(PluginContext context) => order.Add($"{Id}:init");
    }

    private readonly record struct PluginNumber(int Value);

    private sealed class PluginNumberConverter : ICommandValueConverter<PluginNumber>
    {
        public PluginNumber Parse(string text) => new(int.Parse(text, System.Globalization.CultureInfo.InvariantCulture));
    }

    private sealed class ConverterPlugin : IMajoPlugin
    {
        public string Id => "converter.plugin";

        public void Configure(PluginRegistrationContext context)
        {
            context.Commands.Converters.AddDefault<PluginNumber>(_ => new PluginNumberConverter(), Id);
            context.Commands.Add("plugin convert", _ => new ConverterCommand(), Id);
        }
    }

    private sealed class ConverterCommand : RuntimeCommand<ConverterCommand.Arguments>
    {
        public sealed record Arguments([Primary] PluginNumber Value);

        private static readonly CommandDefinition<Arguments> StaticDefinition =
            CommandDefinition<Arguments>.Create("plugin convert")
                .Primary<PluginNumber>("value", memberName: "Value")
                .Build(static context =>
                {
                    var slot = context.GetMemberSlot("Value");
                    return reader => new Arguments(reader.ReadRequired<PluginNumber>(slot));
                });

        public override CommandDefinition<Arguments> Definition => StaticDefinition;

        protected override ValueTask ExecuteAsync(Arguments arguments, CommandExecutionContext context) =>
            ValueTask.CompletedTask;
    }

    private sealed class TestFrameClock : IFrameClock
    {
        public double DeltaTime => 0.016;
        public double TotalTime => 0;
        public ulong FrameCount => 0;

        public Task NextFrameAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class TestLogSink : IRuntimeLogSink
    {
        public void Emit(RuntimeLogEvent logEvent) { }
        public void Dispose() { }
    }

    private sealed class TestRuntimeLifetime : IRuntimeLifetime, IDisposable
    {
        private readonly CancellationTokenSource cancellation = new();
        public CancellationToken RuntimeCancellationToken => cancellation.Token;
        public CancellationToken SessionCancellationToken => cancellation.Token;
        public void Dispose() => cancellation.Dispose();
    }

    private sealed class TestServiceResolver : IServiceResolver
    {
        public TService Resolve<TService>() where TService : class => throw new NotSupportedException();
        public object Resolve(Type serviceType) => throw new NotSupportedException();
        public bool TryGet<TService>(out TService? service) where TService : class
        {
            service = null;
            return false;
        }
    }
}
