#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Majo.Runtime.Localization;

/// <summary>Participates before a dialogue locale is published as committed.</summary>
public interface IDialogueLocaleSwitchParticipant
{
    bool IsBusy { get; }
    Task WaitForSafeBoundaryAsync(CancellationToken cancellationToken);

    /// <summary>Also called with the previous locale to compensate a failed switch.</summary>
    Task ApplyAsync(string locale, CancellationToken cancellationToken);
}
