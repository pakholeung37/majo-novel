#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Actors;
using Majo.Runtime.Actors.Godot;
using Majo.Runtime.Resources;
using Majo.Runtime.Plugins;

namespace Majo.Demos.BackgroundExtension;

/// <summary>A project plugin using only public Actor and resource contracts.</summary>
public sealed class CompositeBackgroundPlugin : IMajoPlugin
{
    private readonly string farPath;
    private readonly string nearPath;

    public CompositeBackgroundPlugin(string farPath = FarPath, string nearPath = NearPath)
    {
        this.farPath = farPath;
        this.nearPath = nearPath;
    }

    public string Id => FactoryId;
    public void Configure(PluginRegistrationContext context) => Register(context.ActorFactories, farPath, nearPath);
    public const string FactoryId = "sample.composite-background";
    public const string FarPath = "res://demos/background-extension/assets/far.svg";
    public const string NearPath = "res://demos/background-extension/assets/near.svg";

    public static void Register(ActorFactoryRegistry factories, string farPath = FarPath, string nearPath = NearPath) => factories.Register(FactoryId,
        ActorFactoryDomains.Runtime | ActorFactoryDomains.Direction,
        context => new CompositeBackgroundFactory(context.BackgroundRoot, context.Resources, farPath, nearPath),
        (configuration, _) => new ActorDefinition(configuration.Id, FactoryId));

    public static Rect2 Bounds(string appearance) => appearance switch
    {
        "Day" => new Rect2(-80, -20, 320, 180),
        "Night" => new Rect2(20, -60, 240, 240),
        _ => throw new ActorException($"Unknown composite background appearance '{appearance}'."),
    };

    private sealed class CompositeBackgroundFactory(Control root, IResourceManager resources, string farPath, string nearPath) : IActorFactory
    {
        public string FactoryId => CompositeBackgroundPlugin.FactoryId;
        public string GetInitialAppearance(ActorDefinition definition) => "Day";
        public string ResolveAppearance(ActorDefinition definition, string sourceAppearance, string appearance)
        { _ = Bounds(appearance); return appearance; }
        public IReadOnlyList<ResourcePreloadEntry> GetPreloadResources(ActorDefinition definition, string appearance)
        {
            _ = Bounds(appearance);
            return new[] { ResourcePreloadEntry.Create<Texture2D>(new ResourceKey("sample/far"), farPath),
                ResourcePreloadEntry.Create<Texture2D>(new ResourceKey("sample/near"), nearPath) };
        }
        public async Task<IActor> CreateAsync(ActorDefinition definition, ActorCreationMode mode, CancellationToken token)
        {
            var container = new Node2D { Name = definition.Id, Visible = false };
            root.AddChild(container);
            var actor = new CompositeBackgroundActor(definition, container, resources, farPath, nearPath);
            try
            {
                await using var appearance = await actor.PrepareBackgroundAppearanceAsync("Day", token);
                appearance.Commit(); appearance.ConfirmCommit();
                actor.SetVisible(mode == ActorCreationMode.Active);
                return actor;
            }
            catch { actor.Destroy(); throw; }
        }
    }

    private sealed class CompositeBackgroundActor(ActorDefinition definition, Node2D root, IResourceManager resources, string farPath, string nearPath)
        : GodotBackgroundActorBase(definition, root, "Day")
    {
        protected override GodotBackgroundContent LoadContent(string appearance)
        {
            // Only synchronous resource reads here. Direction uses the asynchronous path below.
            _ = resources.Load<Texture2D>(new ResourceKey("sample/far"), farPath);
            _ = resources.Load<Texture2D>(new ResourceKey("sample/near"), nearPath);
            return LoadContentAsync(appearance, default).GetAwaiter().GetResult();
        }

        protected override async Task<GodotBackgroundContent> LoadContentAsync(string appearance, CancellationToken token)
        {
            var bounds = Bounds(appearance);
            var scope = new ResourceRetentionScope(resources);
            try
            {
                var far = await scope.AcquireAsync<Texture2D>(new ResourceKey("sample/far"), farPath, token);
                var near = await scope.AcquireAsync<Texture2D>(new ResourceKey("sample/near"), nearPath, token);
                token.ThrowIfCancellationRequested();
                CanvasItem content = appearance == "Day" ? new Node2D() : new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
                if (appearance == "Day")
                    content.AddChild(new Polygon2D { Color = new Color("315749"), Polygon = new[]
                    { bounds.Position, new Vector2(bounds.End.X, bounds.Position.Y), bounds.End, new Vector2(bounds.Position.X, bounds.End.Y) } });
                content.AddChild(new Sprite2D { Texture = far, Position = bounds.GetCenter() + new Vector2(-25, 10), Scale = new Vector2(1.4f, 1.4f) });
                content.AddChild(new Sprite2D { Texture = near, Position = bounds.GetCenter() + new Vector2(55, -25), Modulate = new Color(1, 1, 1, .65f) });
                content.AddChild(new AnimatedBackdropAccent { Position = bounds.GetCenter(),
                    Polygon = new[] { new Vector2(-15, 20), new Vector2(0, -12), new Vector2(15, 20) } });
                return OwnContent(content, bounds, appearance == "Day" ? BackgroundCoverage.OpaqueRectangle : BackgroundCoverage.Unknown, scope);
            }
            catch { scope.Dispose(); throw; }
        }
    }
}
