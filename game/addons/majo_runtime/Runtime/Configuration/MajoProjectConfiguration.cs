#nullable enable

using System;
using System.Collections.Generic;
using Godot;
using Majo.Runtime.Actors;
using Majo.Runtime.Core;
using Majo.Runtime.Resources;
using RuntimeResourcePath = Majo.Runtime.Resources.ResourcePath;

namespace Majo.Runtime.Configuration;

[GlobalClass]
public partial class MajoProjectConfiguration : Resource
{
    [Export]
    public RuntimeConfiguration? Runtime { get; set; }

    [Export]
    public OperationConfiguration? Operations { get; set; }

    [Export]
    public ResourceConfiguration? Resources { get; set; }

    [Export]
    public ActorConfiguration? Actors { get; set; }

    [Export]
    public DialogueConfiguration? Dialogue { get; set; }

    [Export]
    public InputConfiguration? Input { get; set; }

    [Export]
    public DialoguePresentationConfiguration? Presentation { get; set; }

    [Export]
    public AudioConfiguration? Audio { get; set; }

    [Export]
    public ScriptConfiguration? Script { get; set; }

    [Export]
    public StateConfiguration? State { get; set; }

    [Export]
    public AutoConfiguration? Auto { get; set; }

    [Export]
    public HistoryConfiguration? History { get; set; }

    [Export]
    public DiagnosticsConfiguration? Diagnostics { get; set; }

    public static MajoProjectConfiguration CreateDefault() => new()
    {
        Runtime = new RuntimeConfiguration(),
        Operations = new OperationConfiguration(),
        Resources = new ResourceConfiguration(),
        Actors = new ActorConfiguration(),
        Dialogue = new DialogueConfiguration(),
        Input = new InputConfiguration(),
        Presentation = new DialoguePresentationConfiguration(),
        Audio = new AudioConfiguration(),
        Script = new ScriptConfiguration(),
        State = new StateConfiguration(),
        Auto = new AutoConfiguration(),
        History = new HistoryConfiguration(),
        Diagnostics = new DiagnosticsConfiguration(),
    };

    public ConfigurationValidationResult Validate(Func<string, bool>? resourceExists = null)
    {
        resourceExists ??= path => ResourceLoader.Exists(path);
        var issues = new List<ConfigurationIssue>();

        ValidateRuntime(issues);
        ValidateOperations(issues);
        ValidateResources(issues);
        ValidateActors(issues, resourceExists);
        ValidateDialogue(issues);
        ValidateInput(issues);
        ValidatePresentation(issues);
        ValidateAudio(issues);
        ValidateScript(issues);
        ValidateState(issues);
        ValidateAuto(issues);
        ValidateHistory(issues);
        ValidateDiagnostics(issues);

        return new ConfigurationValidationResult(issues);
    }

    private void ValidateScript(ICollection<ConfigurationIssue> issues)
    {
        if (Script is null)
        {
            issues.Add(new ConfigurationIssue(nameof(Script), "Configuration is required."));
            return;
        }

        if (!Script.RequireExactProgramFingerprint)
        {
            issues.Add(new ConfigurationIssue(
                $"{nameof(Script)}.{nameof(Script.RequireExactProgramFingerprint)}",
                "Exact program fingerprint validation is required by checkpoint format v1."));
        }
    }

    private void ValidateState(ICollection<ConfigurationIssue> issues)
    {
        if (State is null)
        {
            issues.Add(new ConfigurationIssue(nameof(State), "Configuration is required."));
        }
    }

    private void ValidateAuto(ICollection<ConfigurationIssue> issues)
    {
        if (Auto is null)
        {
            issues.Add(new ConfigurationIssue(nameof(Auto), "Configuration is required."));
            return;
        }

        if (!double.IsFinite(Auto.ExtraDelaySeconds) || Auto.ExtraDelaySeconds < 0)
        {
            issues.Add(new ConfigurationIssue(
                $"{nameof(Auto)}.{nameof(Auto.ExtraDelaySeconds)}",
                "Extra delay must be finite and non-negative."));
        }
    }

    private void ValidateHistory(ICollection<ConfigurationIssue> issues)
    {
        if (History is null)
        {
            issues.Add(new ConfigurationIssue(nameof(History), "Configuration is required."));
            return;
        }

        if (History.Capacity <= 0)
        {
            issues.Add(new ConfigurationIssue(
                $"{nameof(History)}.{nameof(History.Capacity)}",
                "Capacity must be greater than zero."));
        }
    }

    private void ValidateResources(ICollection<ConfigurationIssue> issues)
    {
        if (Resources is null)
        {
            issues.Add(new ConfigurationIssue(nameof(Resources), "Configuration is required."));
            return;
        }

        if (string.IsNullOrWhiteSpace(Resources.RootPath) ||
            !Resources.RootPath.StartsWith("res://", StringComparison.Ordinal))
        {
            issues.Add(new ConfigurationIssue(
                $"{nameof(Resources)}.{nameof(Resources.RootPath)}",
                "Root path must be a Godot project path starting with 'res://'."));
        }

        ValidateResourceKeyPrefix(Resources.CharactersPath, nameof(Resources.CharactersPath), issues);
        ValidateResourceKeyPrefix(Resources.BackgroundsPath, nameof(Resources.BackgroundsPath), issues);
        ValidateResourceKeyPrefix(Resources.ScriptsPath, nameof(Resources.ScriptsPath), issues);
    }

    private static void ValidateResourceKeyPrefix(
        string value,
        string property,
        ICollection<ConfigurationIssue> issues)
    {
        try
        {
            _ = new ResourceKey(value);
        }
        catch (ArgumentException exception)
        {
            issues.Add(new ConfigurationIssue(
                $"{nameof(Resources)}.{property}", exception.Message));
        }
    }

    private void ValidateRuntime(ICollection<ConfigurationIssue> issues)
    {
        if (Runtime is null)
        {
            issues.Add(new ConfigurationIssue(nameof(Runtime), "Configuration is required."));
            return;
        }

        if (!double.IsFinite(Runtime.LifecycleTimeoutSeconds) ||
            Runtime.LifecycleTimeoutSeconds <= 0)
        {
            issues.Add(new ConfigurationIssue(
                $"{nameof(Runtime)}.{nameof(Runtime.LifecycleTimeoutSeconds)}",
                "Value must be finite and greater than zero."));
        }
    }

    private void ValidateOperations(ICollection<ConfigurationIssue> issues)
    {
        if (Operations is null)
        {
            issues.Add(new ConfigurationIssue(nameof(Operations), "Configuration is required."));
            return;
        }

        if (!double.IsFinite(Operations.DefaultAnimationDurationSeconds) ||
            Operations.DefaultAnimationDurationSeconds <= 0)
        {
            issues.Add(new ConfigurationIssue(
                $"{nameof(Operations)}.{nameof(Operations.DefaultAnimationDurationSeconds)}",
                "Value must be finite and greater than zero."));
        }
    }

    private void ValidateActors(
        ICollection<ConfigurationIssue> issues,
        Func<string, bool> resourceExists)
    {
        if (Actors is null)
        {
            issues.Add(new ConfigurationIssue(nameof(Actors), "Configuration is required."));
            return;
        }

        var actorIds = new HashSet<string>(StringComparer.Ordinal);
        ValidatePosition(Actors.LeftPosition, nameof(Actors.LeftPosition), issues);
        ValidatePosition(Actors.CenterPosition, nameof(Actors.CenterPosition), issues);
        ValidatePosition(Actors.RightPosition, nameof(Actors.RightPosition), issues);
        for (var index = 0; index < Actors.Actors.Count; index++)
        {
            var actor = Actors.Actors[index];
            var path = $"{nameof(Actors)}.{nameof(ActorConfiguration.Actors)}[{index}]";
            if (actor is null)
            {
                issues.Add(new ConfigurationIssue(path, "Actor definition is required."));
                continue;
            }

            if (string.IsNullOrWhiteSpace(actor.Id))
            {
                issues.Add(new ConfigurationIssue($"{path}.{nameof(actor.Id)}", "ID is required."));
            }
            else if (!actorIds.Add(actor.Id))
            {
                issues.Add(new ConfigurationIssue(
                    $"{path}.{nameof(actor.Id)}",
                    $"Actor ID '{actor.Id}' is duplicated."));
            }

            if (string.IsNullOrWhiteSpace(actor.FactoryId))
            {
                issues.Add(new ConfigurationIssue(
                    $"{path}.{nameof(actor.FactoryId)}",
                    "Factory ID is required."));
            }

            var builtInTextureFactory = actor.FactoryId is ActorFactoryIds.Character or
                ActorFactoryIds.Background;
            if (builtInTextureFactory && actor.FactoryConfiguration is not null)
            {
                issues.Add(new ConfigurationIssue(
                    $"{path}.{nameof(actor.FactoryConfiguration)}",
                    $"Built-in factory '{actor.FactoryId}' uses the texture appearance fields."));
            }

            var usesTextureDefinition = builtInTextureFactory ||
                (actor.FactoryConfiguration is null &&
                    (!string.IsNullOrWhiteSpace(actor.ResourcePathPrefix) ||
                        actor.Appearances.Count > 0));
            if (!usesTextureDefinition)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(actor.DefaultAppearance))
            {
                issues.Add(new ConfigurationIssue(
                    $"{path}.{nameof(actor.DefaultAppearance)}",
                    "Default appearance is required."));
            }

            var appearanceIds = new HashSet<string>(StringComparer.Ordinal);
            if (!string.IsNullOrWhiteSpace(actor.ResourcePathPrefix))
            {
                try
                {
                    _ = new ResourceKey(actor.ResourcePathPrefix);
                }
                catch (ArgumentException exception)
                {
                    issues.Add(new ConfigurationIssue(
                        $"{path}.{nameof(actor.ResourcePathPrefix)}", exception.Message));
                }
            }

            for (var appearanceIndex = 0;
                appearanceIndex < actor.Appearances.Count;
                appearanceIndex++)
            {
                var appearance = actor.Appearances[appearanceIndex];
                var appearancePath =
                    $"{path}.{nameof(actor.Appearances)}[{appearanceIndex}]";
                if (appearance is null)
                {
                    issues.Add(new ConfigurationIssue(
                        appearancePath, "Appearance definition is required."));
                    continue;
                }

                if (string.IsNullOrWhiteSpace(appearance.Id))
                {
                    issues.Add(new ConfigurationIssue(
                        $"{appearancePath}.{nameof(appearance.Id)}",
                        "Appearance ID is required."));
                }
                else if (!appearanceIds.Add(appearance.Id))
                {
                    issues.Add(new ConfigurationIssue(
                        $"{appearancePath}.{nameof(appearance.Id)}",
                        $"Appearance ID '{appearance.Id}' is duplicated."));
                }

                if (string.IsNullOrWhiteSpace(appearance.TexturePath))
                {
                    issues.Add(new ConfigurationIssue(
                        $"{appearancePath}.{nameof(appearance.TexturePath)}",
                        "Texture path is required."));
                }
                else
                {
                    var resolvedPath = RuntimeResourcePath.IsRooted(appearance.TexturePath)
                        ? appearance.TexturePath
                        : RuntimeResourcePath.Combine(
                            Resources?.RootPath ?? ResourceConfiguration.DefaultRootPath,
                            appearance.TexturePath);
                    if (Resources?.FailOnMissingAssets == true &&
                        !resourceExists(resolvedPath))
                    {
                        issues.Add(new ConfigurationIssue(
                            $"{appearancePath}.{nameof(appearance.TexturePath)}",
                            $"Resource '{resolvedPath}' does not exist."));
                    }
                }
            }

        }
    }

    private static void ValidatePosition(
        Vector2 position,
        string property,
        ICollection<ConfigurationIssue> issues)
    {
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y))
        {
            issues.Add(new ConfigurationIssue(
                $"{nameof(Actors)}.{property}",
                "Position components must be finite."));
        }
    }

    private void ValidateDialogue(ICollection<ConfigurationIssue> issues)
    {
        if (Dialogue is null)
        {
            issues.Add(new ConfigurationIssue(nameof(Dialogue), "Configuration is required."));
            return;
        }

        if (!string.Equals(
            Dialogue.RequiredYarnPluginVersion,
            MajoRuntimeMetadata.RequiredYarnPluginVersion,
            StringComparison.Ordinal))
        {
            issues.Add(new ConfigurationIssue(
                $"{nameof(Dialogue)}.{nameof(Dialogue.RequiredYarnPluginVersion)}",
                $"Version must be {MajoRuntimeMetadata.RequiredYarnPluginVersion}."));
        }

        if (!string.Equals(
            Dialogue.RequiredYarnRuntimeVersion,
            MajoRuntimeMetadata.RequiredYarnRuntimeVersion,
            StringComparison.Ordinal))
        {
            issues.Add(new ConfigurationIssue(
                $"{nameof(Dialogue)}.{nameof(Dialogue.RequiredYarnRuntimeVersion)}",
                $"Version must be {MajoRuntimeMetadata.RequiredYarnRuntimeVersion}."));
        }

        if (string.IsNullOrWhiteSpace(Dialogue.DefaultProjectId))
        {
            issues.Add(new ConfigurationIssue(
                $"{nameof(Dialogue)}.{nameof(Dialogue.DefaultProjectId)}",
                "Default project ID is required."));
        }

        if (string.IsNullOrWhiteSpace(Dialogue.DefaultStartNode))
        {
            issues.Add(new ConfigurationIssue(
                $"{nameof(Dialogue)}.{nameof(Dialogue.DefaultStartNode)}",
                "Default start node is required."));
        }
    }

    private void ValidateInput(ICollection<ConfigurationIssue> issues)
    {
        if (Input is null)
        {
            issues.Add(new ConfigurationIssue(nameof(Input), "Configuration is required."));
        }
    }

    private void ValidatePresentation(ICollection<ConfigurationIssue> issues)
    {
        if (Presentation is null)
        {
            issues.Add(new ConfigurationIssue(
                nameof(Presentation),
                "Configuration is required."));
            return;
        }

        if (Presentation.FontSize <= 0)
        {
            issues.Add(new ConfigurationIssue(
                $"{nameof(Presentation)}.{nameof(Presentation.FontSize)}",
                "Value must be greater than zero."));
        }

        if (Presentation.LettersPerSecond <= 0)
        {
            issues.Add(new ConfigurationIssue(
                $"{nameof(Presentation)}.{nameof(Presentation.LettersPerSecond)}",
                "Value must be greater than zero."));
        }

        ValidateNonNegativeDuration(
            Presentation.FadeUpDurationSeconds,
            nameof(Presentation.FadeUpDurationSeconds),
            issues);
        ValidateNonNegativeDuration(
            Presentation.FadeDownDurationSeconds,
            nameof(Presentation.FadeDownDurationSeconds),
            issues);
    }

    private static void ValidateNonNegativeDuration(
        double value,
        string property,
        ICollection<ConfigurationIssue> issues)
    {
        if (!double.IsFinite(value) || value < 0)
        {
            issues.Add(new ConfigurationIssue(
                $"{nameof(Presentation)}.{property}",
                "Value must be finite and non-negative."));
        }
    }

    private void ValidateAudio(ICollection<ConfigurationIssue> issues)
    {
        if (Audio is null)
        {
            issues.Add(new ConfigurationIssue(nameof(Audio), "Configuration is required."));
            return;
        }

        ValidateVolume(Audio.MasterVolume, nameof(Audio.MasterVolume), issues);
        ValidateVolume(Audio.BgmVolume, nameof(Audio.BgmVolume), issues);
        ValidateVolume(Audio.SfxVolume, nameof(Audio.SfxVolume), issues);
        ValidateVolume(Audio.VoiceVolume, nameof(Audio.VoiceVolume), issues);
        if (!double.IsFinite(Audio.DefaultFadeDurationSeconds) ||
            Audio.DefaultFadeDurationSeconds < 0)
        {
            issues.Add(new ConfigurationIssue(
                $"{nameof(Audio)}.{nameof(Audio.DefaultFadeDurationSeconds)}",
                "Value must be finite and non-negative."));
        }

        if (string.IsNullOrWhiteSpace(Audio.VoiceLocale))
        {
            issues.Add(new ConfigurationIssue(
                $"{nameof(Audio)}.{nameof(Audio.VoiceLocale)}",
                "Voice locale is required."));
        }

        ValidateAudioPath(Audio.BgmPath, nameof(Audio.BgmPath), issues);
        ValidateAudioPath(Audio.SfxPath, nameof(Audio.SfxPath), issues);
        ValidateAudioPath(Audio.VoicePath, nameof(Audio.VoicePath), issues);
    }

    private static void ValidateVolume(
        float value,
        string property,
        ICollection<ConfigurationIssue> issues)
    {
        if (!float.IsFinite(value) || value is < 0 or > 1)
        {
            issues.Add(new ConfigurationIssue(
                $"{nameof(Audio)}.{property}",
                "Value must be finite and between zero and one."));
        }
    }

    private static void ValidateAudioPath(
        string value,
        string property,
        ICollection<ConfigurationIssue> issues)
    {
        try
        {
            _ = new ResourceKey(value);
        }
        catch (ArgumentException exception)
        {
            issues.Add(new ConfigurationIssue(
                $"{nameof(Audio)}.{property}", exception.Message));
        }
    }

    private void ValidateDiagnostics(ICollection<ConfigurationIssue> issues)
    {
        if (Diagnostics is null)
        {
            issues.Add(new ConfigurationIssue(nameof(Diagnostics), "Configuration is required."));
            return;
        }

        if (Diagnostics.RingBufferCapacity <= 0)
        {
            issues.Add(new ConfigurationIssue(
                $"{nameof(Diagnostics)}.{nameof(Diagnostics.RingBufferCapacity)}",
                "Ring buffer capacity must be greater than zero."));
        }

        if (Diagnostics.MaxFileSizeBytes <= 0)
        {
            issues.Add(new ConfigurationIssue(
                $"{nameof(Diagnostics)}.{nameof(Diagnostics.MaxFileSizeBytes)}",
                "Max file size must be greater than zero."));
        }

        if (Diagnostics.MaxFileCount <= 0)
        {
            issues.Add(new ConfigurationIssue(
                $"{nameof(Diagnostics)}.{nameof(Diagnostics.MaxFileCount)}",
                "Max file count must be greater than zero."));
        }
    }
}
