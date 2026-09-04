#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Majo.Runtime.Actors;
using Majo.Runtime.Audio;
using Majo.Runtime.Auto;
using Majo.Runtime.Core;
using Majo.Runtime.Dialogue;
using Majo.Runtime.Dialogue.Presentation;
using Majo.Runtime.Dialogue.Yarn;
using Majo.Runtime.Direction.Execution;
using Majo.Runtime.Direction.Rendering;
using Majo.Runtime.History;
using Majo.Runtime.Input;
using Majo.Runtime.Localization;
using Majo.Runtime.Operations;
using Majo.Runtime.Played;
using Majo.Runtime.Save;
using Majo.Runtime.Save.Godot;
using Majo.Runtime.Script;
using Majo.Runtime.Settings;
using Majo.Runtime.Stage;
using Majo.Runtime.State;
using Majo.Runtime.Travel;
using Majo.Runtime.UI.SaveLoad;
using Majo.Runtime.UI.Settings;
using Majo.Runtime.UI.Travel;

namespace Majo.Runtime;

public partial class MajoRuntimeBootstrap : Node
{
    private void RegisterStateAndSaveServices(RuntimeServiceRegistry services)
    {
        if (!Configuration!.State!.Enabled)
        {
            return;
        }

        var stateMigrations = new StateMigrationRegistry();
        ConfigureStateMigrations?.Invoke(stateMigrations);
        var customStateContributors = new StateContributorRegistry();
        ConfigureStateContributors?.Invoke(customStateContributors);
        var customContributors = customStateContributors.Build(
        [
            BuiltInStateKeys.DialogueVariables,
            BuiltInStateKeys.Actors,
            BuiltInStateKeys.AudioBgm,
            BuiltInStateKeys.ScriptPlayback,
            BuiltInStateKeys.SessionHistory,
            PlayedRegister.StateKey,
            "runtime.settings",
        ]);

        var settingsContributors = customContributors.Where(value =>
            value.Scope == StateScope.Settings).ToArray();
        var globalCustomContributors = customContributors.Where(value =>
            value.Scope == StateScope.Global).ToArray();
        var gameStateContributors = customContributors.Where(value =>
            value.Scope is StateScope.Game or StateScope.Session).ToArray();

        services.AddSingleton<IStateSerializer>(
            _ => new CanonicalJsonStateSerializer(stateMigrations));
        services.AddSingleton<IStateStorage>(
            _ => new GodotUserStateStorage(Configuration.State.StorageNamespace));

        if (Configuration.Settings?.Enabled == true)
        {
            services.AddSingleton<ISettingsManager>(
                resolver => new SettingsManager(
                    CreateSettingsDefaults(),
                    settingsContributors,
                    [new RuntimeSettingsProjection(
                        resolver.Get<IAudioManager>(),
                        resolver.Get<IPlaybackModeManager>(),
                        resolver.Get<IDialoguePresentation>(),
                        resolver.Get<ILocaleManager>(),
                        resolver.Get<IStageAccessibility>())],
                    resolver.Get<IStateSerializer>(),
                    resolver.Get<IStateStorage>(),
                    stateMigrations,
                    MajoRuntimeMetadata.PackageVersion,
                    Configuration.State.GameBuildId,
                    Configuration.State.StorageNamespace,
                    TimeSpan.FromSeconds(Configuration.Settings.FlushDebounceSeconds),
                    Logger,
                    resolver.Get<ILocaleManager>()),
                typeof(IStateSerializer),
                typeof(IStateStorage),
                typeof(IAudioManager),
                typeof(IPlaybackModeManager),
                typeof(IDialoguePresentation),
                typeof(ILocaleManager),
                typeof(IStageAccessibility));
        }

        if (Configuration.GlobalState?.Enabled == true)
        {
            services.AddSingleton<IGlobalStateManager>(
                resolver =>
                {
                    var globalContributors = new IStateContributor[globalCustomContributors.Length + 1];
                    globalContributors[0] = (IStateContributor)resolver.Get<IPlayedRegister>();
                    globalCustomContributors.CopyTo(globalContributors, 1);

                    return new GlobalStateManager(
                        globalContributors,
                        resolver.Get<IStateSerializer>(),
                        resolver.Get<IStateStorage>(),
                        stateMigrations,
                        MajoRuntimeMetadata.PackageVersion,
                        Configuration.State.GameBuildId,
                        Configuration.State.StorageNamespace,
                        TimeSpan.FromSeconds(Configuration.GlobalState.FlushDebounceSeconds),
                        Configuration.GlobalState.StrictRecovery,
                        Logger);
                },
                typeof(IStateSerializer),
                typeof(IStateStorage),
                typeof(IPlayedRegister));
        }

        if (Configuration.Settings?.Enabled == true &&
            Configuration.Settings.ShowDefaultUi)
        {
            services.AddSingleton<ISettingsOverlay>(
                resolver => new DefaultSettingsOverlay(
                    this,
                    resolver.Get<ISettingsManager>(),
                    resolver.Get<IInputManager>(),
                    resolver.Get<ILocaleManager>(),
                    Configuration.Settings.DefaultUiScene),
                typeof(ISettingsManager),
                typeof(IInputManager),
                typeof(ILocaleManager));
        }

        services.AddSingleton<IStateEligibility>(
            resolver => new StateEligibility(
                resolver.Get<IScriptPlayback>(),
                resolver.Get<IRuntimeActivityObserver>(),
                resolver.Get<IInputManager>(),
                resolver.Get<IYarnCommandAdapter>()),
            typeof(IScriptPlayback),
            typeof(IRuntimeActivityObserver),
            typeof(IInputManager),
            typeof(IYarnCommandAdapter));

        services.AddSingleton<IStateManager>(
            resolver => CreateStateManager(
                resolver.Get<IDialogueVariables>(),
                resolver.Get<IActorManager>(),
                resolver.Get<IAudioManager>(),
                resolver.Get<IHistoryManager>(),
                resolver.Get<IScriptPlayback>(),
                resolver.Get<IInputManager>(),
                resolver.Get<IRuntimeRestoreBoundary>(),
                resolver.Get<IStateEligibility>(),
                resolver.Get<IDirectionTransactionCoordinator>(),
                stateMigrations,
                gameStateContributors),
            typeof(IDialogueVariables),
            typeof(IActorManager),
            typeof(IAudioManager),
            typeof(IHistoryManager),
            typeof(IScriptPlayback),
            typeof(IInputManager),
            typeof(IRuntimeRestoreBoundary),
            typeof(IStateEligibility),
            typeof(IDirectionTransactionCoordinator));

        services.AddSingleton<IGameSessionCoordinator>(
            resolver => new GameSessionCoordinator(
                resolver.Get<IRuntimeSessionController>(),
                resolver.Get<IStateManager>(),
                resolver.Get<IHistoryManager>(),
                resolver.Get<IPlaybackModeManager>(),
                resolver.Get<IStateEligibility>()),
            typeof(IRuntimeSessionController),
            typeof(IStateManager),
            typeof(IHistoryManager),
            typeof(IPlaybackModeManager),
            typeof(IStateEligibility));

        var travel = Configuration.State.DialogueTravel;
        if (travel.Enabled &&
            (OS.HasFeature("editor") ||
             OS.IsDebugBuild() && travel.EnableInExportedDebugBuild))
        {
            services.AddSingleton<IDialogueTravelManager>(
                resolver => new DialogueTravelManager(
                    resolver.Get<IStateManager>(),
                    resolver.Get<IGameSessionCoordinator>(),
                    resolver.Get<IScriptPlayback>(),
                    resolver.Get<IDialoguePlayer>(),
                    resolver.Get<IPlaybackModeManager>(),
                    travel.Capacity,
                    resolver.Get<IInputManager>(),
                    Logger),
                typeof(IStateManager),
                typeof(IGameSessionCoordinator),
                typeof(IScriptPlayback),
                typeof(IDialoguePlayer),
                typeof(IPlaybackModeManager),
                typeof(IInputManager));

            if (travel.ShowDefaultUi)
            {
                services.AddSingleton<DefaultDialogueTravelOverlay>(
                    resolver => new DefaultDialogueTravelOverlay(
                        this,
                        resolver.Get<IDialogueTravelManager>(),
                        resolver.Get<IInputManager>(),
                        resolver.Get<IPlaybackModeManager>()),
                    typeof(IDialogueTravelManager),
                    typeof(IInputManager),
                    typeof(IPlaybackModeManager));
            }
        }

        if (Configuration.Save?.Enabled == true)
        {
            services.AddSingleton<ISaveThumbnailProvider>(
                _ => Configuration.Save.CaptureThumbnails
                    ? new GodotSaveThumbnailProvider(
                        this,
                        Configuration.State.StorageNamespace,
                        Configuration.Save.ThumbnailWidth,
                        Configuration.Save.ThumbnailHeight)
                    : NullSaveThumbnailProvider.Instance);

            services.AddSingleton<ISaveSlotManager>(
                resolver => new SaveSlotManager(
                    resolver.Get<IStateManager>(),
                    resolver.Get<IStateSerializer>(),
                    resolver.Get<IStateStorage>(),
                    resolver.Get<IStateEligibility>(),
                    resolver.Get<IGameSessionCoordinator>(),
                    resolver.Get<IHistoryManager>(),
                    new Dictionary<SaveSlotKind, int>
                    {
                        [SaveSlotKind.Manual] = Configuration.Save.ManualSlotCapacity,
                        [SaveSlotKind.Quick] = Configuration.Save.QuickSlotCapacity,
                        [SaveSlotKind.Auto] = Configuration.Save.AutoSlotCapacity,
                    },
                    MajoRuntimeMetadata.PackageVersion,
                    Configuration.State.GameBuildId,
                    Configuration.State.StorageNamespace,
                    resolver.Get<ISaveThumbnailProvider>(),
                    Logger),
                typeof(IStateManager),
                typeof(IStateSerializer),
                typeof(IStateStorage),
                typeof(IStateEligibility),
                typeof(IGameSessionCoordinator),
                typeof(IHistoryManager),
                typeof(ISaveThumbnailProvider));

            services.AddSingleton<ISaveProductManager>(
                resolver => new SaveProductManager(
                    resolver.Get<ISaveSlotManager>(),
                    resolver.Get<IDialoguePlayer>(),
                    resolver.Get<IDialoguePresentation>(),
                    resolver.Get<IScriptPlayback>(),
                    resolver.Get<IInputManager>(),
                    Configuration.Save.AutoSaveNodeIds,
                    Configuration.Save.AutoSaveLineIds,
                    Configuration.Save.AutoSaveMetadata,
                    Logger),
                typeof(ISaveSlotManager),
                typeof(IDialoguePlayer),
                typeof(IDialoguePresentation),
                typeof(IScriptPlayback),
                typeof(IInputManager));

            if (Configuration.Save.ShowDefaultUi)
            {
                services.AddSingleton<ISaveLoadOverlay>(
                    resolver => new DefaultSaveLoadOverlay(
                        this,
                        resolver.Get<ISaveSlotManager>(),
                        resolver.Get<IInputManager>(),
                        Configuration.Save.DefaultUiScene ??
                            GD.Load<PackedScene>(
                                "res://addons/majo_runtime/Runtime/UI/SaveLoad/DefaultSaveLoadOverlay.tscn"),
                        resolver.Get<IFrameClock>(),
                        resolver.Get<IPlaybackModeManager>()),
                    typeof(ISaveSlotManager),
                    typeof(IInputManager),
                    typeof(IFrameClock),
                    typeof(IPlaybackModeManager));
            }
        }
    }

    private StateManager CreateStateManager(
        IDialogueVariables variables,
        IActorManager actors,
        IAudioManager audio,
        IHistoryManager history,
        IScriptPlayback scriptPlayback,
        IInputManager input,
        IRuntimeRestoreBoundary restoreBoundary,
        IStateEligibility eligibility,
        IDirectionTransactionCoordinator direction,
        StateMigrationRegistry migrations,
        IReadOnlyList<IStateContributor> customContributors)
    {
        var contributors = new StateContributorRegistry()
            .Register(new DialogueVariablesStateContributor(variables))
            .Register(new ActorStateContributor(actors))
            .Register(new AudioStateContributor(audio))
            .Register(new SessionHistoryStateContributor(history))
            .Register(new DirectionStateContributor(direction));
        foreach (var contributor in customContributors)
        {
            contributors.Register(contributor);
        }
        contributors.Register(new ScriptStateContributor(scriptPlayback));
        return new StateManager(
            contributors.Build(),
            input,
            restoreBoundary,
            eligibility,
            migrations,
            Logger);
    }

    private RuntimeSettings CreateSettingsDefaults()
    {
        var audio = Configuration!.Audio!;
        var presentation = Configuration.Presentation!;
        var auto = Configuration.Auto!;
        var settings = Configuration.Settings!;
        return new RuntimeSettings(
            audio.MasterVolume,
            audio.BgmVolume,
            audio.SfxVolume,
            audio.VoiceVolume,
            audio.MasterMuted,
            audio.BgmMuted,
            audio.SfxMuted,
            audio.VoiceMuted,
            presentation.LettersPerSecond,
            auto.ExtraDelaySeconds,
            auto.WaitForVoice,
            settings.SkipDefaultMode,
            settings.SkipDelaySeconds,
            settings.ShowSpeakerName,
            settings.ShowTextBox,
            settings.DialogueLocale,
            settings.VoiceLocale,
            settings.FontScale,
            settings.LineSpacing,
            settings.ReduceMotion);
    }
}
