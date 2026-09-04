#nullable enable

using Godot;
using Majo.Runtime.Actors;
using Majo.Runtime.Actors.Godot;
using Majo.Runtime.Core;
using Majo.Runtime.Diagnostics;
using Majo.Runtime.Input;
using Majo.Runtime.Input.Godot;
using Majo.Runtime.Localization;
using Majo.Runtime.Operations;
using Majo.Runtime.Operations.Godot;
using Majo.Runtime.Preloading;
using Majo.Runtime.Resources;
using Majo.Runtime.Resources.Godot;
using YarnSpinnerGodot;

namespace Majo.Runtime;

public partial class MajoRuntimeBootstrap : Node
{
    private void RegisterCoreServices(RuntimeServiceRegistry services)
    {
        var activityRegistry = new RuntimeActivityRegistry();
        services
            .AddSingleton<IRuntimeActivityRegistry>(_ => activityRegistry)
            .AddSingleton<IRuntimeActivityObserver>(_ => activityRegistry)
            .AddSingleton<IRuntimeDiagnostics>(
                resolver =>
                {
                    var contributors = new DiagnosticContributorRegistry();
                    ConfigureDiagnosticContributors?.Invoke(contributors);
                    return new RuntimeDiagnosticsService(
                        () => Host?.Services ?? resolver,
                        RingBuffer,
                        contributors,
                        Host?.RuntimeId);
                })
            .AddSingleton<IInputBackend>(
                _ => new GodotInputBackend(this, Configuration!.Input!))
            .AddSingleton<IInputManager>(
                resolver => new InputManager(
                    resolver.Get<IInputBackend>(),
                    Configuration!.Input!.Enabled,
                    Logger),
                typeof(IInputBackend))
            .AddSingleton<IFrameClock>(_ => new GodotFrameClock(GetTree()))
            .AddSingleton<IMissingResourceReporter>(_ => new MissingResourceReporter(Logger))
            .AddSingleton<IResourceManager>(
                resolver => CreateResourceManager(
                    resolver.Get<IFrameClock>(),
                    resolver.Get<IRuntimeLifetime>()),
                typeof(IFrameClock),
                typeof(IRuntimeLifetime))
            .AddSingleton<ILocalizedResourceResolver>(
                resolver => new LocalizedResourceResolver(resolver.Get<IResourceManager>()),
                typeof(IResourceManager))
            .AddSingleton<IActorTextureLoader>(
                resolver => new ResourceActorTextureLoader(
                    resolver.Get<IResourceManager>(),
                    Configuration!.Resources!.FailOnMissingAssets,
                    resolver.Get<IMissingResourceReporter>(),
                    resolver.Get<ILocalizedResourceResolver>(),
                    resolver.Get<ILocaleManager>(),
                    [Configuration.Resources.CharactersPath, Configuration.Resources.BackgroundsPath]),
                typeof(IResourceManager),
                typeof(IMissingResourceReporter),
                typeof(ILocalizedResourceResolver),
                typeof(ILocaleManager))
            .AddSingleton<IOperationScheduler>(
                resolver => new OperationScheduler(
                    resolver.Get<IFrameClock>(),
                    resolver.Get<IRuntimeLifetime>(),
                    Logger),
                typeof(IFrameClock),
                typeof(IRuntimeLifetime))
            .AddSingleton<IScriptPreloadManager>(
                resolver => new ScriptPreloadManager(
                    resolver.Get<IResourceManager>(),
                    resolver.Get<IActorManager>(),
                    Logger),
                typeof(IResourceManager),
                typeof(IActorManager));
    }

    private ResourceManager CreateResourceManager(
        IFrameClock frameClock,
        IRuntimeLifetime lifetime)
    {
        var resourceConfiguration = Configuration!.Resources!;
        var pathResolver = ResourcePathResolver ??
            new GodotProjectResourcePathResolver(resourceConfiguration.RootPath)
                .RegisterSourceExtensions<YarnProject>(YarnProject.YARN_PROJECT_EXTENSION)
                .RegisterSourceExtensions<AudioStream>("wav", "ogg", "mp3");
        var godotProvider = new GodotProjectResourceProvider(pathResolver, frameClock);
        var providers = new ResourceProviderRegistry().Register(godotProvider);
        ConfigureResourceProviders?.Invoke(providers);
        return new ResourceManager(
            providers.Providers,
            lifetime,
            Logger,
            memoryBudgetBytes: (long)resourceConfiguration.MemoryBudgetMiB * 1024 * 1024,
            defaultResourceEstimateBytes:
                (long)resourceConfiguration.DefaultResourceEstimateKiB * 1024);
    }
}
