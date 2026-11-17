using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Dialogue;
using Majo.Runtime.Dialogue.Commands;
using Majo.Runtime.Dialogue.Presentation;
using Majo.Runtime.Dialogue.Yarn;
using Majo.Runtime.Dialogue.Yarn.HotReload;
using Majo.Runtime.Dialogue.Yarn.Presentation;
using Majo.Runtime.Input;
using Majo.Runtime.Localization;
using Majo.Runtime.Operations;
using Majo.Runtime.Resources;
using Majo.Runtime.Save;
using Majo.Runtime.Script;
using Majo.Runtime.State;

namespace Majo.Runtime.HeadlessTests;

public partial class HeadlessTestRunner
{
    private async Task ValidateServiceReplacementAsync()
    {
        var configuration = CreateHeadlessConfiguration();
        configuration.Resources!.RootPath = "res://tests";
        configuration.Resources.ScriptsPath = "headless";
        configuration.Dialogue!.DefaultProjectId = "YarnVmBridge";
        configuration.Settings!.Enabled = false;
        configuration.GlobalState!.Enabled = false;
        configuration.Save!.Enabled = true;
        configuration.Save.CaptureThumbnails = false;
        configuration.Save.ShowDefaultUi = false;
        configuration.Presentation!.LettersPerSecond = 10_000;
        configuration.Presentation.UseFadeEffect = false;
        var fonts = new FontCatalog();
        var bootstrap = new MajoRuntimeBootstrap { Configuration = configuration };
        bootstrap.ConfigureServices = services => services
            .ReplaceSingleton<IFontCatalog>(_ => fonts)
            .ReplaceSingleton<IYarnDialogueBackend>(resolver => new YarnDialogueBackend(
                bootstrap, resolver.Get<IResourceManager>(), configuration.Resources.ScriptsPath,
                Array.Empty<IDialoguePresenter>(), resolver.Get<ILocaleManager>(),
                projectVersions: resolver.Get<IYarnProjectVersionStore>()),
                typeof(IResourceManager), typeof(ILocaleManager), typeof(IYarnProjectVersionStore))
            .ReplaceSingleton<IDialoguePresentation>(resolver => new ReplacementPresentation(
                new YarnDefaultDialoguePresentation(resolver.Get<IYarnDialogueBackend>(),
                    resolver.Get<IInputManager>(), resolver.Get<IFrameClock>(), configuration.Presentation,
                    Array.Empty<IDialogueMarkupHandler>(), resolver.Get<ILocaleManager>(), fonts)),
                typeof(IYarnDialogueBackend), typeof(IInputManager), typeof(IFrameClock), typeof(ILocaleManager))
            .ReplaceSingleton<IDialoguePlayer>(resolver => new DialoguePlayer(
                resolver.Get<IYarnDialogueBackend>(), "YarnVmBridge", "Start"),
                typeof(IYarnDialogueBackend), typeof(IDialoguePresentation))
            .ReplaceSingleton<IScriptPlayback>(resolver => new ReplacementScriptPlayback(
                resolver.Get<IDialoguePlayer>(), resolver.Get<IDialoguePresentation>(), resolver.Get<IYarnDialogueBackend>()),
                typeof(IDialoguePlayer), typeof(IDialoguePresentation), typeof(IYarnDialogueBackend))
            .ReplaceSingleton<ISaveProductManager>(resolver => new SaveProductManager(
                resolver.Get<ISaveSlotManager>(), resolver.Get<IDialoguePlayer>(),
                resolver.Get<IDialoguePresentation>(), resolver.Get<IScriptPlayback>(), resolver.Get<IInputManager>()),
                typeof(ISaveSlotManager), typeof(IDialoguePlayer), typeof(IDialoguePresentation),
                typeof(IScriptPlayback), typeof(IInputManager));
        AddChild(bootstrap);
        try
        {
            await bootstrap.InitializationCompletion;
            var services = bootstrap.Host!.Services;
            var locales = services.Get<ILocaleManager>();
            Assert(ReferenceEquals(locales.Fonts, fonts), "LocaleManager retained the replaced font catalog.");
            Assert(ReferenceEquals(services.Get<ISaveProductAccessor>().SaveProduct,
                services.Get<ISaveProductManager>()), "Save accessor did not resolve the replacement service.");
            var dialogue = services.Get<IDialoguePlayer>();
            var script = services.Get<IScriptPlayback>();
            var boundaries = (IStateBoundarySource)services.Get<IStateManager>();
            await dialogue.StartAsync();
            await WaitForConditionAsync(() => script.Phase == ScriptPlaybackPhase.AwaitingAdvance, 2);
            Assert(services.Get<IDialogueNodeAccessor>().RequireNode() == "Start",
                "Direction commands lost the node of the replacement dialogue player.");
            var first = await boundaries.CaptureSaveBoundaryAsync();
            Assert(first.Context.Script.Spot.LineId == "line:bridge-headless-first",
                "Replacement playback did not provide its committed line boundary.");

            var result = await locales.ChangeDialogueLocaleAsync("zh-CN");
            Assert(result.Success && ((ReplacementPresentation)services.Get<IDialoguePresentation>()).ReRenderedLines > 0,
                "Locale switching did not refresh the replacement backend and presentation.");
            Assert(services.Get<IYarnDialogueBackend>().Runner.LineProvider.LocaleCode == "zh-CN",
                "Locale switching did not reach the replacement Yarn backend.");

            dialogue.RequestNextLine();
            await WaitForConditionAsync(() => script.Phase == ScriptPlaybackPhase.AwaitingAdvance &&
                dialogue.CurrentLineId == "line:bridge-headless-second", 2);
            await services.Get<IStateManager>().RestoreGameAsync(first.Snapshot);
            Assert(dialogue.CurrentLineId == "line:bridge-headless-first",
                "Checkpoint restoration did not activate the replacement playback boundary.");
            await WaitForConditionAsync(() => script.Phase == ScriptPlaybackPhase.AwaitingAdvance, 2);
            var completion = dialogue.Completion;
            var restore = services.Get<IStateManager>().RestoreTransaction;
            var plan = await restore.PreflightAsync(first.Snapshot, StateScope.Game, StateOperationKind.HotReload);
            await restore.ExecuteAsync(plan);
            Assert(ReferenceEquals(completion, dialogue.Completion) && !completion.IsCompleted &&
                dialogue.CurrentLineId == "line:bridge-headless-first",
                "Hot reload stopped the public dialogue session of the replacement playback.");
        }
        finally
        {
            await bootstrap.RequestShutdownAsync();
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }

        var withoutSave = new MajoRuntimeBootstrap { Configuration = CreateHeadlessConfiguration() };
        AddChild(withoutSave);
        try
        {
            await withoutSave.InitializationCompletion;
            Assert(withoutSave.Host!.Services.Get<ISaveProductAccessor>().SaveProduct is null,
                "The optional save accessor must remain available when saving is disabled.");
        }
        finally { await withoutSave.RequestShutdownAsync(); }
    }

    private sealed class ReplacementPresentation : IDialoguePresentation
    {
        private readonly IDialoguePresentation inner;

        public ReplacementPresentation(IDialoguePresentation inner) => this.inner = inner;

        public int ReRenderedLines { get; private set; }
        public event Action LineDisplayCompleted { add => inner.LineDisplayCompleted += value; remove => inner.LineDisplayCompleted -= value; }
        public event Action LineDismissed { add => inner.LineDismissed += value; remove => inner.LineDismissed -= value; }
        public bool IsLineActive => inner.IsLineActive;
        public bool IsLineDisplayComplete => inner.IsLineDisplayComplete;
        public bool IsPresentingOptions => inner.IsPresentingOptions;
        public void ReRenderLine(DialogueLine line) { ReRenderedLines++; inner.ReRenderLine(line); }
        public void ReRenderOptions(IReadOnlyList<DialogueOption> options) => inner.ReRenderOptions(options);
        public Task InitializeAsync(CancellationToken token) => inner.InitializeAsync(token);
        public Task ResetAsync(CancellationToken token) => inner.ResetAsync(token);
        public Task ShutdownAsync(CancellationToken token) => inner.ShutdownAsync(token);
    }

    private sealed class ReplacementScriptPlayback : IScriptPlayback, IScriptCheckpointRestorePreparation
    {
        private readonly ScriptPlayback inner;
        public ReplacementScriptPlayback(IDialoguePlayer dialogue, IDialoguePresentation presentation, IYarnDialogueBackend backend)
        {
            inner = new ScriptPlayback(dialogue, presentation, backend);
            BoundarySource = (ILineBoundarySource)backend;
        }
        public ILineBoundarySource BoundarySource { get; }
        public event Action<ScriptPlaybackPhaseChanged> PhaseChanged { add => inner.PhaseChanged += value; remove => inner.PhaseChanged -= value; }
        public ScriptPlaybackPhase Phase => inner.Phase;
        public ScriptPlaybackSpot CurrentSpot => inner.CurrentSpot;
        public bool CanCaptureCheckpoint => inner.CanCaptureCheckpoint;
        public ScriptCheckpoint CaptureCheckpoint() => inner.CaptureCheckpoint();
        public Task ValidateCheckpointAsync(ScriptCheckpoint checkpoint, CancellationToken token = default) => inner.ValidateCheckpointAsync(checkpoint, token);
        public Task RestoreCheckpointAsync(ScriptCheckpoint checkpoint, CancellationToken token = default) => inner.RestoreCheckpointAsync(checkpoint, token);
        public Task<IPreparedScriptCheckpoint> PrepareCheckpointRestoreAsync(ScriptCheckpoint checkpoint, CancellationToken token,
            DialoguePresentationOrigin origin = DialoguePresentationOrigin.CheckpointRestore) => inner.PrepareCheckpointRestoreAsync(checkpoint, token, origin);
        public Task InitializeAsync(CancellationToken token) => inner.InitializeAsync(token);
        public Task ResetAsync(CancellationToken token) => inner.ResetAsync(token);
        public Task ShutdownAsync(CancellationToken token) => inner.ShutdownAsync(token);
    }
}
