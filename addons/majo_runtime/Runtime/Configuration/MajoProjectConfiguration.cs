#nullable enable

using System;
using System.Collections.Generic;
using Godot;

namespace Majo.Runtime.Configuration;

[GlobalClass]
public partial class MajoProjectConfiguration : Resource, IValidatableConfiguration
{
    [Export]
    public RuntimeConfiguration? Runtime { get; set; }

    [Export]
    public OperationConfiguration? Operations { get; set; }

    [Export]
    public AnimationConfiguration? Animation { get; set; }

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
    public SaveConfiguration? Save { get; set; }

    [Export]
    public AutoConfiguration? Auto { get; set; }

    [Export]
    public HistoryConfiguration? History { get; set; }

    [Export]
    public DiagnosticsConfiguration? Diagnostics { get; set; }

    [Export]
    public SettingsConfiguration? Settings { get; set; }

    [Export]
    public LocalizationConfiguration? Localization { get; set; }

    [Export]
    public StageConfiguration? Stage { get; set; }

    [Export]
    public CameraEnvironmentConfiguration? CameraEnvironment { get; set; }

    [Export]
    public GlobalStateConfiguration? GlobalState { get; set; }

    public static MajoProjectConfiguration CreateDefault() => new()
    {
        Runtime = new RuntimeConfiguration(),
        Operations = new OperationConfiguration(),
        Animation = new AnimationConfiguration(),
        Resources = new ResourceConfiguration(),
        Actors = new ActorConfiguration(),
        Dialogue = new DialogueConfiguration(),
        Input = new InputConfiguration(),
        Presentation = new DialoguePresentationConfiguration(),
        Audio = new AudioConfiguration(),
        Script = new ScriptConfiguration(),
        State = new StateConfiguration(),
        Save = new SaveConfiguration(),
        Auto = new AutoConfiguration(),
        History = new HistoryConfiguration(),
        Diagnostics = new DiagnosticsConfiguration(),
        Settings = new SettingsConfiguration(),
        Localization = new LocalizationConfiguration(),
        Stage = new StageConfiguration(),
        CameraEnvironment = new CameraEnvironmentConfiguration(),
        GlobalState = new GlobalStateConfiguration(),
    };

    public ConfigurationValidationResult Validate(Func<string, bool>? resourceExists = null)
    {
        resourceExists ??= path => ResourceLoader.Exists(path);
        var issues = new List<ConfigurationIssue>();
        var context = new ConfigurationValidationContext(this, resourceExists, issues);

        Validate(context);
        return new ConfigurationValidationResult(issues);
    }

    public void Validate(ConfigurationValidationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        ValidateSection(nameof(Runtime), Runtime, context);
        ValidateSection(nameof(Operations), Operations, context);
        ValidateSection(nameof(Animation), Animation, context);
        ValidateSection(nameof(Resources), Resources, context);
        ValidateSection(nameof(Actors), Actors, context);
        ValidateSection(nameof(Dialogue), Dialogue, context);
        ValidateSection(nameof(Input), Input, context);
        ValidateSection(nameof(Presentation), Presentation, context);
        ValidateSection(nameof(Audio), Audio, context);
        ValidateSection(nameof(Script), Script, context);
        ValidateSection(nameof(State), State, context);
        ValidateSection(nameof(Save), Save, context);
        ValidateSection(nameof(Auto), Auto, context);
        ValidateSection(nameof(History), History, context);
        ValidateSection(nameof(Diagnostics), Diagnostics, context);
        ValidateSection(nameof(Settings), Settings, context);
        ValidateSection(nameof(Localization), Localization, context);
        ValidateSection(nameof(Stage), Stage, context);
        if (CameraEnvironment is not null)
            CameraEnvironment.Validate(context.ForSection(nameof(CameraEnvironment)));
        ValidateSection(nameof(GlobalState), GlobalState, context);
    }

    private static void ValidateSection<T>(
        string sectionName,
        T? section,
        ConfigurationValidationContext context)
        where T : class, IValidatableConfiguration
    {
        if (section is null)
        {
            context.Issues.Add(new ConfigurationIssue(sectionName, "Configuration is required."));
            return;
        }

        section.Validate(context.ForSection(sectionName));
    }
}
