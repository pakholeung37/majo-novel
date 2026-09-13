#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Actors;
using Majo.Runtime.Actors.Godot;
using Majo.Runtime.Direction.Transitions;

namespace Majo.Demos.Live2D.Actors;

public sealed class Live2DCharacterActor : GodotCharacterActorBase
{
    private const int PriorityForce = 3;
    private const int PlaybackProcessIdle = 1;
    private static readonly StringName MotionFinishedSignal = "motion_finished";
    private readonly Node2D model;
    private readonly Live2DCharacterDefinitionData live2D;
    private readonly Live2DActorRegistry registry;

    public Live2DCharacterActor(
        ActorDefinition definition,
        Node2D view,
        Node2D model,
        CharacterPositionMap positions,
        Live2DActorRegistry registry)
        : this(
            definition,
            view,
            model,
            positions,
            registry,
            definition.GetFactoryData<Live2DCharacterDefinitionData>())
    {
    }

    private Live2DCharacterActor(
        ActorDefinition definition,
        Node2D view,
        Node2D model,
        CharacterPositionMap positions,
        Live2DActorRegistry registry,
        Live2DCharacterDefinitionData live2D)
        : base(definition, view, positions, live2D.InitialPosition, live2D.DefaultAppearance)
    {
        this.model = model ?? throw new ArgumentNullException(nameof(model));
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        this.live2D = live2D;

        model.Call("set_assets", live2D.ModelPath);
        model.Call("set_process_callback", PlaybackProcessIdle);
        LayoutModel();
        SetAppearance(live2D.DefaultAppearance);
        registry.Register(this);
    }

    public override void SetAppearance(string appearance)
    {
        PersistentWrite(() =>
        {
            EnsureAlive();
            ArgumentException.ThrowIfNullOrWhiteSpace(appearance);
            if (!live2D.Expressions.TryGetValue(appearance, out var expression))
            {
                throw new ActorException(
                    $"Live2D actor '{Id}' does not define expression '{appearance}'.");
            }

            InvalidateAppearance();
            if (string.IsNullOrWhiteSpace(expression))
            {
                model.Call("stop_expression");
            }
            else
            {
                model.Call("start_expression", expression);
            }

            Appearance = appearance;
        });
    }

    public override Task PrepareAppearanceAsync(
        string appearance,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!live2D.Expressions.ContainsKey(appearance))
        {
            throw new ActorException(
                $"Live2D actor '{Id}' does not define expression '{appearance}'.");
        }

        return Task.CompletedTask;
    }

    public override Task<IPreparedActorAppearance> PrepareAppearanceTransitionAsync(
        string sourceAppearance,
        string appearance,
        AnimationSpec animation,
        ITransitionDriver driver,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = sourceAppearance;
        _ = animation;
        _ = driver;
        if (!live2D.Expressions.ContainsKey(appearance))
            throw new ActorException(
                $"Live2D actor '{Id}' does not define expression '{appearance}'.");
        return Task.FromResult<IPreparedActorAppearance>(
            new PreparedLive2DAppearance(this, appearance));
    }

    public async Task PlayMotionAsync(
        string motion,
        bool wait,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!live2D.Motions.TryGetValue(motion, out var definition))
        {
            throw new ActorException(
                $"Live2D actor '{Id}' does not define motion '{motion}'.");
        }

        if (!wait)
        {
            model.Call(
                "start_motion",
                definition.Group,
                definition.Index,
                PriorityForce);
            return;
        }

        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void Complete() => completion.TrySetResult();
        var callback = Callable.From(Complete);
        model.Connect(MotionFinishedSignal, callback);
        try
        {
            model.Call(
                "start_motion",
                definition.Group,
                definition.Index,
                PriorityForce);
            await completion.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            if (GodotObject.IsInstanceValid(model) && model.IsConnected(MotionFinishedSignal, callback))
            {
                model.Disconnect(MotionFinishedSignal, callback);
            }
        }
    }

    protected override void OnDestroy() => registry.Unregister(this);

    private void LayoutModel()
    {
        var canvas = model.Call("get_canvas_info").AsGodotDictionary();
        if (!canvas.TryGetValue("size_in_pixels", out var sizeValue) ||
            sizeValue.AsVector2().Y <= 0)
        {
            throw new ActorException(
                $"Live2D model '{live2D.ModelPath}' did not expose valid canvas metadata.");
        }

        var scale = live2D.DisplayHeight / sizeValue.AsVector2().Y;
        model.Scale = Vector2.One * scale;
        model.Position = live2D.VisualOffset;
    }

    private sealed class PreparedLive2DAppearance : IPreparedActorAppearance
    {
        private readonly Live2DCharacterActor actor;
        private bool committed;

        public PreparedLive2DAppearance(Live2DCharacterActor actor, string appearance)
        {
            this.actor = actor;
            ResolvedAppearance = appearance;
        }

        public string ResolvedAppearance { get; }

        public void Commit()
        {
            if (committed)
            {
                throw new InvalidOperationException("Live2D appearance was already committed.");
            }

            actor.SetAppearance(ResolvedAppearance);
            committed = true;
        }

        public void ApplyProgress(float progress) => _ = progress;

        public void Finish() { }

        public void Dispose() { }
    }
}
