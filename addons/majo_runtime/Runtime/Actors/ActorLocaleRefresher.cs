#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Majo.Runtime.Core;
using Majo.Runtime.Localization;

namespace Majo.Runtime.Actors;

internal sealed class ActorLocaleRefresher(
    ILocaleManager? locales, Func<IReadOnlyCollection<IActor>> snapshot, IRuntimeLogger logger) : IDisposable
{
    private bool attached;
    public bool RefreshBackgrounds { get; set; } = true;
    public void Attach()
    {
        if (attached || locales is null) return;
        locales.DialogueLocaleChanged += Refresh;
        attached = true;
    }
    public void Dispose()
    {
        if (attached && locales is not null) locales.DialogueLocaleChanged -= Refresh;
        attached = false;
    }
    private void Refresh(LocaleChangedEvent change)
    {
        foreach (var actor in snapshot().Where(value => value.Visible))
        {
            if (!RefreshBackgrounds && actor is IBackgroundActor) continue;
            try { actor.SetAppearance(actor.Appearance); }
            catch (Exception failure)
            {
                logger.Warning("locale", "locale.actor_refresh_failed",
                    $"Actor '{actor.Id}' kept its previous appearance after dialogue locale switched to '{change.CurrentLocale}': {failure.Message}", failure);
            }
        }
    }
}
