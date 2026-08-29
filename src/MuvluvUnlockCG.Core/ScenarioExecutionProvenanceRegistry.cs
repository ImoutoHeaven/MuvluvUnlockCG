using System;
using System.Collections.Generic;

namespace MuvluvUnlockCG.Core;

/// <summary>
/// The two ScenarioController state machines whose business calls are guarded.
/// These names mirror the persisted generated interop types d104 and d103.
/// </summary>
public enum ScenarioExecutionKind
{
    PostRead,
    BranchSelection,
}

/// <summary>
/// A one-shot handoff from the public ScenarioController wrapper to the first
/// synchronous MoveNext invocation. The wrapper evidence calls AsyncUniTask
/// MethodBuilder.Start and then invokes MoveNext before returning; this frame
/// lets that first invocation prove its identity without a pointer-keyed queue.
/// </summary>
public sealed class ScenarioExecutionStartFrame
{
    private readonly object _gate = new object();
    private int _state;

    internal ScenarioExecutionStartFrame(
        ScenarioExecutionKind kind,
        IntPtr scenarioController,
        PlaybackIdentity identity)
    {
        Kind = kind;
        ScenarioController = scenarioController;
        Identity = identity;
    }

    public ScenarioExecutionKind Kind { get; }
    public IntPtr ScenarioController { get; }
    public PlaybackIdentity Identity { get; }

    public bool IsCleared
    {
        get
        {
            lock (_gate)
            {
                return (_state & 2) != 0;
            }
        }
    }

    public bool IsConsumed
    {
        get
        {
            lock (_gate)
            {
                return (_state & 1) != 0;
            }
        }
    }

    public bool Matches(
        ScenarioExecutionKind kind,
        IntPtr scenarioController,
        int state,
        int initialState)
    {
        lock (_gate)
        {
            return _state == 0
                && state == initialState
                && Kind == kind
                && ScenarioController != IntPtr.Zero
                && ScenarioController == scenarioController
                && Identity.IsComplete
                && Identity.ScenarioController == scenarioController;
        }
    }

    /// <summary>
    /// Consumes this handoff exactly once. A wrapper finalizer or ClearAll may
    /// clear it before consumption, in which case the state machine fails open.
    /// </summary>
    public bool TryConsume()
    {
        lock (_gate)
        {
            if (_state != 0)
            {
                return false;
            }

            _state = 1;
            return true;
        }
    }

    /// <summary>
    /// Makes the handoff unusable. This operation is idempotent and does not
    /// retain any native pointer in the provenance registry.
    /// </summary>
    public void Clear()
    {
        lock (_gate)
        {
            _state |= 2;
        }
    }
}

/// <summary>
/// Bounded provenance for generated ScenarioController state machines. Active
/// entries exist only while a state machine may resume and are removed on its
/// terminal/fault path. ClearAll genuinely drops every entry; it never replaces
/// entries with pointer-retaining tombstones. The wrapper start frame is the
/// only way an initial state can acquire a new identity after that clear.
/// </summary>
public sealed class ScenarioExecutionProvenanceRegistry
{
    /// <summary>
    /// The persisted ScenarioController wrappers initialize d103/d104 to -1
    /// before AsyncUniTaskMethodBuilder.Start (full ISIL around lines 13770 and
    /// 13903). The generated MoveNext methods write 0 only when they suspend
    /// (selected d103:1464-1466 and d104:511-512), so the wrapper frame must be
    /// checked together with this exact initial state.
    /// </summary>
    public const int InitialState = -1;

    private readonly record struct ExecutionKey(
        IntPtr StateMachine,
        ScenarioExecutionKind Kind);

    private readonly object _gate = new object();
    private readonly Dictionary<ExecutionKey, PlaybackIdentity> _active = new();
    private readonly HashSet<ScenarioExecutionStartFrame> _startFrames = new();

    public int ActiveCount
    {
        get
        {
            lock (_gate)
            {
                return _active.Count;
            }
        }
    }

    public int StartFrameCount
    {
        get
        {
            lock (_gate)
            {
                return _startFrames.Count;
            }
        }
    }

    public ScenarioExecutionStartFrame? CreateStartFrame(
        ScenarioExecutionKind kind,
        IntPtr scenarioController,
        PlaybackIdentity identity)
    {
        if (scenarioController == IntPtr.Zero
            || !identity.IsComplete
            || identity.ScenarioController != scenarioController)
        {
            return null;
        }

        var frame = new ScenarioExecutionStartFrame(kind, scenarioController, identity);
        lock (_gate)
        {
            _startFrames.Add(frame);
        }

        return frame;
    }

    /// <summary>
    /// Looks up a state machine that already consumed its wrapper handoff.
    /// </summary>
    public bool TryGet(
        IntPtr stateMachine,
        ScenarioExecutionKind kind,
        out PlaybackIdentity identity)
    {
        if (stateMachine == IntPtr.Zero)
        {
            identity = default;
            return false;
        }

        lock (_gate)
        {
            return _active.TryGetValue(new ExecutionKey(stateMachine, kind), out identity);
        }
    }

    /// <summary>
    /// Consumes a matching wrapper frame only for the exact generated initial
    /// state. The operation and key insertion are atomic with respect to
    /// ClearAll, preventing a stale continuation from borrowing a new frame.
    /// </summary>
    public bool BeginInitial(
        IntPtr stateMachine,
        IntPtr scenarioController,
        ScenarioExecutionKind kind,
        int state,
        ScenarioExecutionStartFrame? startFrame,
        out PlaybackIdentity identity)
    {
        identity = default;
        if (stateMachine == IntPtr.Zero
            || scenarioController == IntPtr.Zero
            || startFrame is null
            || !startFrame.Matches(kind, scenarioController, state, InitialState))
        {
            return false;
        }

        lock (_gate)
        {
            var key = new ExecutionKey(stateMachine, kind);
            if (_active.TryGetValue(key, out identity))
            {
                if (identity.IsComplete && identity.ScenarioController == scenarioController)
                {
                    return true;
                }

                identity = default;
                return false;
            }

            if (!startFrame.TryConsume())
            {
                identity = default;
                return false;
            }

            identity = startFrame.Identity;
            if (!identity.IsComplete
                || identity.ScenarioController != scenarioController)
            {
                identity = default;
                return false;
            }

            _active[key] = identity;
            return true;
        }
    }

    /// <summary>
    /// Removes a terminal or faulted state-machine entry. Suspended entries are
    /// intentionally retained until their next MoveNext, but ClearAll removes
    /// them immediately on disable, replacement, or unload. A bound Scenario
    /// Leave is retained by the runtime only until the matching PostRead reaches
    /// its terminal/fault boundary; pending or mismatched Leave still calls
    /// ClearAll immediately.
    /// </summary>
    public bool End(
        IntPtr stateMachine,
        ScenarioExecutionKind kind,
        bool terminalOrFaulted)
    {
        if (stateMachine == IntPtr.Zero || !terminalOrFaulted)
        {
            return false;
        }

        lock (_gate)
        {
            return _active.Remove(new ExecutionKey(stateMachine, kind));
        }
    }

    /// <summary>
    /// Clears a wrapper frame on normal completion or wrapper exception and
    /// releases the registry's bounded reference to it.
    /// </summary>
    public void ReleaseStartFrame(ScenarioExecutionStartFrame? startFrame)
    {
        if (startFrame is null)
        {
            return;
        }

        lock (_gate)
        {
            startFrame.Clear();
            _startFrames.Remove(startFrame);
        }
    }

    public void ClearAll()
    {
        lock (_gate)
        {
            _active.Clear();
            foreach (var startFrame in _startFrames)
            {
                startFrame.Clear();
            }

            _startFrames.Clear();
        }
    }
}
