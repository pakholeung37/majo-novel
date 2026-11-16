#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Actors;
using Majo.Runtime.Actors.Godot;
using Majo.Runtime.Core;
using Majo.Runtime.Dialogue.Commands;
using Majo.Runtime.Direction.Transitions;
using Majo.Runtime.Plugins;
using Majo.Runtime.Resources;

namespace Majo.Demos.SGRE.Emote;

public sealed class EmoteRegistry
{
    private readonly Dictionary<string, EmoteCharacterActor> actors = new(StringComparer.Ordinal);
    public void Register(EmoteCharacterActor actor) => actors[actor.Id] = actor;
    public void Unregister(EmoteCharacterActor actor) => actors.Remove(actor.Id);
    public void SetTalking(string id, bool value) { if (actors.TryGetValue(id, out var actor)) actor.SetTalking(value); }
    public void PlayMotion(string id, string motion)
    {
        if (!actors.TryGetValue(id, out var actor)) throw new ActorException($"E-mote actor '{id}' not found.");
        actor.PlayMotion(motion);
    }
    public EmoteCharacterActor? Find(string id) => actors.GetValueOrDefault(id);
}

public sealed class EmoteCharacterPlugin(EmoteRegistry registry) : IMajoPlugin
{
    public const string Factory = "emote-character";
    public string Id => Factory;
    public void ConfigureServices(RuntimeServiceRegistry services) { }
    public void Configure(PluginRegistrationContext context)
    {
        context.ActorFactories.Register(Factory, ActorFactoryDomains.Runtime | ActorFactoryDomains.Direction,
            factoryContext => new EmoteCharacterFactory(factoryContext, registry),
            (configuration, _) => new ActorDefinition(configuration.Id, Factory,
                new EmoteDefinition(CharacterPositionPreset.Center)));
        context.Commands.Add("emote motion", _ => new EmoteMotionCommand(registry), "emote", "1.0");
    }
}

public sealed record EmoteDefinition(CharacterPositionPreset InitialPosition);

public sealed class EmoteCharacterFactory(ActorFactoryContext context, EmoteRegistry registry) : IConventionActorFactory
{
    public string FactoryId => EmoteCharacterPlugin.Factory;
    public bool PreloadExistingAppearances => false;
    public bool TryResolveDefinition(ActorSpawnRequest request, out ActorDefinition definition)
    {
        var id = request.DefinitionId ?? request.ActorId;
        if (id != "红莉栖" || request.FallbackFactoryId is not ActorFactoryIds.Character and not EmoteCharacterPlugin.Factory)
        {
            definition = null!; return false;
        }
        definition = new ActorDefinition(request.ActorId, FactoryId,
            new EmoteDefinition(request.InitialPosition ?? CharacterPositionPreset.Center), id);
        return true;
    }
    public string GetInitialAppearance(ActorDefinition definition) => "neutral";
    public IReadOnlyList<ResourcePreloadEntry> GetPreloadResources(ActorDefinition definition, string appearance) => [];
    public void ValidateDefinition(ActorDefinition definition) { }
    public Task<IActor> CreateAsync(ActorDefinition definition, ActorCreationMode mode, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var view = new Node2D { Name = definition.Id, Visible = false };
        context.CharacterRoot.AddChild(view);
        try
        {
            var rig = new EmoteRig { Name = "EmoteRig" };
            view.AddChild(rig);
            var actor = new EmoteCharacterActor(definition, view, context.CharacterPositions, rig, registry);
            actor.SetVisible(mode == ActorCreationMode.Active);
            return Task.FromResult<IActor>(actor);
        }
        catch { view.QueueFree(); throw; }
    }
}

public sealed class EmoteCharacterActor : GodotCharacterActorBase
{
    private static readonly Dictionary<string, string> Expressions = new(StringComparer.Ordinal)
    {
        ["neutral"] = "表情_普通", ["uneasy"] = "表情_哀しい00", ["angry"] = "表情_怒る00", ["surprised"] = "表情_驚き00",
    };
    private readonly EmoteRig rig;
    private readonly EmoteRegistry registry;
    public EmoteCharacterActor(ActorDefinition definition, Node2D view, CharacterPositionMap positions,
        EmoteRig rig, EmoteRegistry registry)
        : base(definition, view, positions, definition.GetFactoryData<EmoteDefinition>().InitialPosition, "neutral")
    {
        this.rig = rig; this.registry = registry; registry.Register(this);
    }
    public override void SetAppearance(string appearance) => PersistentWrite(() =>
    {
        EnsureAlive();
        if (!Expressions.TryGetValue(appearance, out var expression)) throw new ActorException($"Unknown E-mote expression '{appearance}'.");
        rig.SetExpression(expression);
        InvalidateAppearance(); Appearance = appearance;
    });
    public void SetTalking(bool value) { if (IsAlive) rig.SetTalking(value); }
    public void PlayMotion(string motion) { EnsureAlive(); rig.PlayMotion(motion); }
    public EmoteRig Rig => rig;
    public override Task PrepareAppearanceAsync(string appearance, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Expressions.ContainsKey(appearance)) throw new ActorException($"Unknown E-mote expression '{appearance}'.");
        return Task.CompletedTask;
    }
    public override Task<IPreparedActorAppearance> PrepareAppearanceTransitionAsync(string sourceAppearance,
        string appearance, AnimationSpec animation, ITransitionDriver driver, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Expressions.ContainsKey(appearance)) throw new ActorException($"Unknown E-mote expression '{appearance}'.");
        return Task.FromResult<IPreparedActorAppearance>(new Prepared(this, appearance));
    }
    protected override void OnDestroy() => registry.Unregister(this);
    private sealed class Prepared(EmoteCharacterActor actor, string appearance) : IPreparedActorAppearance
    {
        public string ResolvedAppearance => appearance;
        public void Commit() => actor.SetAppearance(appearance);
        public void ApplyProgress(float progress) { }
        public void Finish() { }
        public void Dispose() { }
    }
}

[Command("emote motion", Category = "E-mote")]
public sealed partial class EmoteMotionCommand(EmoteRegistry actors) : RuntimeCommand<EmoteMotionCommand.Arguments>
{
    protected override ValueTask ExecuteAsync(Arguments arguments, CommandExecutionContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        actors.PlayMotion(arguments.Actor, arguments.Motion);
        return ValueTask.CompletedTask;
    }
    public sealed record Arguments([Primary] string Actor, [Option] string Motion);
}
