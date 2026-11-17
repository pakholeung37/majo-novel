#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;
using Majo.Runtime.Dialogue.Yarn.HotReload;
using Majo.Runtime.Script;
using Majo.YarnBridge;
using YarnSpinnerGodot;

namespace Majo.Runtime.Dialogue.Yarn;

internal sealed class YarnCheckpointController
{
    private readonly IYarnProjectVersionStore projectVersions;
    private readonly bool requireExactProgramFingerprint;
    private readonly IRuntimeLogger logger;
    private readonly YarnVmStateBridge stateBridge = new();
    private ScriptCheckpoint? entryCheckpoint;
    private DialoguePresentationOrigin presentationOrigin;

    public YarnCheckpointController(IYarnProjectVersionStore projectVersions,
        bool requireExactProgramFingerprint, IRuntimeLogger logger)
    {
        this.projectVersions = projectVersions;
        this.requireExactProgramFingerprint = requireExactProgramFingerprint;
        this.logger = logger;
    }

    public ScriptCheckpoint? Pending { get; private set; }
    public void BeginSession() => entryCheckpoint = null;
    public void ClearPending()
    {
        Pending = null;
        presentationOrigin = DialoguePresentationOrigin.Normal;
    }
    public void ReplacePending(ScriptCheckpoint checkpoint, DialoguePresentationOrigin origin)
    {
        Pending = checkpoint;
        presentationOrigin = origin;
    }
    public DialoguePresentationOrigin ConsumePresentationOrigin()
    {
        var origin = presentationOrigin;
        presentationOrigin = DialoguePresentationOrigin.Normal;
        return origin;
    }
    public int GetCallStackDepth(DialogueRunner runner) => stateBridge.GetCallStackDepth(runner.Dialogue);

    public async Task<ScriptDescriptor> ResolveAsync(
        ScriptId id,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        YarnProject project;
        try
        {
            project = await projectVersions.GetActiveAsync(id.ProjectId, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new ScriptRuntimeException(
                $"Failed to load script project '{id.ProjectId}'.",
                exception);
        }

        return CreateDescriptor(project, id);
    }

    public ScriptCheckpoint Capture(DialogueRunner runner, ScriptId id, string lineId,
        IReadOnlyList<string> substitutions, bool atEntry = false)
    {
        if (!atEntry && entryCheckpoint is not null) return entryCheckpoint;
        var project = runner.YarnProject ?? throw new ScriptCheckpointException(
            "No Yarn project is active.");
        if (string.IsNullOrWhiteSpace(id.NodeName) ||
            string.IsNullOrWhiteSpace(lineId))
        {
            throw new ScriptCheckpointException(
                "Yarn playback is not waiting at a line boundary.");
        }

        var descriptor = CreateDescriptor(project, id);
        var snapshot = atEntry
            ? stateBridge.CaptureAtLineEntry(runner.Dialogue, lineId, substitutions)
            : stateBridge.CaptureAtLine(runner.Dialogue, lineId, substitutions);
        var checkpoint = new ScriptCheckpoint(
            ScriptCheckpoint.CurrentFormatVersion,
            descriptor.ProgramFingerprint,
            new ScriptPlaybackSpot(id, snapshot.LineId),
            snapshot.ResolvedSubstitutions,
            snapshot.CallStack.Select(frame => new ScriptCallFrame(frame.NodeName, frame.InstructionIndex)),
            snapshot.OperandStack.Select(value => new ScriptStackValue(value.Type, value.Value)));
        if (atEntry) entryCheckpoint = checkpoint;
        return checkpoint;
    }

    public async Task ValidateCheckpointAsync(
        ScriptCheckpoint checkpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (checkpoint.FormatVersion is not (ScriptCheckpoint.CurrentFormatVersion or ScriptCheckpoint.LegacyFormatVersion) ||
            (checkpoint.FormatVersion == ScriptCheckpoint.LegacyFormatVersion &&
                (checkpoint.CallStack.Count > 0 || checkpoint.OperandStack.Count > 0)))
        {
            throw new ScriptCheckpointException(
                $"Unsupported script checkpoint format '{checkpoint.FormatVersion}'; " +
                $"expected '{ScriptCheckpoint.CurrentFormatVersion}'.");
        }

        if (checkpoint.Spot.Boundary != ScriptCheckpointBoundary.LineEntry)
        {
            throw new ScriptCheckpointException(
                $"Unsupported script checkpoint boundary '{checkpoint.Spot.Boundary}'.");
        }

        var descriptor = await ResolveAsync(checkpoint.Spot.Script, cancellationToken);
        if (!descriptor.LineIds.Contains(checkpoint.Spot.LineId, StringComparer.Ordinal))
        {
            throw new ScriptCheckpointException(
                $"Line '{checkpoint.Spot.LineId}' does not exist in script " +
                $"'{checkpoint.Spot.Script}'.");
        }

        if (descriptor.LineSubstitutionCounts.TryGetValue(
                checkpoint.Spot.LineId,
                out var substitutionCount) &&
            substitutionCount != checkpoint.ResolvedSubstitutions.Count)
        {
            throw new ScriptCheckpointException(
                $"Line '{checkpoint.Spot.LineId}' now requires {substitutionCount} substitutions, " +
                $"but the checkpoint contains {checkpoint.ResolvedSubstitutions.Count}.");
        }

        var exactProgram = string.Equals(
                descriptor.ProgramFingerprint,
                checkpoint.ProgramFingerprint,
                StringComparison.Ordinal);
        if (!exactProgram && (requireExactProgramFingerprint ||
                checkpoint.CallStack.Count > 0 || checkpoint.OperandStack.Count > 0))
        {
            throw new ScriptCheckpointException(
                $"Script '{checkpoint.Spot.Script}' no longer matches checkpoint fingerprint.");
        }

        var project = await projectVersions.GetActiveAsync(checkpoint.Spot.Script.ProjectId, cancellationToken);
        stateBridge.ValidateReplay(project.Program, CreateBridgeSnapshot(checkpoint));
        if (exactProgram) return;

        logger.Debug(
            "script",
            "script.checkpoint_program_changed",
            $"Script '{checkpoint.Spot.Script}' changed after the checkpoint was captured; " +
            "restore will continue using its stable line ID.",
            properties: new Dictionary<string, object?>
            {
                ["projectId"] = checkpoint.Spot.Script.ProjectId,
                ["nodeName"] = checkpoint.Spot.Script.NodeName,
                ["lineId"] = checkpoint.Spot.LineId,
                ["checkpointFingerprint"] = checkpoint.ProgramFingerprint,
                ["currentFingerprint"] = descriptor.ProgramFingerprint,
            });
    }

    public void PrepareRestore(ScriptCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (Pending is not null)
        {
            throw new ScriptCheckpointException(
                "Another script checkpoint restore is already pending.");
        }

        Pending = checkpoint;
        presentationOrigin = DialoguePresentationOrigin.CheckpointRestore;
    }

    public void PrepareRestore(
        ScriptCheckpoint checkpoint,
        DialoguePresentationOrigin origin)
    {
        if (origin == DialoguePresentationOrigin.Normal)
            throw new ScriptCheckpointException("Checkpoint replay requires a non-normal presentation origin.");
        PrepareRestore(checkpoint);
        presentationOrigin = origin;
    }

    public void CancelPreparedRestore(ScriptCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (Pending is null || ReferenceEquals(Pending, checkpoint) || Pending == checkpoint)
        {
            Pending = null;
            presentationOrigin = DialoguePresentationOrigin.Normal;
        }
    }

    public int? RestoreSelectedNode(DialogueRunner runner)
    {
        if (Pending is not { } checkpoint)
        {
            return null;
        }

        Pending = null;

        var project = runner.YarnProject ?? throw new ScriptCheckpointException(
            "DialogueRunner has no Yarn project during checkpoint restore.");
        var bridgeSnapshot = CreateBridgeSnapshot(checkpoint);
        stateBridge.InjectReplayAfterNodeSelected(
            runner.Dialogue,
            project.Program,
            bridgeSnapshot);
        return bridgeSnapshot.CallStackDepth;
    }

    private static YarnVmLineSnapshot CreateBridgeSnapshot(ScriptCheckpoint checkpoint) => new(
            checkpoint.Spot.Script.NodeName,
            checkpoint.Spot.LineId,
            0,
            "WaitingForContinue",
            checkpoint.OperandStack.Count,
            checkpoint.CallStack.Count,
            0,
            checkpoint.ResolvedSubstitutions)
    {
        CallStack = checkpoint.CallStack.Select(frame => new YarnVmCallFrame(frame.NodeName, frame.InstructionIndex)).ToArray(),
        OperandStack = checkpoint.OperandStack.Select(value => new YarnVmStackValue(value.Type, value.Value)).ToArray(),
    };

    private static ScriptDescriptor CreateDescriptor(YarnProject project, ScriptId id) =>
        YarnScriptCatalogHelper.CreateDescriptor(project, id);
}
