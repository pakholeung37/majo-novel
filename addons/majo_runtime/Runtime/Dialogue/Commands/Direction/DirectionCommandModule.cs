#nullable enable

using System;
using Majo.Runtime.Direction.Execution;

namespace Majo.Runtime.Dialogue.Commands;

public sealed class DirectionCommandModule : IRuntimeCommandModule
{
    public void Register(CommandRegistrationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        RegisterConverters(context.Converters);
        RegisterCommands(context.Commands);
    }

    public static void RegisterConverters(ICommandConverterRegistrar converters)
    {
        ArgumentNullException.ThrowIfNull(converters);

        const string providerId = "majo.direction";

        converters.AddDefault<Direction.Actors.ActorShakeAxis>(_ => new EnumCommandValueConverter<Direction.Actors.ActorShakeAxis>(), providerId);
        converters.AddDefault<PositionRef>(_ => PositionRefConverter.Instance, providerId);
        converters.AddDefault<Godot.Vector2>(_ => Vector2Converter.Instance, providerId);
        converters.AddDefault<Direction.Camera.CameraShakeAxis>(_ => CameraShakeAxisConverter.Instance, providerId);
        converters.AddDefault<Direction.Core.DirectionTransitionId>(_ => DirectionTransitionIdConverter.Instance, providerId);
        converters.AddDefault<Direction.Core.CurtainPosition>(_ => new EnumCommandValueConverter<Direction.Core.CurtainPosition>(), providerId);
        converters.AddDefault(
            services => new EasingIdConverter(services.TryGet<Timing.IEasingRegistry>(out var registry) && registry is not null
                ? registry
                : CreateDefaultEasingRegistry()), "timing");
    }

    private static Timing.EasingRegistry CreateDefaultEasingRegistry()
    {
        var registry = new Timing.EasingRegistry();
        Timing.BuiltInEasings.RegisterDefaults(registry);
        return registry;
    }

    private static void RegisterCommands(IRuntimeCommandRegistrar commands)
    {
        const string providerId = "majo.direction";
        const string version = "1.0";

        commands.Add("stage define", services => new StageDefineCommand(
            services.Get<IDirectionBeatRuntime>(),
            services.Get<IDirectionScopeResolver>(),
            services.Get<IDialogueNodeAccessor>()), providerId, version);

        commands.Add("stage clear", services => new StageClearCommand(
            services.Get<IDirectionBeatRuntime>(),
            services.Get<IDirectionScopeResolver>(),
            services.Get<IDialogueNodeAccessor>(),
            services.Get<IDirectionCommandPolicy>()), providerId, version);

        commands.Add("backdrop", services => new BackdropCommand(
            services.Get<IDirectionBeatRuntime>(),
            services.Get<IDirectionScopeResolver>(),
            services.Get<IDialogueNodeAccessor>(),
            services.Get<IDirectionCommandPolicy>(),
            GetEnvironment(services)), providerId, version);

        commands.Add("character", services => new CharacterCommand(
            services.Get<IDirectionBeatRuntime>(),
            services.Get<IDirectionScopeResolver>(),
            services.Get<IDialogueNodeAccessor>(),
            services.Get<IDirectionCommandPolicy>(),
            services.Get<IDirectionTransactionCoordinator>()), providerId, version);

        commands.Add("character hide", services => new CharacterHideCommand(
            services.Get<IDirectionBeatRuntime>(),
            services.Get<IDirectionScopeResolver>(),
            services.Get<IDialogueNodeAccessor>(),
            services.Get<IDirectionCommandPolicy>()), providerId, version);

        commands.Add("object", services => new ObjectCommand(
            services.Get<IDirectionBeatRuntime>(),
            services.Get<IDirectionScopeResolver>(),
            services.Get<IDialogueNodeAccessor>(),
            services.Get<IDirectionCommandPolicy>(),
            services.Get<IDirectionTransactionCoordinator>()), providerId, version);

        commands.Add("character move", services => new CharacterMoveCommand(
            services.Get<IDirectionBeatRuntime>(), services.Get<IDirectionScopeResolver>(),
            services.Get<IDialogueNodeAccessor>(), services.Get<IDirectionCommandPolicy>()), providerId, version);

        commands.Add("object move", services => new ObjectMoveCommand(
            services.Get<IDirectionBeatRuntime>(), services.Get<IDirectionScopeResolver>(),
            services.Get<IDialogueNodeAccessor>(), services.Get<IDirectionCommandPolicy>()), providerId, version);

        commands.Add("object hide", services => new ObjectHideCommand(
            services.Get<IDirectionBeatRuntime>(), services.Get<IDirectionScopeResolver>(),
            services.Get<IDialogueNodeAccessor>(), services.Get<IDirectionCommandPolicy>()), providerId, version);

        commands.Add("character fx", services => new CharacterFxCommand(
            services.Get<IDirectionBeatRuntime>(), services.Get<IDirectionScopeResolver>(),
            services.Get<IDialogueNodeAccessor>(), services.Get<IDirectionCommandPolicy>()), providerId, version);

        commands.Add("object fx", services => new ObjectFxCommand(
            services.Get<IDirectionBeatRuntime>(), services.Get<IDirectionScopeResolver>(),
            services.Get<IDialogueNodeAccessor>(), services.Get<IDirectionCommandPolicy>()), providerId, version);

        commands.Add("backdrop fx", services => new BackdropFxCommand(
            services.Get<IDirectionBeatRuntime>(), services.Get<IDirectionScopeResolver>(),
            services.Get<IDialogueNodeAccessor>(), services.Get<IDirectionCommandPolicy>()), providerId, version);

        commands.Add("view define", services => new ViewDefineCommand(
            services.Get<IDirectionBeatRuntime>(),
            services.Get<IDirectionScopeResolver>(),
            services.Get<IDialogueNodeAccessor>()), providerId, version);

        commands.Add("camera preset define", services => new CameraPresetDefineCommand(
            services.Get<IDirectionScopeResolver>(), services.Get<IDialogueNodeAccessor>(),
            GetEnvironment(services)), providerId, version);

        commands.Add("camera", services => new CameraCommand(
            services.Get<IDirectionBeatRuntime>(),
            services.Get<IDirectionScopeResolver>(),
            services.Get<IDialogueNodeAccessor>(),
            services.Get<IDirectionCommandPolicy>(),
            GetEnvironment(services)), providerId, version);

        commands.Add("camera move", services => new CameraMoveCommand(
            services.Get<IDirectionBeatRuntime>(),
            services.Get<IDirectionScopeResolver>(),
            services.Get<IDialogueNodeAccessor>(),
            services.Get<IDirectionCommandPolicy>(),
            GetEnvironment(services)), providerId, version);

        commands.Add("camera reset", services => new CameraResetCommand(
            services.Get<IDirectionBeatRuntime>(),
            services.Get<IDirectionScopeResolver>(),
            services.Get<IDialogueNodeAccessor>(),
            services.Get<IDirectionCommandPolicy>()), providerId, version);

        commands.Add("camera parallax", services => new CameraParallaxCommand(
            services.Get<IDirectionBeatRuntime>(), services.Get<IDirectionScopeResolver>(),
            services.Get<IDialogueNodeAccessor>(), services.Get<IDirectionCommandPolicy>(),
            GetEnvironment(services)), providerId, version);

        commands.Add("camera idle", services => new CameraIdleCommand(
            services.Get<IDirectionBeatRuntime>(), services.Get<IDirectionScopeResolver>(),
            services.Get<IDialogueNodeAccessor>(), services.Get<IDirectionCommandPolicy>(),
            GetEnvironment(services)), providerId, version);

        commands.Add("camera fx", services => new CameraFxCommand(
            services.Get<IDirectionBeatRuntime>(),
            services.Get<IDirectionScopeResolver>(),
            services.Get<IDialogueNodeAccessor>()), providerId, version);

        commands.Add("shot use", services => new ShotUseCommand(
            services.Get<IDirectionBeatRuntime>(),
            services.Get<IDirectionScopeResolver>(),
            services.Get<IDialogueNodeAccessor>(),
            services.Get<IDirectionCommandPolicy>()), providerId, version);

        commands.Add("screen show", services => new ScreenShowCommand(
            services.Get<IDirectionBeatRuntime>(),
            services.Get<IDirectionScopeResolver>(),
            services.Get<IDialogueNodeAccessor>(),
            services.Get<IDirectionCommandPolicy>()), providerId, version);

        commands.Add("screen split", services => new ScreenSplitCommand(
            services.Get<IDirectionBeatRuntime>(),
            services.Get<IDirectionScopeResolver>(),
            services.Get<IDialogueNodeAccessor>(),
            services.Get<IDirectionCommandPolicy>()), providerId, version);

        commands.Add("screen pip", services => new ScreenPipCommand(
            services.Get<IDirectionBeatRuntime>(),
            services.Get<IDirectionScopeResolver>(),
            services.Get<IDialogueNodeAccessor>(),
            services.Get<IDirectionCommandPolicy>()), providerId, version);

        commands.Add("import", services => new DirectionImportCommand(
            services.Get<IDirectionScopeResolver>(),
            services.Get<IDialogueNodeAccessor>()), providerId, version);

        commands.Add("mark", services => new DirectionMarkCommand(
            services.Get<IDirectionScopeResolver>(),
            services.Get<IDialogueNodeAccessor>()), providerId, version);

        commands.Add("curtain", services => new CurtainCommand(
            services.Get<IDirectionBeatRuntime>(),
            services.Get<IDialogueNodeAccessor>()), providerId, version);

        commands.Add("beat begin", services => new BeatBeginCommand(
            services.Get<IDirectionBeatRuntime>(),
            services.Get<IDialogueNodeAccessor>()), providerId, version);

        commands.Add("beat end", services => new BeatEndCommand(
            services.Get<IDirectionBeatRuntime>()), providerId, version);

        commands.Add("beat join", services => new BeatJoinCommand(
            services.Get<IDirectionBeatRuntime>()), providerId, version);

        commands.Add("beat finish", services => new BeatFinishCommand(
            services.Get<IDirectionBeatRuntime>()), providerId, version);

        commands.Add("beat abort", services => new BeatAbortCommand(
            services.Get<IDirectionBeatRuntime>()), providerId, version);
    }

    private static Direction.Camera.CameraEnvironmentPresetCatalog GetEnvironment(Majo.Runtime.Core.IServiceResolver services) =>
        services.TryGet<Direction.Camera.CameraEnvironmentPresetCatalog>(out var catalog) && catalog is not null
            ? catalog : new Direction.Camera.CameraEnvironmentPresetCatalog();
}
