#nullable enable

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Actors;
using Majo.Runtime.Audio;
using Majo.Runtime.Auto;
using Majo.Runtime.Core;
using Majo.Runtime.Dialogue;
using Majo.Runtime.Dialogue.Commands;
using Majo.Runtime.Dialogue.Presentation;
using Majo.Runtime.Dialogue.Yarn;
using Majo.Runtime.Dialogue.Yarn.HotReload;
using Majo.Runtime.Dialogue.Yarn.Preloading;
using Majo.Runtime.Dialogue.Yarn.Presentation;
using Majo.Runtime.Direction.Execution;
using Majo.Runtime.History;
using Majo.Runtime.Input;
using Majo.Runtime.Localization;
using Majo.Runtime.Operations;
using Majo.Runtime.Played;
using Majo.Runtime.Resources;
using Majo.Runtime.Save;
using Majo.Runtime.Script;
using Majo.Runtime.Settings;
using Majo.Runtime.State;
using Majo.Runtime.UI.History;

namespace Majo.Runtime;

public partial class MajoRuntimeBootstrap : Node
{
    private void RegisterDialogueServices(RuntimeServiceRegistry services)
    {
        services
            .AddSingleton<IFontCatalog>(_ => new FontCatalog
            {
                FontScale = Configuration?.Localization?.FontScale ?? 1.0,
                LineSpacing = Configuration?.Localization?.LineSpacing ?? 0,
            })
            .AddSingleton<ILocaleManager>(
                resolver =>
                {
                    var defaultLoc = Configuration?.Localization?.DefaultLocale ??
                                     Configuration?.Settings?.DialogueLocale ?? "en";
                    var defaultVoiceLoc = Configuration?.Settings?.VoiceLocale ??
                                          Configuration?.Audio?.VoiceLocale ?? "en";
                    var supportedLocs = Configuration?.Localization?.SupportedLocales?.ToArray() ?? ["en", "zh-CN"];
                    LocaleManager? manager = null;
                    manager = new LocaleManager(
                        defaultLoc,
                        supportedLocs,
                        resolver.Get<IFontCatalog>(),
                        isBusyCheck: () => FindRuntimeService<IYarnCommandAdapter>()?.HasActiveCommands == true,
                        onLocaleApplied: locale => OnLocaleApplied(locale, manager!),
                        applyEngineLocale: TranslationServer.SetLocale,
                        logger: Logger,
                        waitForSafeBoundary: WaitForLocaleSwitchBoundaryAsync,
                        defaultVoiceLocale: defaultVoiceLoc);
                    ConfigureLocales?.Invoke(manager);
                    return manager;
                }, typeof(IFontCatalog));

        var presenterRegistry = new DialoguePresenterRegistry();
        ConfigureDialoguePresenters?.Invoke(presenterRegistry);
        var markupHandlerRegistry = new DialogueMarkupHandlerRegistry();
        ConfigureDialogueMarkupHandlers?.Invoke(markupHandlerRegistry);

        services
            .AddSingleton<IYarnProjectVersionStore>(
                resolver => new YarnProjectVersionStore(
                    resolver.Get<IResourceManager>(),
                    Configuration!.Resources!.ScriptsPath),
                typeof(IResourceManager))
            .AddSingleton<IYarnDialogueBackend>(
                resolver =>
                {
                    var voicePresenter = new VoiceDialoguePresenter(
                        resolver.Get<IAudioManager>(),
                        resolver.Get<ILocaleManager>(),
                        resolver.Get<ILocalizedResourceResolver>());
                    var presenters = new IDialoguePresenter[presenterRegistry.Presenters.Count + 1];
                    for (var i = 0; i < presenterRegistry.Presenters.Count; i++)
                    {
                        presenters[i] = presenterRegistry.Presenters[i];
                    }
                    presenters[^1] = voicePresenter;

                    return new YarnDialogueBackend(
                        this,
                        resolver.Get<IResourceManager>(),
                        Configuration!.Resources!.ScriptsPath,
                        presenters,
                        resolver.Get<ILocaleManager>(),
                        ResolveSpeakerName,
                        Logger,
                        Configuration.Script!.RequireExactProgramFingerprint,
                        resolver.Get<IYarnProjectVersionStore>(),
                        resolver.Get<IDirectionScopeResolver>(),
                        new YarnWindowPreloader(resolver.Get<IResourceManager>(),
                            Configuration.Resources, Configuration.Audio!,
                            resolver.Get<ILocaleManager>(), resolver.Get<ILocalizedResourceResolver>(), resolver.Get<IActorManager>(), Logger,
                            resolver.Get<IDirectionTransactionCoordinator>(), resolver.Get<IDirectionScopeResolver>(),
                            resolver.Get<Majo.Runtime.Actors.Godot.DirectionActorDefinitionEnvironment>().Resolver));
                },
                typeof(IResourceManager),
                typeof(IAudioManager),
                typeof(ILocaleManager),
                typeof(ILocalizedResourceResolver),
                typeof(IYarnProjectVersionStore),
                typeof(IDirectionScopeResolver),
                typeof(IDirectionTransactionCoordinator),
                typeof(Majo.Runtime.Actors.Godot.DirectionActorDefinitionEnvironment),
                typeof(IActorManager))
            .AddSingleton<IDialoguePresentation>(
                resolver =>
                {
                    return new YarnDefaultDialoguePresentation(
                        resolver.Get<IYarnDialogueBackend>(),
                        resolver.Get<IInputManager>(),
                        resolver.Get<IFrameClock>(),
                        Configuration!.Presentation!,
                        markupHandlerRegistry.Handlers,
                        resolver.Get<ILocaleManager>(),
                        resolver.Get<IFontCatalog>(),
                        ResolveSpeakerName);
                },
                typeof(IYarnDialogueBackend),
                typeof(IInputManager),
                typeof(IFrameClock),
                typeof(ILocaleManager),
                typeof(IFontCatalog))
            .AddSingleton<IDialogueVariables>(
                resolver => new DialogueVariables(
                    resolver.Get<IYarnDialogueBackend>().VariableStore),
                typeof(IYarnDialogueBackend))
            .AddSingleton<IDialogueNodeAccessor>(
                resolver =>
                {
                    var player = resolver.Get<IDialoguePlayer>();
                    return new DelegateDialogueNodeAccessor(() => player.CurrentNode);
                }, typeof(IDialoguePlayer))
            .AddSingleton<ISaveProductAccessor>(
                // Save is optional and depends on the script graph. Resolve at
                // use time so this accessor neither creates a cycle nor captures
                // the default factory's instance before plugins replace it.
                _ => new DelegateSaveProductAccessor(FindRuntimeService<ISaveProductManager>))
            .AddSingleton<IYarnCommandAdapter>(
                _ => defaultYarnCommandAdapter)
            .AddSingleton<IDialoguePlayer>(
                resolver =>
                {
                    return new DialoguePlayer(
                        resolver.Get<IYarnDialogueBackend>(),
                        Configuration!.Dialogue!.DefaultProjectId,
                        Configuration.Dialogue.DefaultStartNode,
                        Logger);
                },
                typeof(IYarnDialogueBackend),
                typeof(IDialoguePresentation))
            .AddSingleton<IScriptManager>(
                resolver => new ScriptManager(resolver.Get<IYarnDialogueBackend>()),
                typeof(IYarnDialogueBackend))
            .AddSingleton<IScriptPlayback>(
                resolver => new ScriptPlayback(
                    resolver.Get<IDialoguePlayer>(),
                    resolver.Get<IDialoguePresentation>(),
                    resolver.Get<IYarnDialogueBackend>(),
                    Logger),
                typeof(IDialoguePlayer),
                typeof(IDialoguePresentation),
                typeof(IYarnDialogueBackend))
            .AddSingleton<IYarnHotReloadService>(
                resolver =>
                {
                    var hotReload = Configuration!.Dialogue!.HotReload;
                    var resourceRoot = ProjectSettings.GlobalizePath(Configuration.Resources!.RootPath);
                    var hasSourceOverride = !string.IsNullOrWhiteSpace(hotReload.SourceRootOverride);
                    var configuredRoot = string.IsNullOrWhiteSpace(hotReload.SourceRootOverride)
                        ? System.IO.Path.Combine(
                            resourceRoot,
                            Configuration.Resources.ScriptsPath)
                        : hotReload.SourceRootOverride;
                    return new YarnHotReloadService(
                        resolver.Get<IYarnProjectVersionStore>(),
                        resolver.Get<IScriptPlayback>(),
                        resolver.Get<IYarnDialogueBackend>(),
                        hotReload,
                        Configuration.Dialogue.DefaultProjectId,
                        configuredRoot,
                        Logger,
                        playback: resolver.Get<IPlaybackModeManager>(),
                        state: resolver.Get<IStateManager>(),
                        sourceFilesAvailable: hasSourceOverride ||
                            (!string.IsNullOrWhiteSpace(ProjectSettings.GlobalizePath("res://")) &&
                                System.IO.Path.IsPathFullyQualified(resourceRoot)));
                },
                typeof(IYarnProjectVersionStore),
                typeof(IScriptPlayback),
                typeof(IYarnDialogueBackend),
                typeof(IPlaybackModeManager),
                typeof(IStateManager))
            .AddSingleton<IHistoryManager>(
                resolver => new HistoryManager(
                    resolver.Get<IDialoguePlayer>(),
                    resolver.Get<IDialoguePresentation>(),
                    resolver.Get<IAudioManager>(),
                    resolver.Get<IInputManager>(),
                    Configuration!.History!.Capacity,
                    Logger,
                    resolver.Get<ILocaleManager>(),
                    resolver.Get<ILocalizedResourceResolver>()),
                typeof(IDialoguePlayer),
                typeof(IDialoguePresentation),
                typeof(IAudioManager),
                typeof(IInputManager),
                typeof(ILocaleManager),
                typeof(ILocalizedResourceResolver))
            .AddSingleton<IPlayedRegister>(
                resolver => new PlayedRegister(
                    resolver.Get<IDialoguePlayer>(),
                    resolver.Get<IDialoguePresentation>(),
                    logger: Logger),
                typeof(IDialoguePlayer),
                typeof(IDialoguePresentation))
            .AddSingleton<IPlaybackModeManager>(
                resolver => new PlaybackModeManager(
                    resolver.Get<IScriptPlayback>(),
                    resolver.Get<IDialoguePlayer>(),
                    resolver.Get<IAudioManager>(),
                    resolver.Get<IFrameClock>(),
                    resolver.Get<IInputManager>(),
                    resolver.Get<IHistoryManager>(),
                    resolver.Get<IPlayedRegister>(),
                    resolver.Get<IDirectionTransactionCoordinator>(),
                    TimeSpan.FromSeconds(Configuration!.Auto!.ExtraDelaySeconds),
                    Configuration.Auto.WaitForVoice,
                    TimeSpan.FromSeconds(Configuration.Settings!.SkipDelaySeconds),
                    Configuration.Settings.SkipDefaultMode == SkipDefaultMode.ReadOnly
                        ? PlaybackMode.SkipReadOnly
                        : PlaybackMode.SkipAll,
                    Logger),
                typeof(IScriptPlayback),
                typeof(IDialoguePlayer),
                typeof(IAudioManager),
                typeof(IFrameClock),
                typeof(IInputManager),
                typeof(IHistoryManager),
                typeof(IPlayedRegister),
                typeof(IDirectionTransactionCoordinator));

        if (Configuration!.History!.ShowDefaultUi)
        {
            services.AddSingleton<DefaultHistoryOverlay>(
                resolver => new DefaultHistoryOverlay(
                    this,
                    resolver.Get<IHistoryManager>()),
                typeof(IHistoryManager));
        }
    }

    private TService? FindRuntimeService<TService>() where TService : class =>
        Host is { State: RuntimeHostState.Initializing or RuntimeHostState.Initialized or RuntimeHostState.Resetting } host &&
        host.Services.TryGet<TService>(out var service) ? service : null;

    private Task OnLocaleApplied(string locale, LocaleManager manager)
    {
        var backend = FindRuntimeService<IYarnDialogueBackend>();
        var player = FindRuntimeService<IDialoguePlayer>();
        var presentation = FindRuntimeService<IDialoguePresentation>();
        backend?.SetLocale(locale);

        if (player?.State == DialogueSessionState.Running &&
            backend is not null && presentation is not null)
        {
            var fallbackChain = manager.GetDialogueFallbackChain(locale);
            if (player.IsPresentingOptions)
            {
                var reResolvedOpts = backend.ReResolveCurrentOptions(locale, fallbackChain);
                presentation.ReRenderOptions(reResolvedOpts);
            }
            else if (!string.IsNullOrWhiteSpace(player.CurrentLineId))
            {
                var reResolvedLine = backend.ReResolveCurrentLine(locale, fallbackChain);
                if (reResolvedLine is not null)
                {
                    presentation.ReRenderLine(reResolvedLine);
                }
            }
        }

        return Task.CompletedTask;
    }

    private async Task WaitForLocaleSwitchBoundaryAsync(CancellationToken cancellationToken)
    {
        if (FindRuntimeService<IYarnDialogueBackend>() is { } backend)
        {
            await backend.WaitForLocaleSwitchBoundaryAsync(cancellationToken);
        }
    }

    private YarnCommandAdapter CreateYarnCommandAdapter(
        IYarnDialogueBackend backend,
        IServiceResolver services)
    {
        var catalog = commandRegistry.BuildCatalog(services);
        var dispatcher = new RuntimeCommandDispatcher(
            services,
            Logger,
            Configuration?.Dialogue?.AbortOnCommandError ?? true);
        var adapter = new YarnCommandAdapter(
            new DialogueRunnerCommandRegistry(backend.Runner),
            catalog,
            dispatcher);
        backend.ObserveCommandAdapter(adapter);
        return adapter;
    }
}
