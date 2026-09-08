#nullable enable

using System;
using System.Collections.Generic;
using Majo.Runtime.Core;
using Majo.Runtime.Dialogue.Commands;
using Majo.Runtime.Direction.Camera;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Execution;
using Majo.Runtime.Operations.Godot;
using Majo.Runtime.Direction.Transitions;
using Majo.Runtime.Plugins;
using Majo.Runtime.Timing;
using Majo.Runtime.UnitTests.Testing;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class TimingRegistryTests
{
    [Fact]
    public void BuiltInEasingsAreRegisteredWithExpectedDefaultsAndAliases()
    {
        var registry = new EasingRegistry();
        BuiltInEasings.RegisterDefaults(registry);

        foreach (var def in BuiltInEasings.All)
        {
            Assert.True(registry.Contains(def.Id));
            Assert.NotNull(registry.GetRequired(def.Id));
            Assert.Equal(def.Id, registry.Resolve(def.Id.Value).Id);

            if (def.Aliases is not null)
            {
                foreach (var alias in def.Aliases)
                {
                    Assert.Equal(def.Id, registry.Resolve(alias).Id);
                    Assert.Equal(def.Id, registry.Resolve(alias.ToUpperInvariant()).Id);
                }
            }
        }
    }

    [Fact]
    public void BuiltInEasingsEvaluateFiniteOnDomainBounds()
    {
        foreach (var def in BuiltInEasings.All)
        {
            var v0 = def.Evaluate(0.0);
            var vHalf = def.Evaluate(0.5);
            var v1 = def.Evaluate(1.0);

            Assert.True(double.IsFinite(v0), $"{def.Id} evaluated non-finite at 0: {v0}");
            Assert.True(double.IsFinite(vHalf), $"{def.Id} evaluated non-finite at 0.5: {vHalf}");
            Assert.True(double.IsFinite(v1), $"{def.Id} evaluated non-finite at 1: {v1}");
        }
    }

    [Fact]
    public void RegisteringCustomEasingSucceedsAndCanBeResolved()
    {
        var registry = new EasingRegistry();
        BuiltInEasings.RegisterDefaults(registry);

        var elasticOutId = new EasingId("elastic-out");
        var custom = new EasingDefinition(
            elasticOutId,
            t => Math.Sin(-13.0 * (Math.PI / 2.0) * (t + 1.0)) * Math.Pow(2.0, -10.0 * t) + 1.0,
            new[] { "elasticout", "ElasticOut" });

        registry.Register(custom);

        Assert.True(registry.Contains(elasticOutId));
        Assert.Same(custom, registry.GetRequired(elasticOutId));
        Assert.Same(custom, registry.Resolve("elastic-out"));
        Assert.Same(custom, registry.Resolve("elasticout"));
        Assert.Same(custom, registry.Resolve("ELASTICOUT"));
        Assert.Same(custom, registry.Resolve("ElasticOut"));
    }

    [Fact]
    public void RegisteringDuplicateIdThrowsArgumentException()
    {
        var registry = new EasingRegistry();
        BuiltInEasings.RegisterDefaults(registry);

        var duplicate = new EasingDefinition(EasingId.Linear, t => t);
        var ex = Assert.Throws<ArgumentException>(() => registry.Register(duplicate));
        Assert.Contains("linear", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RegisteringDuplicateAliasThrowsArgumentException()
    {
        var registry = new EasingRegistry();
        BuiltInEasings.RegisterDefaults(registry);

        var conflicting = new EasingDefinition(
            new EasingId("custom-sine"),
            t => t,
            new[] { "sinein" }); // conflicts with built-in SineIn alias

        var ex = Assert.Throws<ArgumentException>(() => registry.Register(conflicting));
        Assert.Contains("sinein", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RegisteringInvalidDefinitionThrows()
    {
        var registry = new EasingRegistry();

        // Empty ID
        Assert.Throws<ArgumentException>(() =>
            registry.Register(new EasingDefinition(new EasingId(""), t => t)));

        // Null delegate
        Assert.Throws<ArgumentNullException>(() =>
            registry.Register(new EasingDefinition(new EasingId("test"), null!)));

        // Non-finite evaluation
        Assert.Throws<ArgumentException>(() =>
            registry.Register(new EasingDefinition(new EasingId("nan-ease"), t => double.NaN)));
    }

    [Fact]
    public void RegisteringAfterSealThrowsInvalidOperationException()
    {
        var registry = new EasingRegistry();
        BuiltInEasings.RegisterDefaults(registry);
        registry.Seal();

        var custom = new EasingDefinition(new EasingId("custom"), t => t);
        Assert.Throws<InvalidOperationException>(() => registry.Register(custom));
    }

    [Fact]
    public void ResolveUnknownNameThrowsWithCandidateDiagnostics()
    {
        var registry = new EasingRegistry();
        BuiltInEasings.RegisterDefaults(registry);

        var ex = Assert.Throws<KeyNotFoundException>(() => registry.Resolve("nonexistent-ease"));
        Assert.Contains("nonexistent-ease", ex.Message);
        Assert.Contains("linear", ex.Message);
    }

    [Fact]
    public void PluginCanRegisterEasingViaPluginRegistrationContext()
    {
        var commands = new RuntimeCommandRegistry();
        var transitions = new TransitionDriverRegistry();
        var cameraModifiers = new CameraModifierRegistry();
        var easings = new EasingRegistry();
        BuiltInEasings.RegisterDefaults(easings);

        var context = new PluginRegistrationContext(
            commands,
            transitions,
            cameraModifiers,
            easings: easings);

        var customId = new EasingId("wobble");
        context.Easings.Register(new EasingDefinition(customId, t => Math.Sin(t * Math.PI * 4)));

        Assert.True(easings.Contains(customId));
        Assert.Equal(customId, easings.Resolve("wobble").Id);
    }

    [Fact]
    public void TransitionSpecAcceptsCustomEasingWithoutEnumCheck()
    {
        var customId = new EasingId("custom-bounce");
        var spec = new DirectionTransitionSpec("fade", 1.0, customId);
        var validated = spec.Validate();

        Assert.Equal(customId, validated.Easing);
        Assert.Equal(1.0, validated.DurationSeconds);

        // Empty EasingId should fail
        var invalidSpec = new DirectionTransitionSpec(new DirectionTransitionId("fade"), 1.0, new EasingId(""));
        Assert.Throws<ArgumentException>(() => invalidSpec.Validate());
    }

    [Fact]
    public void CommandBindingConvertsBuiltInAndCustomEasings()
    {
        var easings = new EasingRegistry();
        BuiltInEasings.RegisterDefaults(easings);
        var customId = new EasingId("spring-out");
        easings.Register(new EasingDefinition(customId, t => t));

        var converter = new EasingIdConverter(easings);

        // Built-in
        Assert.Equal(EasingId.SineIn, converter.Parse("sine-in"));
        Assert.Equal(EasingId.SineIn, converter.Parse("sinein"));
        Assert.Equal(EasingId.SineIn, converter.Parse("SineIn"));

        // Custom
        Assert.Equal(customId, converter.Parse("spring-out"));

        // Unknown
        var ex = Assert.Throws<CommandValueFormatException>(() => converter.Parse("nonexistent"));
        Assert.Contains("nonexistent", ex.Message);
    }

    private sealed class TestTransitionCommand : RuntimeCommand<TestTransitionCommand.Arguments>
    {
        private static readonly CommandDefinition<Arguments> StaticDefinition =
            CommandDefinition<Arguments>.Create("test-transition")
                .Group("transition", "Transition", typeof(TransitionArguments), groupParameters: new[]
                {
                    new CommandParameterDeclaration("via", "Via", 20, false, false, typeof(DirectionTransitionId), true, "kind"),
                    new CommandParameterDeclaration("duration", "Duration", 21, false, false, typeof(double), true, "seconds", Minimum: 0),
                    new CommandParameterDeclaration("ease", "Ease", 22, false, false, typeof(EasingId), true, "curve"),
                    new CommandParameterDeclaration("wait", "Wait", 23, false, false, typeof(bool), true, "bool", HasDefault: true, DefaultText: "false"),
                })
                .Build(static context =>
                {
                    var viaSlot = context.GetSlot("transition.via");
                    var durSlot = context.GetSlot("transition.duration");
                    var easeSlot = context.GetSlot("transition.ease");
                    var waitSlot = context.GetSlot("transition.wait");
                    return reader => new Arguments(new TransitionArguments(
                        reader.ReadOptional<double>(durSlot),
                        reader.ReadOptional<EasingId>(easeSlot),
                        reader.ReadOptional<DirectionTransitionId>(viaSlot),
                        reader.ReadOptional<bool>(waitSlot)));
                });

        public override CommandDefinition<Arguments> Definition => StaticDefinition;

        protected override ValueTask ExecuteAsync(Arguments arguments, CommandExecutionContext context) =>
            ValueTask.CompletedTask;

        public sealed record Arguments(TransitionArguments Transition);
    }

    [Fact]
    public void CommandParameterGroupsBindsCustomEasingInTransitionArguments()
    {
        var easings = new EasingRegistry();
        BuiltInEasings.RegisterDefaults(easings);
        var customId = new EasingId("elastic-out");
        easings.Register(new EasingDefinition(customId, t => t));

        var bound = TestCatalogBuilder.CreateBound(
            new TestTransitionCommand(),
            configureConverters: c => c.ReplaceDefault(_ => new EasingIdConverter(easings), "test", "timing"));

        var raw = new RawCommandArguments("test-transition", new Dictionary<string, string>
        {
            ["via"] = "fade",
            ["duration"] = "0.8",
            ["ease"] = "elastic-out",
        });

        var result = (TestTransitionCommand.Arguments)bound.BindUntyped(raw);
        Assert.True(result.Transition.Ease.WasProvided);
        Assert.Equal(customId, result.Transition.Ease.Value);
    }

    [Fact]
    public void BackdropCommand_ResolveTransition_WhenEaseNotProvided()
    {
        var beats = new FakeDirectionBeatRuntime();
        var policy = new DirectionCommandPolicy(beats, DirectionAnimationDefaults.FromConfiguration(null));
        var transitionArgs = TransitionArguments.Create(
            via: CommandValue<DirectionTransitionId>.Explicit(new DirectionTransitionId("dissolve")),
            duration: CommandValue<double>.Explicit(0.45));

        // When no beat is open
        var specNoBeat = policy.ResolveTransition("backdrop", transitionArgs, null!);
        Assert.Equal(EasingId.Linear, specNoBeat.Easing);

        // When beat is open with null defaults
        beats.Begin("b1", "n1", null);
        var specNullBeat = policy.ResolveTransition("backdrop", transitionArgs, null!);
        Assert.Equal(EasingId.Linear, specNullBeat.Easing);

        // When beat is open with DirectionBeatDefaults(null, null)
        beats.AbortOpen();
        beats.Begin("b2", "n2", new DirectionBeatDefaults(null, (EasingId?)null));
        var specEmptyBeat = policy.ResolveTransition("backdrop", transitionArgs, null!);
        Assert.Equal(EasingId.Linear, specEmptyBeat.Easing);
    }

    [Fact]
    public async Task BeatBeginThenBackdropCommand_ViaCatalog_ExecutesSuccessfully()
    {
        var beats = new FakeDirectionBeatRuntime();
        var scopes = new DirectionScopeResolver();
        var nodeAccessor = new DelegateDialogueNodeAccessor(() => "Start");
        var policy = new DirectionCommandPolicy(beats, DirectionAnimationDefaults.FromConfiguration(null));
        var services = new ServiceResolverBuilder()
            .Add<IDirectionTransactionCoordinator>(new DirectionTransactionCoordinator(new DirectionPlanner(), new FakeDirectionBackend { AutoSettle = true }))
            .Add<IDirectionBeatRuntime>(beats)
            .Add<IDirectionScopeResolver>(scopes)
            .Add<IDialogueNodeAccessor>(nodeAccessor)
            .Add<IDirectionCommandPolicy>(policy)
            .Build();

        var registry = new RuntimeCommandRegistry();
        registry.RegisterModule(new DirectionCommandModule());
        registry.SealRegistrations();
        var catalog = registry.BuildCatalog(services);

        var test1 = new DirectionBeatDefaults(null, (EasingId?)null);
        Assert.True(test1.Easing is null, $"test1.Easing is '{test1.Easing}' (hasValue={test1.Easing.HasValue})");

        var beatBegin = catalog.Get("beat begin");
        var context = new CommandExecutionContext("test-exec");
        await beatBegin.ExecuteAsync(new CommandInput("beat begin", Array.Empty<string>()), context);

        Assert.True(beats.HasOpenBeat);
        var beatDefaults = beats.OpenBeatDefaults;
        Assert.NotNull(beatDefaults);
        Assert.True(beatDefaults.Easing is null, $"beatDefaults.Easing is '{beatDefaults.Easing}' (hasValue={beatDefaults.Easing.HasValue})");

        var backdrop = catalog.Get("backdrop");
        await backdrop.ExecuteAsync(new CommandInput("backdrop", new[] { "backdrop/courtroom", "via:dissolve", "duration:0.45" }), context);

        Assert.True(beats.HasOpenBeat);
    }

    [Fact]
    public void CustomTransitionCannotExpandActorStateKinds()
    {
        var transitions = new TransitionDriverRegistry();
        var customId = new DirectionTransitionId("wipe");
        transitions.Register(new FakeTransitionDriver(customId, TransitionDomains.Actor | TransitionDomains.Stage));

        var defaults = new DirectionAnimationDefaults(
            DefaultAnimationDurationSeconds: 0.5,
            DefaultCharacterAnimationDurationSeconds: 0.5,
            DefaultCharacterAnimationKind: customId);

        var policy = new DirectionCommandPolicy(defaults: defaults, transitions: transitions);
        var transitionArgs = TransitionArguments.Create();

        Assert.Throws<DirectionException>(() => policy.ResolveTransition("character", transitionArgs, null!));
    }

    [Fact]
    public async Task UnregisteredDefaultTransitionThrowsOnPolicyInitialize()
    {
        var transitions = new TransitionDriverRegistry();
        var unregisteredId = new DirectionTransitionId("nonexistent");

        var defaults = new DirectionAnimationDefaults(
            DefaultAnimationDurationSeconds: 0.5,
            DefaultCharacterAnimationDurationSeconds: 0.5,
            DefaultCharacterAnimationKind: unregisteredId);

        var policy = new DirectionCommandPolicy(defaults: defaults, transitions: transitions);
        var ex = await Assert.ThrowsAsync<DirectionException>(() => policy.InitializeAsync(CancellationToken.None));
        Assert.Contains("nonexistent", ex.Message);
        Assert.Contains("character", ex.Message);
    }

    [Fact]
    public void TransitionNotSupportingDomainThrowsOnResolve()
    {
        var transitions = new TransitionDriverRegistry();
        var stageOnlyId = new DirectionTransitionId("stage-wipe");
        transitions.Register(new FakeTransitionDriver(stageOnlyId, TransitionDomains.Stage));

        var policy = new DirectionCommandPolicy(transitions: transitions);
        var transitionArgs = TransitionArguments.Create(
            via: CommandValue<DirectionTransitionId>.Explicit(new DirectionTransitionId("stage-wipe")));

        // Character command requires Actor domain, which stage-wipe does not have
        var ex = Assert.Throws<DirectionException>(() => policy.ResolveTransition("character", transitionArgs, null!));
        Assert.Contains("stage-wipe", ex.Message);
    }

    [Fact]
    public async Task ConfiguredEasingIsValidatedAndUsedAfterCommandAndBeatDefaults()
    {
        var easings = new EasingRegistry();
        BuiltInEasings.RegisterDefaults(easings);
        var custom = new EasingId("custom");
        easings.Register(new EasingDefinition(custom, t => t * t));
        var beats = new FakeDirectionBeatRuntime();
        var defaults = DirectionAnimationDefaults.FromConfiguration(null) with { DefaultEasing = custom };
        var policy = new DirectionCommandPolicy(beats, defaults, easings: easings);
        await policy.InitializeAsync(CancellationToken.None);
        var transition = TransitionArguments.Create();
        var timing = TimingArguments.Create();
        Assert.Equal(custom, policy.ResolveTransition("backdrop", transition, null!).Easing);
        Assert.Equal(custom, policy.ResolveMotionTransition("camera move", timing, DirectionTransitionId.Move, null!).Easing);
        Assert.Equal(custom, policy.ResolveCameraTransition("camera", timing, DirectionTransitionId.Dissolve, null!).Easing);
        beats.Begin("b", "n", new DirectionBeatDefaults(null, EasingId.SineIn));
        Assert.Equal(EasingId.SineIn, policy.ResolveTransition("backdrop", transition, null!).Easing);
        var explicitEase = TransitionArguments.Create(ease: CommandValue<EasingId>.Explicit(EasingId.CubicOut));
        Assert.Equal(EasingId.CubicOut, policy.ResolveTransition("backdrop", explicitEase, null!).Easing);
        var invalid = new DirectionCommandPolicy(defaults: defaults with { DefaultEasing = "missing" }, easings: easings);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => invalid.InitializeAsync(CancellationToken.None));
    }

    [Fact]
    public async Task LayoutDefaultsRejectMoveEvenWithRegistryPresent()
    {
        var defaults = DirectionAnimationDefaults.FromConfiguration(null) with
        {
            DefaultScreenAnimationKind = DirectionTransitionId.Move,
        };
        var policy = new DirectionCommandPolicy(defaults: defaults, transitions: new TransitionDriverRegistry());
        await Assert.ThrowsAsync<DirectionException>(() => policy.InitializeAsync(CancellationToken.None));
    }

    [Fact]
    public void CustomSamplingDoesNotSilentlyFallBackAndReportsInvalidRuntimeValues()
    {
        var registry = new EasingRegistry();
        var id = new EasingId("square");
        registry.Register(new EasingDefinition(id, t => t * t));
        Assert.Equal(0.0625f, GodotProgressTween.Sample(registry.GetRequired(id), .25f));
        Assert.Throws<KeyNotFoundException>(() => registry.GetRequired("missing"));
        registry.Register(new EasingDefinition("invalid-between-samples", t => t == .25 ? double.NaN : t));
        Assert.Throws<InvalidOperationException>(() =>
            GodotProgressTween.Sample(registry.GetRequired("invalid-between-samples"), .25f));
        Assert.Equal(.125f, GodotProgressTween.Sample(BuiltInEasings.CubicIn, .5f));
    }

    [Fact]
    public void AppearanceCommandValidatesActorDomainForCustomDrivers()
    {
        var registry = new TransitionDriverRegistry();
        registry.Register(new FakeTransitionDriver("actor-only", TransitionDomains.Actor));
        registry.Register(new FakeTransitionDriver("stage-only", TransitionDomains.Stage));
        var policy = new DirectionCommandPolicy(transitions: registry);
        Assert.Throws<DirectionException>(() => policy.ResolveTransition("character appearance",
            TransitionArguments.Create(via: CommandValue<DirectionTransitionId>.Explicit("actor-only")), null!));
        Assert.Throws<DirectionException>(() => policy.ResolveTransition("character appearance",
            TransitionArguments.Create(via: CommandValue<DirectionTransitionId>.Explicit("stage-only")), null!));
    }

    private sealed class FakeTransitionDriver : ITransitionDriver
    {
        public DirectionTransitionId Id { get; }
        public TransitionDomains Domains { get; }
        public bool SupportsCustomEasing => false;

        public FakeTransitionDriver(DirectionTransitionId id, TransitionDomains domains)
        {
            Id = id;
            Domains = domains;
        }

        public IPreparedVisualTransition Prepare(VisualTransitionContext context) => new DummyPreparedTransition();

        private sealed class DummyPreparedTransition : IPreparedVisualTransition
        {
            public void ApplyProgress(float progress) { }
            public void Dispose() { }
        }
    }
}
