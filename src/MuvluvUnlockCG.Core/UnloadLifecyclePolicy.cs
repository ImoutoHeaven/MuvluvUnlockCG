using System;

namespace MuvluvUnlockCG.Core;

/// <summary>
/// Public lifecycle seam for the plugin unload decision. The native MoveToAdventure
/// state machines consume the same Viewable bit that the cell factory temporarily
/// changes (d60:418-421/d61:268-271), while EpisodeComponent interop exposes the
/// accessor and backing-field restore routes (EpisodeComponent.cs:971-981). A
/// failed restore must therefore retain the hooks and lease for a later retry.
/// </summary>
public static class UnloadLifecyclePolicy
{
    /// <summary>
    /// Restores all local visibility state before unpatching. The unpatch callback
    /// is never invoked after a failed or throwing restore callback. A successful
    /// restore invokes it once and reports success; an unpatch exception is
    /// contained and reports failure so the caller can keep its lifecycle retry
    /// path alive.
    /// </summary>
    public static bool TryUnload(Func<bool> restoreAll, Action unpatch)
    {
        if (restoreAll is null || unpatch is null)
        {
            return false;
        }

        bool restored;
        try
        {
            restored = restoreAll();
        }
        catch
        {
            return false;
        }

        if (!restored)
        {
            return false;
        }

        try
        {
            unpatch();
            return true;
        }
        catch
        {
            return false;
        }
    }
}
