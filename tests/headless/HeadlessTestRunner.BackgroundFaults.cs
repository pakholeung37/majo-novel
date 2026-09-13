#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Demos.BackgroundExtension;
using Majo.Runtime.Actors;
using Majo.Runtime.Actors.Godot;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Transitions;
using Majo.Runtime.Resources;

namespace Majo.Runtime.HeadlessTests;

public partial class HeadlessTestRunner
{
    private sealed class BackgroundFaultControl
    {
        public TaskCompletionSource Release { get; } = new();
        public bool Loading { get; set; }
    }
    private sealed class FaultBackgroundFactory(Control host, IResourceManager resources, BackgroundFaultControl faults) : IActorFactory
    {
        public string FactoryId => "test.fault-background";
        public string GetInitialAppearance(ActorDefinition definition) => "Day";
        public async Task<IActor> CreateAsync(ActorDefinition definition, ActorCreationMode mode, CancellationToken token)
        {
            var root = new Node2D(); host.AddChild(root);
            var actor = new FaultBackgroundActor(definition, root, resources, faults);
            try
            {
                await using var prepared = await actor.PrepareBackgroundAppearanceAsync("Day", token);
                prepared.Commit(); prepared.ConfirmCommit();
                return actor;
            }
            catch { actor.Destroy(); throw; }
        }
    }
    private sealed class FaultBackgroundActor(ActorDefinition definition, Node2D root, IResourceManager resources, BackgroundFaultControl faults)
        : GodotBackgroundActorBase(definition, root, "Day")
    {
        protected override GodotBackgroundContent LoadContent(string appearance) => throw new NotSupportedException();
        protected override async Task<GodotBackgroundContent> LoadContentAsync(string appearance, CancellationToken token)
        {
            var scope = new ResourceRetentionScope(resources);
            try
            {
                var texture = await scope.AcquireAsync<Texture2D>(new ResourceKey("sample/far"), CompositeBackgroundPlugin.FarPath, token);
                if (appearance == "Broken") throw new InvalidOperationException("Injected failure after texture loading.");
                if (appearance == "Slow") { faults.Loading = true; await faults.Release.Task; }
                if (appearance == "Escape")
                {
                    var invalid = new Node2D();
                    invalid.AddChild(new CanvasLayer());
                    return OwnContent(invalid, new Rect2(0, 0, 100, 100), BackgroundCoverage.Unknown, scope);
                }
                return OwnContent(new Sprite2D { Texture = texture, Centered = false }, new Rect2(Vector2.Zero, texture.GetSize()), BackgroundCoverage.Unknown, scope);
            }
            catch { scope.Dispose(); throw; }
        }
    }
    private sealed class FailingBackgroundDriver : ITransitionDriver
    {
        public DirectionTransitionId Id => new("test-failed-background-driver");
        public IPreparedVisualTransition Prepare(VisualTransitionContext context) => throw new InvalidOperationException("Injected driver preparation failure.");
    }
}
