#nullable enable

using System;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Operations;
using Majo.Runtime.Operations.Godot;

namespace Majo.Runtime.Actors.Godot;

public sealed class BackgroundActor : ActorBase, IBackgroundActor
{
    private const string AppearanceProperty = "appearance";

    public BackgroundActor(
        ActorDefinition definition,
        Control container,
        IOperationScheduler scheduler,
        IActorTextureLoader textureLoader)
        : base(definition, container, scheduler, textureLoader)
    {
        CurrentView = CreateView();
        SetAppearance(definition.DefaultAppearance);
    }

    private Control Container => (Control)base.View;

    public TextureRect CurrentView { get; private set; }

    public override Vector2 Position => Container.Position;

    public IOperationHandle CrossFadeAppearance(
        string appearance,
        double durationSeconds,
        string? operationId = null,
        string? groupId = null) => CrossFadeAppearance(
            appearance,
            new AnimationSpec(durationSeconds),
            operationId,
            groupId);

    public IOperationHandle CrossFadeAppearance(
        string appearance,
        AnimationSpec animation,
        string? operationId = null,
        string? groupId = null)
    {
        var texture = TextureLoader.Load(
            Definition.GetAppearanceResourceKey(appearance),
            Definition.GetAppearanceOverride(appearance));
        TargetAppearance = appearance;
        TextureRect? previousView = null;
        TextureRect? nextView = null;
        OperationDefinition? previousFade = null;
        OperationDefinition? nextFade = null;
        var committed = false;

        void EnsureTransition()
        {
            if (nextView is not null)
            {
                return;
            }

            previousView = CurrentView;
            nextView = CreateView();
            nextView.Texture = texture;
            nextView.Modulate = new Color(1, 1, 1, 0);
            previousFade = GodotTweenOperation.CreatePropertyTween(
                Container,
                previousView,
                new NodePath("modulate:a"),
                0.0f,
                animation.DurationSeconds,
                transition: GodotAnimationSpec.Transition(animation.Transition),
                ease: GodotAnimationSpec.Ease(animation.Ease),
                ignoreTimeScale: animation.TimeMode == AnimationTimeMode.Unscaled);
            nextFade = GodotTweenOperation.CreatePropertyTween(
                Container,
                nextView,
                new NodePath("modulate:a"),
                1.0f,
                animation.DurationSeconds,
                transition: GodotAnimationSpec.Transition(animation.Transition),
                ease: GodotAnimationSpec.Ease(animation.Ease),
                ignoreTimeScale: animation.TimeMode == AnimationTimeMode.Unscaled);
        }

        void Commit()
        {
            if (committed)
            {
                return;
            }

            committed = true;
            if (previousView is not null && GodotObject.IsInstanceValid(previousView))
            {
                previousView.QueueFree();
            }

            CurrentView = nextView ?? throw new ActorException(
                $"Background '{Id}' transition did not create a next view.");
            Appearance = appearance;
        }

        var transition = new OperationDefinition(
            async context =>
            {
                EnsureTransition();
                await Task.WhenAll(
                    previousFade!.ExecuteAsync(context),
                    nextFade!.ExecuteAsync(context));
                Commit();
            },
            completeAsync: async cancellationToken =>
            {
                EnsureTransition();
                await Task.WhenAll(
                    previousFade!.CompleteAsync(cancellationToken),
                    nextFade!.CompleteAsync(cancellationToken));
                Commit();
            },
            cancelAsync: async cancellationToken =>
            {
                if (nextView is null)
                {
                    return;
                }

                await Task.WhenAll(
                    previousFade!.CancelAsync(cancellationToken),
                    nextFade!.CancelAsync(cancellationToken));
                if (previousView is not null && GodotObject.IsInstanceValid(previousView))
                {
                    previousView.Modulate = Colors.White;
                }

                if (GodotObject.IsInstanceValid(nextView))
                {
                    nextView.QueueFree();
                }
            });

        return StartSequencedOperation(
            AppearanceProperty,
            transition,
            operationId,
            groupId,
            animation.ConflictPolicy);
    }

    protected override void ApplyTexture(Texture2D texture)
    {
        CurrentView.Texture = texture;
    }

    protected override void ApplyPosition(Vector2 position)
    {
        Container.Position = position;
    }

    private TextureRect CreateView()
    {
        var view = new TextureRect
        {
            Name = "Appearance",
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        Container.AddChild(view);
        view.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        return view;
    }

}
