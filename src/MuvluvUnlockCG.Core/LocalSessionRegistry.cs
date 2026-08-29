using System;
using System.Collections.Generic;

namespace MuvluvUnlockCG.Core;

public enum SessionState
{
    Empty,
    Pending,
    Bound,
}

public readonly record struct PlaybackIdentity(
    long EpisodeId,
    long SceneId,
    IntPtr EpisodeController,
    IntPtr ScenarioController,
    long Generation)
{
    public bool IsComplete =>
        EpisodeId > 0
        && SceneId > 0
        && EpisodeController != IntPtr.Zero
        && ScenarioController != IntPtr.Zero
        && Generation > 0;
}

public readonly record struct SceneContentLease(
    long SceneId,
    IntPtr EpisodeServiceController,
    long Generation)
{
    public bool IsComplete =>
        SceneId > 0
        && EpisodeServiceController != IntPtr.Zero
        && Generation > 0;
}

public sealed class PendingLocalSession
{
    private readonly IReadOnlyList<long> _expectedSceneIds;

    internal PendingLocalSession(
        long episodeId,
        IReadOnlyList<long> expectedSceneIds,
        IntPtr episodeController,
        IntPtr episodeServiceController,
        IntPtr apiClientController,
        long generation,
        BypassReason reason)
    {
        EpisodeId = episodeId;
        _expectedSceneIds = Array.AsReadOnly(LocalSessionRegistry.CopySceneIds(expectedSceneIds));
        EpisodeController = episodeController;
        EpisodeServiceController = episodeServiceController;
        ApiClientController = apiClientController;
        Generation = generation;
        Reason = reason;
    }

    public long EpisodeId { get; }
    public IReadOnlyList<long> ExpectedSceneIds => _expectedSceneIds;
    public IntPtr EpisodeController { get; }
    public IntPtr EpisodeServiceController { get; }
    public IntPtr ApiClientController { get; }
    public long Generation { get; }
    public BypassReason Reason { get; }

    internal bool HasExpectedScene(long sceneId)
    {
        for (var i = 0; i < _expectedSceneIds.Count; i++)
        {
            if (_expectedSceneIds[i] == sceneId)
            {
                return true;
            }
        }

        return false;
    }

}

public sealed class BoundLocalSession
{
    private readonly IReadOnlyList<long> _expectedSceneIds;

    internal BoundLocalSession(
        long episodeId,
        long sceneId,
        IReadOnlyList<long> expectedSceneIds,
        IntPtr episodeController,
        IntPtr scenarioController,
        IntPtr episodeServiceController,
        IntPtr apiClientController,
        long generation,
        BypassReason reason)
    {
        EpisodeId = episodeId;
        SceneId = sceneId;
        _expectedSceneIds = Array.AsReadOnly(LocalSessionRegistry.CopySceneIds(expectedSceneIds));
        EpisodeController = episodeController;
        ScenarioController = scenarioController;
        EpisodeServiceController = episodeServiceController;
        ApiClientController = apiClientController;
        Generation = generation;
        Reason = reason;
    }

    public long EpisodeId { get; }
    public long SceneId { get; }
    public IReadOnlyList<long> ExpectedSceneIds => _expectedSceneIds;
    public IntPtr EpisodeController { get; }
    public IntPtr ScenarioController { get; }
    public IntPtr EpisodeServiceController { get; }
    public IntPtr ApiClientController { get; }
    public long Generation { get; }
    public BypassReason Reason { get; }

    public PlaybackIdentity Identity => new(
        EpisodeId,
        SceneId,
        EpisodeController,
        ScenarioController,
        Generation);

    internal bool HasExpectedScene(long sceneId)
    {
        for (var i = 0; i < _expectedSceneIds.Count; i++)
        {
            if (_expectedSceneIds[i] == sceneId)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// The one fail-open registry shared by all runtime hooks. A pending session
/// is deliberately not a business-call match; only a bound session with the
/// exact generation, Scene, and controller identity can suppress a call.
/// </summary>
public sealed class LocalSessionRegistry
{
    private readonly object _gate = new object();
    private PendingLocalSession? _pending;
    private BoundLocalSession? _bound;
    private long _lastGeneration;

    public SessionState State
    {
        get
        {
            lock (_gate)
            {
                return _bound is not null
                    ? SessionState.Bound
                    : _pending is not null
                        ? SessionState.Pending
                        : SessionState.Empty;
            }
        }
    }

    public PendingLocalSession? Pending
    {
        get
        {
            lock (_gate)
            {
                return _pending;
            }
        }
    }

    public BoundLocalSession? Bound
    {
        get
        {
            lock (_gate)
            {
                return _bound;
            }
        }
    }

    public bool BeginPending(
        long episodeId,
        IReadOnlyList<long> expectedSceneIds,
        IntPtr episodeController,
        BypassReason reason,
        out PendingLocalSession session,
        IntPtr episodeServiceController = default,
        IntPtr apiClientController = default)
    {
        session = null!;
        // Full ISIL callers invoke d60/d61.MoveNext directly at
        // evidence/decomp/full-isil/IsilDump/GameUi/Assets/GameUi/Episode/
        // EpisodeComponent_NestedType__SelectCharacterCell_d__39.txt:505-510
        // and evidence/decomp/full-isil/IsilDump/GameUi/Assets/GameUi/Episode/
        // EpisodeComponent_NestedType___ProcessOnCreate_b__32_20_d.txt:351-356.
        // PostRead d16 and branch d103 still require these exact service/API
        // identities before a Character/Memory LocalBypass session is valid.
        // Main/Event MoveToScenario state machines do not carry either service
        // object (persisted interop MainEpisodeController.cs:216-314 and
        // EventChapterController.cs:936-1035); their pending session is
        // deliberately allowed to attach those identities at Scenario.Refresh.
        if (episodeId <= 0
            || episodeController == IntPtr.Zero
            || expectedSceneIds is null
            || !IsLocalBypassReason(reason))
        {
            return false;
        }

        if (reason != BypassReason.LocallyAvailableStory
            && (episodeServiceController == IntPtr.Zero || apiClientController == IntPtr.Zero))
        {
            return false;
        }

        var normalizedSceneIds = NormalizeSceneIds(expectedSceneIds);
        if (normalizedSceneIds.Count == 0)
        {
            return false;
        }

        lock (_gate)
        {
            _lastGeneration = NextGeneration(_lastGeneration);
            _pending = new PendingLocalSession(
                episodeId,
                normalizedSceneIds,
                episodeController,
                episodeServiceController,
                apiClientController,
                _lastGeneration,
                reason);
            _bound = null;
            session = _pending;
            return true;
        }
    }

    public bool TryBind(
        long episodeId,
        long sceneId,
        IntPtr episodeController,
        IntPtr scenarioController,
        out BoundLocalSession session,
        IntPtr episodeServiceController = default,
        IntPtr apiClientController = default)
    {
        session = null!;
        if (episodeId <= 0
            || sceneId <= 0
            || episodeController == IntPtr.Zero
            || scenarioController == IntPtr.Zero)
        {
            return false;
        }

        lock (_gate)
        {
            if (_pending is not null)
            {
                if (_pending.EpisodeId != episodeId
                    || _pending.EpisodeController != episodeController
                    || !_pending.HasExpectedScene(sceneId)
                    || !MatchesOptionalIdentity(
                        _pending.EpisodeServiceController,
                        episodeServiceController)
                    || !MatchesOptionalIdentity(
                        _pending.ApiClientController,
                        apiClientController))
                {
                    return false;
                }

                var pendingService = PreferKnownIdentity(
                    _pending.EpisodeServiceController,
                    episodeServiceController);
                var pendingApi = PreferKnownIdentity(
                    _pending.ApiClientController,
                    apiClientController);
                _bound = new BoundLocalSession(
                    _pending.EpisodeId,
                    sceneId,
                    _pending.ExpectedSceneIds,
                    _pending.EpisodeController,
                    scenarioController,
                    pendingService,
                    pendingApi,
                    _pending.Generation,
                    _pending.Reason);
                _pending = null;
                session = _bound;
                return true;
            }

            // A single local generation can span multiple SceneMaster rows.
            // Refresh is called after each native frame assignment (selected
            // Refresh d__76), so retain the exact generation/controller while
            // advancing only to another Master-derived expected Scene ID.
            if (_bound is null
                || _bound.EpisodeId != episodeId
                || _bound.EpisodeController != episodeController
                || _bound.ScenarioController != scenarioController
                || !_bound.HasExpectedScene(sceneId)
                || !MatchesOptionalIdentity(
                    _bound.EpisodeServiceController,
                    episodeServiceController)
                || !MatchesOptionalIdentity(
                    _bound.ApiClientController,
                    apiClientController))
            {
                return false;
            }

            var boundService = PreferKnownIdentity(
                _bound.EpisodeServiceController,
                episodeServiceController);
            var boundApi = PreferKnownIdentity(
                _bound.ApiClientController,
                apiClientController);
            _bound = new BoundLocalSession(
                _bound.EpisodeId,
                sceneId,
                _bound.ExpectedSceneIds,
                _bound.EpisodeController,
                _bound.ScenarioController,
                boundService,
                boundApi,
                _bound.Generation,
                _bound.Reason);
            session = _bound;
            return true;
        }
    }

    public bool TryMatch(PlaybackIdentity identity)
    {
        if (!identity.IsComplete)
        {
            return false;
        }

        lock (_gate)
        {
            return MatchesBound(_bound, identity);
        }
    }

    /// <summary>
    /// Matches a branch call only when its caller supplied the complete
    /// Scenario-scoped identity captured at Refresh, plus the exact API object
    /// used by that bound generation. A scene/API pair alone is not provenance.
    /// </summary>
    public bool TryMatchBranch(PlaybackIdentity identity, IntPtr apiClientController)
    {
        if (!identity.IsComplete || apiClientController == IntPtr.Zero)
        {
            return false;
        }

        lock (_gate)
        {
            return MatchesBound(_bound, identity)
                && _bound!.ApiClientController != IntPtr.Zero
                && _bound.ApiClientController == apiClientController;
        }
    }

    /// <summary>
    /// Matches a PostRead call only with complete Scenario-scoped provenance and
    /// the exact EpisodeService object captured for that generation.
    /// </summary>
    public bool TryMatchEpisodeService(PlaybackIdentity identity, IntPtr episodeServiceController)
    {
        if (!identity.IsComplete || episodeServiceController == IntPtr.Zero)
        {
            return false;
        }

        lock (_gate)
        {
            return MatchesBound(_bound, identity)
                && _bound!.EpisodeServiceController != IntPtr.Zero
                && _bound.EpisodeServiceController == episodeServiceController;
        }
    }

    /// <summary>
    /// Content is needed before a ScenarioController exists, so its safe seam is
    /// the active generation's exact EpisodeService plus a Master-derived Scene.
    /// Unlike business-call matching, both pending and bound states are valid.
    /// </summary>
    public bool TryBeginSceneContent(
        long sceneId,
        IntPtr episodeServiceController,
        out SceneContentLease lease)
    {
        lease = default;
        if (sceneId <= 0 || episodeServiceController == IntPtr.Zero)
        {
            return false;
        }

        lock (_gate)
        {
            // Main/Event entry state machines do not expose EpisodeService. The
            // first high-level SceneFrame request is the earliest persisted seam
            // that does, so attach that exact object before Scenario.Refresh.
            if (_pending is not null
                && _pending.Reason == BypassReason.LocallyAvailableStory
                && _pending.EpisodeServiceController == IntPtr.Zero
                && _pending.HasExpectedScene(sceneId))
            {
                _pending = new PendingLocalSession(
                    _pending.EpisodeId,
                    _pending.ExpectedSceneIds,
                    _pending.EpisodeController,
                    episodeServiceController,
                    _pending.ApiClientController,
                    _pending.Generation,
                    _pending.Reason);
            }

            var generation = _pending is not null
                && IsLocalBypassReason(_pending.Reason)
                && _pending.EpisodeServiceController == episodeServiceController
                && _pending.HasExpectedScene(sceneId)
                    ? _pending.Generation
                    : _bound is not null
                        && IsLocalBypassReason(_bound.Reason)
                        && _bound.EpisodeServiceController == episodeServiceController
                        && _bound.HasExpectedScene(sceneId)
                            ? _bound.Generation
                            : 0;
            if (generation <= 0)
            {
                return false;
            }

            lease = new SceneContentLease(sceneId, episodeServiceController, generation);
            return true;
        }
    }

    public bool TryConfirmSceneContent(SceneContentLease lease)
    {
        if (!lease.IsComplete)
        {
            return false;
        }

        lock (_gate)
        {
            return (_pending is not null
                    && IsLocalBypassReason(_pending.Reason)
                    && _pending.Generation == lease.Generation
                    && _pending.EpisodeServiceController == lease.EpisodeServiceController
                    && _pending.HasExpectedScene(lease.SceneId))
                || (_bound is not null
                    && IsLocalBypassReason(_bound.Reason)
                    && _bound.Generation == lease.Generation
                    && _bound.EpisodeServiceController == lease.EpisodeServiceController
                    && _bound.HasExpectedScene(lease.SceneId));
        }
    }

    public bool TryGetBoundIdentity(out PlaybackIdentity identity)
    {
        lock (_gate)
        {
            if (_bound is not null && _bound.Identity.IsComplete)
            {
                identity = _bound.Identity;
                return true;
            }
        }

        identity = default;
        return false;
    }

    internal static long[] CopySceneIds(IReadOnlyList<long> source)
    {
        var copy = new long[source.Count];
        for (var i = 0; i < source.Count; i++)
        {
            copy[i] = source[i];
        }

        return copy;
    }

    private static bool MatchesBound(BoundLocalSession? bound, PlaybackIdentity identity)
    {
        return bound is not null
            && bound.EpisodeId == identity.EpisodeId
            && bound.SceneId == identity.SceneId
            && bound.EpisodeController == identity.EpisodeController
            && bound.ScenarioController == identity.ScenarioController
            && bound.Generation == identity.Generation;
    }

    public void ClearForEpisode(long episodeId)
    {
        if (episodeId <= 0)
        {
            return;
        }

        lock (_gate)
        {
            if (_pending?.EpisodeId == episodeId || _bound?.EpisodeId == episodeId)
            {
                _pending = null;
                _bound = null;
            }
        }
    }

    public void ClearForController(IntPtr episodeController)
    {
        if (episodeController == IntPtr.Zero)
        {
            return;
        }

        lock (_gate)
        {
            if (_pending?.EpisodeController == episodeController || _bound?.EpisodeController == episodeController)
            {
                _pending = null;
                _bound = null;
            }
        }
    }

    public void ClearForScenarioController(IntPtr scenarioController)
    {
        if (scenarioController == IntPtr.Zero)
        {
            return;
        }

        lock (_gate)
        {
            if (_bound?.ScenarioController == scenarioController)
            {
                _pending = null;
                _bound = null;
            }
        }
    }

    public void ClearAll()
    {
        lock (_gate)
        {
            _pending = null;
            _bound = null;
        }
    }

    private static List<long> NormalizeSceneIds(IReadOnlyList<long> sceneIds)
    {
        var normalized = new List<long>(sceneIds.Count);
        for (var i = 0; i < sceneIds.Count; i++)
        {
            var sceneId = sceneIds[i];
            if (sceneId <= 0 || normalized.Contains(sceneId))
            {
                continue;
            }

            normalized.Add(sceneId);
        }

        return normalized;
    }

    private static long NextGeneration(long previous)
    {
        return previous == long.MaxValue ? 1 : previous + 1;
    }

    private static bool IsLocalBypassReason(BypassReason reason)
    {
        return reason == BypassReason.UnownedCharacter
            || reason == BypassReason.AffectionBelowRequirement
            || reason == BypassReason.UnownedMemory
            || reason == BypassReason.LocallyAvailableStory;
    }

    private static bool MatchesOptionalIdentity(IntPtr known, IntPtr observed)
    {
        return known == IntPtr.Zero
            || observed == IntPtr.Zero
            || known == observed;
    }

    private static IntPtr PreferKnownIdentity(IntPtr known, IntPtr observed)
    {
        return known != IntPtr.Zero ? known : observed;
    }
}
