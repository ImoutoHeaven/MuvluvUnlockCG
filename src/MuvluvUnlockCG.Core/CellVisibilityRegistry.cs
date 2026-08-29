using System;
using System.Collections.Generic;

namespace MuvluvUnlockCG.Core;

/// <summary>
/// Identity of an individual generated episode cell. The cell factory evidence
/// (EpisodeController b54_0/b56_1) and the d60/d61 MoveNext entry seams share
/// this episode/controller boundary. Full ISIL callers are persisted at
/// evidence/decomp/full-isil/IsilDump/GameUi/Assets/GameUi/Episode/
/// EpisodeComponent_NestedType__SelectCharacterCell_d__39.txt:505-510 and
/// EpisodeComponent_NestedType___ProcessOnCreate_b__32_20_d.txt:351-356.
/// </summary>
public readonly record struct CellIdentity(IntPtr EpisodeController, long EpisodeId)
{
    public bool IsComplete => EpisodeController != IntPtr.Zero && EpisodeId > 0;
}

/// <summary>
/// The native value captured before a LocalBypass cell is made visible. The
/// adapter must complete a restoration before this value is discarded.
/// </summary>
public readonly record struct CellVisibilitySnapshot(CellIdentity Identity, bool OriginalViewable);

/// <summary>
/// Fail-open visibility state shared by the runtime adapter and lifecycle tests.
/// A lease owns the generated cell wrapper strongly while its native Viewable
/// override is outstanding. It is released only after the supplied restoration
/// callback reports success; a setter failure retains both wrapper and snapshot
/// for a later lifecycle retry. The number of strong references is bounded by
/// the number of active captured cell identities.
/// </summary>
public sealed class CellVisibilityRegistry
{
    private sealed class Lease
    {
        public Lease(object wrapper, CellVisibilitySnapshot snapshot)
        {
            Wrapper = wrapper;
            Snapshot = snapshot;
        }

        public object Wrapper { get; }
        public CellVisibilitySnapshot Snapshot { get; }
    }

    private readonly object _gate = new object();
    private readonly Dictionary<CellIdentity, Lease> _leases = new();

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _leases.Count;
            }
        }
    }

    /// <summary>
    /// Records a LocalBypass cell and returns the value that may be assigned to
    /// its native Viewable property. Normal, incomplete, or invalid decisions
    /// never create a visibility override and preserve the original value.
    /// An existing lease is never overwritten: the caller must successfully
    /// restore it first so replacement generation cannot strand its wrapper.
    /// </summary>
    public bool Capture(
        CellIdentity identity,
        object wrapper,
        bool originalViewable,
        EligibilityDecision decision,
        out bool viewable)
    {
        viewable = originalViewable;
        lock (_gate)
        {
            if (!identity.IsComplete || wrapper is null || !decision.IsLocalBypass)
            {
                return false;
            }

            if (_leases.ContainsKey(identity))
            {
                return false;
            }

            var snapshot = new CellVisibilitySnapshot(identity, originalViewable);
            _leases[identity] = new Lease(wrapper, snapshot);
            viewable = true;
            return true;
        }
    }

    public bool Contains(CellIdentity identity)
    {
        lock (_gate)
        {
            return _leases.ContainsKey(identity);
        }
    }

    /// <summary>
    /// Returns the strongly held generated args wrapper for a captured cell.
    /// Entry gates can use it to place a temporary native lock message after a
    /// source-preparation miss, before the lifecycle finalizer restores it.
    /// </summary>
    public bool TryGetWrapper(CellIdentity identity, out object wrapper)
    {
        lock (_gate)
        {
            if (_leases.TryGetValue(identity, out var lease))
            {
                wrapper = lease.Wrapper;
                return true;
            }
        }

        wrapper = null!;
        return false;
    }

    /// <summary>
    /// Resolves the captured cell identity for an exact generated args object.
    /// Apply state machines carry their own args field (rather than the
    /// EpisodeController/catalog controller pointer), so this reference match
    /// lets the narrow consumer seam reuse the factory's LocalBypass lease
    /// without guessing an account or episode identity.
    /// </summary>
    public bool TryGetIdentity(object wrapper, out CellIdentity identity)
    {
        lock (_gate)
        {
            foreach (var pair in _leases)
            {
                if (ReferenceEquals(pair.Value.Wrapper, wrapper))
                {
                    identity = pair.Key;
                    return true;
                }
            }
        }

        identity = default;
        return false;
    }

    /// <summary>
    /// Resolves an EpisodeId only when exactly one active cell lease owns it.
    /// Generated IL2CPP state machines can expose a different managed wrapper
    /// for the same native args; duplicate episode leases are ambiguous and
    /// deliberately fail open to native presentation.
    /// </summary>
    public bool TryGetUniqueIdentity(long episodeId, out CellIdentity identity)
    {
        identity = default;
        if (episodeId <= 0)
        {
            return false;
        }

        lock (_gate)
        {
            var found = false;
            foreach (var candidate in _leases.Keys)
            {
                if (candidate.EpisodeId != episodeId)
                {
                    continue;
                }

                if (found)
                {
                    identity = default;
                    return false;
                }

                identity = candidate;
                found = true;
            }

            return found;
        }
    }

    /// <summary>
    /// Attempts one restoration. A missing lease is already restored and is
    /// therefore successful. Callback exceptions or a false callback result
    /// retain the strong wrapper and native snapshot for retry.
    /// </summary>
    public bool TryRestore(CellIdentity identity, Func<object, bool, bool> restore)
    {
        if (restore is null)
        {
            return false;
        }

        Lease lease;
        lock (_gate)
        {
            if (!_leases.TryGetValue(identity, out lease!))
            {
                return true;
            }
        }

        bool restored;
        try
        {
            restored = restore(lease.Wrapper, lease.Snapshot.OriginalViewable);
        }
        catch
        {
            restored = false;
        }

        if (!restored)
        {
            return false;
        }

        lock (_gate)
        {
            // Do not remove a newer replacement lease that was captured while
            // the native setter was running.
            if (_leases.TryGetValue(identity, out var current)
                && ReferenceEquals(current, lease))
            {
                _leases.Remove(identity);
            }
        }

        return true;
    }

    /// <summary>
    /// Drains all active overrides through the same restoration primitive. The
    /// returned snapshots include failed attempts so diagnostics/tests can see
    /// which identities still require a retry; failed leases remain active.
    /// </summary>
    public IReadOnlyList<CellVisibilitySnapshot> RestoreAll(Func<object, bool, bool> restore)
    {
        var snapshots = SnapshotAll();
        for (var index = 0; index < snapshots.Count; index++)
        {
            TryRestore(snapshots[index].Identity, restore);
        }

        return snapshots;
    }

    /// <summary>
    /// Attempts the complete unload/disable restore and reports whether every
    /// strong lease was released. A false result is intentionally retryable.
    /// </summary>
    public bool TryRestoreAll(Func<object, bool, bool> restore)
    {
        RestoreAll(restore);
        return Count == 0;
    }

    /// <summary>
    /// Restores stale overrides while retaining the currently entering cell.
    /// </summary>
    public IReadOnlyList<CellVisibilitySnapshot> RestoreAllExcept(
        CellIdentity keep,
        Func<object, bool, bool> restore)
    {
        var snapshots = SnapshotAllExcept(keep);
        for (var index = 0; index < snapshots.Count; index++)
        {
            TryRestore(snapshots[index].Identity, restore);
        }

        return snapshots;
    }

    private IReadOnlyList<CellVisibilitySnapshot> SnapshotAll()
    {
        lock (_gate)
        {
            if (_leases.Count == 0)
            {
                return Array.Empty<CellVisibilitySnapshot>();
            }

            var snapshots = new List<CellVisibilitySnapshot>(_leases.Count);
            foreach (var lease in _leases.Values)
            {
                snapshots.Add(lease.Snapshot);
            }

            return snapshots;
        }
    }

    private IReadOnlyList<CellVisibilitySnapshot> SnapshotAllExcept(CellIdentity keep)
    {
        lock (_gate)
        {
            if (_leases.Count == 0)
            {
                return Array.Empty<CellVisibilitySnapshot>();
            }

            var snapshots = new List<CellVisibilitySnapshot>();
            foreach (var pair in _leases)
            {
                if (pair.Key != keep)
                {
                    snapshots.Add(pair.Value.Snapshot);
                }
            }

            return snapshots;
        }
    }
}
