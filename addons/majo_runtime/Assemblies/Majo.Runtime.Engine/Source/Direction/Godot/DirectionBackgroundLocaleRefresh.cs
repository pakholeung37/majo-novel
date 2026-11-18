#nullable enable

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Execution;
using Majo.Runtime.Localization;

namespace Majo.Runtime.Direction.Godot;

/// <summary>Locale refreshes enter the same serialized transaction path as authored background changes.</summary>
internal sealed class DirectionBackgroundLocaleRefresh(ILocaleManager locales, IDirectionTransactionCoordinator direction,
    GodotDirectionStageBackend backend, IRuntimeLogger logger) : IRuntimeService, IDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private Task pending = Task.CompletedTask;
    public Task Pending => pending;
    public Task InitializeAsync(CancellationToken token)
    { token.ThrowIfCancellationRequested(); locales.DialogueLocaleChanged += Changed; return Task.CompletedTask; }
    private void Changed(LocaleChangedEvent change)
    {
        backend.InvalidateLocale();
        pending = RefreshAsync(pending);
    }
    private async Task RefreshAsync(Task previous)
    {
        try
        {
            await previous;
            lifetime.Token.ThrowIfCancellationRequested();
            var patches = direction.State.Stages.Values.Where(stage => stage.Backdrop is not null)
                .Select(stage => (IDirectionDirective)new BackdropPatch(stage.Id, stage.Backdrop, DirectionTransitionSpec.Cut)).ToArray();
            if (patches.Length > 0) await direction.ExecuteAsync(patches, DirectionCompletionBarrier.Settle, lifetime.Token);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error)
        { logger.Warning("locale", "locale.direction_background_refresh_failed", "Background locale refresh kept the previous presentation.", error); }
    }
    public Task ResetAsync(CancellationToken token) => pending;
    public async Task ShutdownAsync(CancellationToken token)
    { locales.DialogueLocaleChanged -= Changed; lifetime.Cancel(); await pending; }
    public void Dispose() => lifetime.Dispose();
}
