#nullable enable

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Actors;
using Majo.Runtime.Audio;
using Majo.Runtime.Auto;
using Majo.Runtime.Configuration;
using Majo.Runtime.Core;
using Majo.Runtime.Dialogue;
using Majo.Runtime.Dialogue.Commands;
using Majo.Runtime.Dialogue.Commands.Audio;
using Majo.Runtime.Dialogue.Presentation;
using Majo.Runtime.Dialogue.Yarn;
using Majo.Runtime.Dialogue.Yarn.HotReload;
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
using Majo.Runtime.UI.History;
using Majo.YarnBridge;

namespace Majo.Runtime;

public partial class MajoRuntimeBootstrap : Node
{
    private void RegisterDialogueServices(RuntimeServiceRegistry services)
    {
        var fontCatalog = new FontCatalog();
        if (Configuration?.Localization is not null)
        {
            fontCatalog.FontScale = Configuration.Localization.FontScale;
            fontCatalog.LineSpacing = Configuration.Localization.LineSpacing;
        }

        services
            .AddSingleton<IFontCatalog>(_ => fontCatalog)
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
                        fontCatalog,
                        isBusyCheck: () => yarnCommandAdapter?.HasActiveCommands == true,
                        onLocaleApplied: locale => OnLocaleApplied(locale, manager!),
                        applyEngineLocale: TranslationServer.SetLocale,
                        logger: Logger,
                        waitForSafeBoundary: WaitForLocaleSwitchBoundaryAsync,
                        defaultVoiceLocale: defaultVoiceLoc);
                    ConfigureLocales?.Invoke(manager);
                    return manager;
                });

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

                    yarnDialogueBackend = new YarnDialogueBackend(
                        this,
                        resolver.Get<IResourceManager>(),
                        Configuration!.Resources!.ScriptsPath,
                        presenters,
                        resolver.Get<ILocaleManager>(),
                        ResolveSpeakerName,
                        Logger,
                        Configuration.Script!.RequireExactProgramFingerprint,
                        resolver.Get<IYarnProjectVersionStore>(),
                        resolver.Get<IDirectionScopeResolver>());
                    return yarnDialogueBackend;
                },
                typeof(IResourceManager),
                typeof(IAudioManager),
                typeof(ILocaleManager),
                typeof(ILocalizedResourceResolver),
                typeof(IYarnProjectVersionStore),
                typeof(IDirectionScopeResolver))
            .AddSingleton<IDialoguePresentation>(
                resolver =>
                {
                    dialoguePresentation = new YarnDefaultDialoguePresentation(
                        resolver.Get<IYarnDialogueBackend>(),
                        resolver.Get<IInputManager>(),
                        resolver.Get<IFrameClock>(),
                        Configuration!.Presentation!,
                        markupHandlerRegistry.Handlers,
                        resolver.Get<ILocaleManager>(),
                        resolver.Get<IFontCatalog>(),
                        ResolveSpeakerName);
                    return dialoguePresentation;
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
                _ => new DelegateDialogueNodeAccessor(() => dialoguePlayer?.CurrentNode))
            .AddSingleton<ISaveProductAccessor>(
                _ => new DelegateSaveProductAccessor(() => saveProductManager))
            .AddSingleton<IYarnCommandAdapter>(
                _ => yarnCommandAdapter)
            .AddSingleton<IDialoguePlayer>(
                resolver =>
                {
                    dialoguePlayer = new DialoguePlayer(
                        resolver.Get<IYarnDialogueBackend>(),
                        Configuration!.Dialogue!.DefaultProjectId,
                        Configuration.Dialogue.DefaultStartNode,
                        Logger);
                    return dialoguePlayer;
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
                    var configuredRoot = string.IsNullOrWhiteSpace(hotReload.SourceRootOverride)
                        ? System.IO.Path.Combine(
                            ProjectSettings.GlobalizePath(Configuration.Resources!.RootPath),
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
                        playback: resolver.Get<IPlaybackModeManager>());
                },
                typeof(IYarnProjectVersionStore),
                typeof(IScriptPlayback),
                typeof(IYarnDialogueBackend),
                typeof(IPlaybackModeManager))
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

    private Task OnLocaleApplied(string locale, LocaleManager manager)
    {
        if (yarnDialogueBackend is not null)
        {
            yarnDialogueBackend.SetLocale(locale);
        }

        if (dialoguePlayer?.State == DialogueSessionState.Running &&
            yarnDialogueBackend is not null &&
            dialoguePresentation is not null)
        {
            var fallbackChain = manager.GetDialogueFallbackChain(locale);
            if (dialoguePlayer.IsPresentingOptions)
            {
                var reResolvedOpts = yarnDialogueBackend.ReResolveCurrentOptions(locale, fallbackChain);
                dialoguePresentation.ReRenderOptions(reResolvedOpts);
            }
            else if (!string.IsNullOrWhiteSpace(dialoguePlayer.CurrentLineId))
            {
                var reResolvedLine = yarnDialogueBackend.ReResolveCurrentLine(locale, fallbackChain);
                if (reResolvedLine is not null)
                {
                    dialoguePresentation.ReRenderLine(reResolvedLine);
                }
            }
        }

        return Task.CompletedTask;
    }

    private async Task WaitForLocaleSwitchBoundaryAsync(CancellationToken cancellationToken)
    {
        if (yarnDialogueBackend is not null)
        {
            await yarnDialogueBackend.WaitForLocaleSwitchBoundaryAsync(cancellationToken);
        }
    }

    private YarnCommandAdapter CreateYarnCommandAdapter(
        IYarnDialogueBackend backend,
        IServiceResolver services)
    {
        var environment = new CommandBindingEnvironment(commandConverters, commandParameterGroups);
        var catalog = commandRegistry.BuildCatalog(services, environment);
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
