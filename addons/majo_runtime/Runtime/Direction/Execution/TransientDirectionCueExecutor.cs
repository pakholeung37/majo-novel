#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Operations;
using Majo.Runtime.Stage;

namespace Majo.Runtime.Direction.Execution;

public interface ITransientDirectionCueExecutor
{
    void Validate(IEnumerable<ITransientDirectionCue> cues)
    {
        ArgumentNullException.ThrowIfNull(cues);
    }

    Task ExecuteAsync(IEnumerable<ITransientDirectionCue> cues,
        DirectionCompletionBarrier completion, CancellationToken cancellationToken);

    Task ExecuteCommittedAsync(
        IEnumerable<ITransientDirectionCue> cues,
        DirectionCommitToken token,
        DirectionCompletionBarrier completion,
        CancellationToken cancellationToken) =>
        ExecuteAsync(cues, completion, cancellationToken);
}

public sealed class TransientDirectionCueExecutor : ITransientDirectionCueExecutor
{
    private readonly ICameraManager camera;
    private readonly IScreenEffectManager effects;
    private readonly IStageCurtainManager curtain;

    public TransientDirectionCueExecutor(ICameraManager camera, IScreenEffectManager effects,
        IStageCurtainManager curtain)
    {
        this.camera = camera ?? throw new ArgumentNullException(nameof(camera));
        this.effects = effects ?? throw new ArgumentNullException(nameof(effects));
        this.curtain = curtain ?? throw new ArgumentNullException(nameof(curtain));
    }

    public void Validate(IEnumerable<ITransientDirectionCue> cues)
    {
        ArgumentNullException.ThrowIfNull(cues);
        foreach (var cue in cues)
        {
            switch (cue)
            {
                case ShakeCue shake: ValidateShake(shake); break;
                case FlashCue flash: ValidateFlash(flash); break;
                case CurtainCue curtainCue:
                    ValidateDuration(curtainCue.DurationSeconds);
                    curtainCue.Color.Validate();
                    break;
                case DirectionCue generic: ValidateGeneric(generic); break;
                default: throw new DirectionException($"Unsupported transient cue '{cue.GetType().Name}'.");
            }
        }
    }

    public async Task ExecuteAsync(IEnumerable<ITransientDirectionCue> cues,
        DirectionCompletionBarrier completion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cues);
        if (!Enum.IsDefined(completion))
            throw new ArgumentOutOfRangeException(nameof(completion));
        var operations = new List<IOperationHandle>();
        try
        {
            foreach (var cue in cues)
            {
                cancellationToken.ThrowIfCancellationRequested();
                switch (cue)
                {
                    case ShakeCue shake:
                        ValidateShake(shake);
                        operations.Add(camera.Shake(new CameraShakeSpec(shake.Strength, shake.Axis,
                            shake.Frequency, new StageOperationSpec(shake.DurationSeconds))));
                        break;
                    case FlashCue flash:
                        ValidateFlash(flash);
                        operations.Add(effects.Flash(ToColor(flash.Color), flash.Intensity,
                            new StageOperationSpec(flash.DurationSeconds)));
                        break;
                    case CurtainCue curtainCue:
                        ValidateDuration(curtainCue.DurationSeconds);
                        curtainCue.Color.Validate();
                        operations.Add(curtain.SetCurtain(ToColor(curtainCue.Color),
                            curtainCue.Open ? 0 : 1, new StageOperationSpec(curtainCue.DurationSeconds)));
                        break;
                    case DirectionCue generic:
                        ExecuteGeneric(generic, operations);
                        break;
                    default:
                        throw new DirectionException($"Unsupported transient cue '{cue.GetType().Name}'.");
                }
            }
            if (completion == DirectionCompletionBarrier.Settle)
                await Task.WhenAll(operations.Select(operation => operation.Completion)).WaitAsync(cancellationToken);
        }
        catch
        {
            foreach (var operation in operations)
                operation.Cancel();
            throw;
        }
    }

    private static Color ToColor(DirectionColor color) => new(color.R, color.G, color.B, color.A);

    private void ExecuteGeneric(DirectionCue cue, ICollection<IOperationHandle> operations)
    {
        ValidateGeneric(cue);
        if (cue.Name.Equals("shake", StringComparison.OrdinalIgnoreCase))
        {
            var strength = Read(cue, "strength", 8);
            var axis = cue.Arguments.TryGetValue("axis", out var axisText) &&
                Enum.TryParse<CameraShakeAxis>(axisText, true, out var parsed) ? parsed : CameraShakeAxis.All;
            var frequency = Read(cue, "frequency", 20);
            var duration = Read(cue, "over", 0.4);
            if (!double.IsFinite(strength) || strength is < 0 or > float.MaxValue ||
                !double.IsFinite(frequency) ||
                frequency is <= 0 or > int.MaxValue)
                throw new DirectionException("Shake cue has invalid strength or frequency.");
            ValidateDuration(duration);
            operations.Add(camera.Shake(new CameraShakeSpec((float)strength, axis, (int)frequency,
                new StageOperationSpec(duration))));
            return;
        }
        if (cue.Name.Equals("curtain", StringComparison.OrdinalIgnoreCase))
        {
            var open = cue.Arguments.TryGetValue("state", out var state) && state.Equals("open", StringComparison.OrdinalIgnoreCase);
            var duration = Read(cue, "over", 0.4);
            ValidateDuration(duration);
            operations.Add(curtain.SetCurtain(Colors.Black, open ? 0 : 1,
                new StageOperationSpec(duration)));
            return;
        }
        throw new DirectionException($"Unsupported transient cue '{cue.Name}'.");
    }

    private static double Read(DirectionCue cue, string name, double fallback) =>
        cue.Arguments.TryGetValue(name, out var value) && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result)
            ? result : fallback;

    private static void ValidateShake(ShakeCue cue)
    {
        if (!float.IsFinite(cue.Strength) || cue.Strength < 0 || cue.Frequency <= 0)
            throw new DirectionException("Shake cue has invalid strength or frequency.");
        ValidateDuration(cue.DurationSeconds);
    }

    private static void ValidateFlash(FlashCue cue)
    {
        cue.Color.Validate();
        if (!float.IsFinite(cue.Intensity) || cue.Intensity < 0 || cue.Intensity > 1)
            throw new DirectionException("Flash cue intensity must be between zero and one.");
        ValidateDuration(cue.DurationSeconds);
    }

    private static void ValidateDuration(double duration)
    {
        if (!double.IsFinite(duration) || duration < 0)
            throw new DirectionException("Transient cue duration must be finite and non-negative.");
    }

    private static void ValidateGeneric(DirectionCue cue)
    {
        if (cue.Name.Equals("shake", StringComparison.OrdinalIgnoreCase))
        {
            var strength = Read(cue, "strength", 8);
            var frequency = Read(cue, "frequency", 20);
            if (!double.IsFinite(strength) || strength is < 0 or > float.MaxValue ||
                !double.IsFinite(frequency) || frequency is <= 0 or > int.MaxValue)
                throw new DirectionException("Shake cue has invalid strength or frequency.");
            ValidateDuration(Read(cue, "over", 0.4));
            return;
        }
        if (cue.Name.Equals("curtain", StringComparison.OrdinalIgnoreCase))
        {
            ValidateDuration(Read(cue, "over", 0.4));
            return;
        }
        throw new DirectionException($"Unsupported transient cue '{cue.Name}'.");
    }

}
