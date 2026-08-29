using System;
using System.Collections.Generic;
using Assets.Api.Client;
using Assets.Api.MemoryDB;
using Assets.GameUi.Episode;
using Assets.GameUi.Episode.EventChapter;
using Assets.GameUi.Episode.MainChapter;
using Assets.GameUi.Scenario;
using Assets.GameUi.Service;
using BepInEx.Logging;
using Cysharp.Threading.Tasks;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MuvluvUnlockCG.Core;

namespace MuvluvUnlockCG;

internal static class MuvluvUnlockRuntime
{
    internal const string DiagnosticPrefix = "[DEBUG-MUCG-b6c2]";

    private static readonly object Gate = new object();
    private static readonly LocalSessionRegistry Sessions = new LocalSessionRegistry();
    private static readonly CellVisibilityRegistry VisibleCells = new CellVisibilityRegistry();
    private static readonly Dictionary<CellIdentity, CharacterPresentationSnapshot> CharacterPresentations = new Dictionary<CellIdentity, CharacterPresentationSnapshot>();
    private static readonly Dictionary<CellIdentity, EventUnlockPresentationSnapshot> EventUnlockPresentations = new Dictionary<CellIdentity, EventUnlockPresentationSnapshot>();
    private static readonly Dictionary<IntPtr, LockMessageSnapshot> TemporaryLockMessages = new Dictionary<IntPtr, LockMessageSnapshot>();
    private static readonly Dictionary<CatalogKey, EligibilityDecision> CatalogDecisions = new Dictionary<CatalogKey, EligibilityDecision>();
    private static readonly ScenarioExecutionProvenanceRegistry ExecutionProvenance = new ScenarioExecutionProvenanceRegistry();
    // Persisted ScenarioController evidence shows Start followed immediately by
    // the first MoveNext (selected ScenarioController.txt:725 and :847), and
    // the business calls occur synchronously inside that MoveNext. ThreadStatic
    // scopes therefore cover exactly those call stacks without flowing a stale
    // frame through a managed await ExecutionContext.
    [ThreadStatic]
    private static StartFrameScope ActiveStartFrame;
    [ThreadStatic]
    private static ExecutionFrame ActiveExecution;
    private static Func<bool> _isEnabled;
    private static ManualLogSource _log;
    private static SceneFrameSource _sceneFrames;
    private static bool? _lastEnabled;
    private static IntPtr _activeScenarioController;
    private static DeferredLeave? _deferredLeave;

    private readonly record struct ExecutionKey(IntPtr StateMachine, ScenarioExecutionKind Kind);

    private readonly record struct DeferredLeave(PlaybackIdentity Identity, bool PostReadStarted);

    private readonly record struct ExecutionContext(bool HasIdentity, PlaybackIdentity Identity);

    private sealed class ExecutionFrame
    {
        public ExecutionFrame(ExecutionKey key, ExecutionContext context, ExecutionFrame previous)
        {
            Key = key;
            Context = context;
            Previous = previous;
        }

        public ExecutionKey Key { get; }
        public ExecutionContext Context { get; }
        public ExecutionFrame Previous { get; }
    }

    private sealed class StartFrameScope
    {
        public StartFrameScope(ScenarioExecutionStartFrame frame, StartFrameScope previous)
        {
            Frame = frame;
            Previous = previous;
        }

        public ScenarioExecutionStartFrame Frame { get; }
        public StartFrameScope Previous { get; }
    }

    public static void Initialize(
        Func<bool> isEnabled,
        ManualLogSource log,
        string sceneDataRoot,
        string remoteBaseUrl = null)
    {
        // Keep the registry lock before the catalog lock. Entry/leave cleanup
        // follows this order (selected EpisodeController d__60/d__61 and
        // ScenarioController <Leave>d__85), so initialization cannot deadlock
        // with a lifecycle callback that is already clearing a session.
        ClearAll("initialize");
        lock (Gate)
        {
            _isEnabled = isEnabled;
            _log = log;
            _sceneFrames = new SceneFrameSource(remoteBaseUrl, sceneDataRoot);
            _lastEnabled = null;
            CatalogDecisions.Clear();
            _activeScenarioController = IntPtr.Zero;
            _deferredLeave = null;
        }

        SafeLogInfo($"runtime initialized enabled={isEnabled is not null} sceneRootConfigured={!string.IsNullOrWhiteSpace(sceneDataRoot)} remoteConfigured={_sceneFrames.RemoteConfigured}");
    }

    public static bool ClearAll(string reason = "unspecified")
    {
        return ClearState(reason, clearCatalogDecisions: true);
    }

    private static bool ClearState(string reason, bool clearCatalogDecisions)
    {
        // Session lifecycle hooks and catalog capture use the same lock order:
        // registry first, catalog second. The exact replacement/leave seams are
        // selected EpisodeController d__60/d__61 and ScenarioController
        // <Leave>d__85.
        var before = SafeDescribeState();
        Sessions.ClearAll();
        var visibilityRestored = RestoreAllCells();
        lock (Gate)
        {
            if (clearCatalogDecisions)
            {
                CatalogDecisions.Clear();
            }

            _activeScenarioController = IntPtr.Zero;
            _deferredLeave = null;
        }
        // d103/d104 state-machine entries are bounded active handoffs, not
        // tombstones. ClearAll must drop suspended native keys immediately so
        // an old continuation cannot borrow a later same-ID generation.
        ExecutionProvenance.ClearAll();
        ClearActiveStartFrames();
        ActiveExecution = null;
        var success = visibilityRestored
            && VisibleCells.Count == 0
            && EventUnlockPresentations.Count == 0
            && TemporaryLockMessages.Count == 0;
        SafeLogInfo($"state clear reason={reason} before={before} after={SafeDescribeState()} visibilityRestored={visibilityRestored} catalogDecisionsCleared={clearCatalogDecisions}");

        return success;
    }

    public static bool Enabled
    {
        get
        {
            bool enabled;
            try
            {
                enabled = _isEnabled is not null && _isEnabled();
            }
            catch (Exception exception)
            {
                SafeLogException("configuration read failed; using native flow", exception);
                enabled = false;
            }

            var changed = false;
            lock (Gate)
            {
                if (_lastEnabled.HasValue && _lastEnabled.Value != enabled)
                {
                    changed = true;
                }

                _lastEnabled = enabled;
            }

            if (changed)
            {
                SafeLogInfo($"enabled changed current={enabled}; clearing active local state");
            }

            // A toggle in either direction invalidates every asynchronous local
            // identity. Calling ClearAll while disabled also makes the guarantee
            // independent of which patched method observes the toggle first.
            if (!enabled || changed)
            {
                ClearAll(!enabled ? "disabled" : "enabled-toggle");
            }

            return enabled;
        }
    }

    public static void OnCharacterCellBuilt(
        EpisodeController controller,
        long episodeMasterId,
        CharacterEpisodeCell.CharacterEpisodeCellArgs args)
    {
        var enabled = Enabled;
        SafeLogInfo($"character-cell factory invoked episode={episodeMasterId} enabled={enabled} controller={FormatPointer(SafePointer(controller))} argsPresent={args is not null}");
        if (!enabled)
        {
            SafeLogInfo($"character-cell episode={episodeMasterId} skip=disabled");
            return;
        }

        if (controller is null || args is null)
        {
            // A missing wrapper cannot be a valid local continuation. ClearAll
            // restores every other captured cell through the strong lease.
            SafeLogWarning($"character-cell episode={episodeMasterId} skip=incomplete-wrapper native-row-retained");
            ClearAll("character-cell-incomplete-wrapper");
            return;
        }

        try
        {
            var cellKey = new CellIdentity(Pointer(controller), episodeMasterId);
            if (!RestoreCell(cellKey))
            {
                SafeLogWarning($"character-cell episode={episodeMasterId} skip=previous-visibility-restore-failed native-row-retained");
                ClearAll("character-cell-restore-failure");
                return;
            }

            var originalViewable = args.Viewable;
            var originalHasCharacter = args.HasCharacter;
            var originalAffectionLevel = args.AffectionLevel;
            var requiredAffectionLevel = args.UnlockConditionAffectionLevel;
            var eligibility = EligibilityPolicy.ForCharacter(new CharacterEligibilityFacts(
                episodeMasterId,
                originalHasCharacter,
                originalAffectionLevel,
                requiredAffectionLevel));
            var locallyAvailable = true;
            var sceneIdentityAvailable = true;
            if (eligibility.IsLocalBypass)
            {
                sceneIdentityAvailable = TryResolveSceneIds(
                    controller.memoryDB,
                    episodeMasterId,
                    out var sceneIds);
                locallyAvailable = sceneIdentityAvailable && HasAvailableScene(sceneIds);
            }

            var decision = EligibilityPolicy.ForCharacterCell(new CharacterCellFacts(
                episodeMasterId,
                originalViewable,
                originalHasCharacter,
                originalAffectionLevel,
                requiredAffectionLevel,
                locallyAvailable));
            // EpisodeMaster nullable ID getters are not a safe diagnostic seam:
            // the generated wrappers can throw or expose transient garbage. The
            // cell itself already contains every Character eligibility fact.
            SafeLogInfo($"character-cell episode={episodeMasterId} originalViewable={originalViewable} ownedCharacter={originalHasCharacter} affection={originalAffectionLevel} required={requiredAffectionLevel} sceneIdentityAvailable={sceneIdentityAvailable} locallyAvailable={locallyAvailable} decision={decision.Eligibility.Mode}/{decision.Eligibility.Reason}");
            Capture(controller, episodeMasterId, args, originalViewable, decision.Eligibility);
            if (decision.Eligibility.IsLocalBypass)
            {
                CaptureCharacterPresentation(
                    cellKey,
                    args,
                    originalHasCharacter,
                    originalAffectionLevel,
                    requiredAffectionLevel);
            }

            SafeLogInfo($"character-cell episode={episodeMasterId} finalViewable={args.Viewable} finalHasCharacter={args.HasCharacter} finalAffection={args.AffectionLevel} catalogDecision={decision.Eligibility.Mode}/{decision.Eligibility.Reason}");
        }
        catch (Exception exception)
        {
            // The b54_0 individual Character cell seam is persisted evidence;
            // an adapter error must not leave an older local generation alive.
            ClearAll("character-cell-exception");
            SafeLogException($"character cell policy failed for episode {episodeMasterId}; native row retained", exception);
        }
    }

    public static void OnMemoryCellBuilt(
        EpisodeController controller,
        long episodeMasterId,
        MemoryEpisodeCell.MemoryEpisodeCellArgs args)
    {
        var enabled = Enabled;
        SafeLogInfo($"memory-cell factory invoked episode={episodeMasterId} enabled={enabled} controller={FormatPointer(SafePointer(controller))} argsPresent={args is not null}");
        if (!enabled)
        {
            SafeLogInfo($"memory-cell episode={episodeMasterId} skip=disabled");
            return;
        }

        if (controller is null || args is null)
        {
            SafeLogWarning($"memory-cell episode={episodeMasterId} skip=incomplete-wrapper native-row-retained");
            ClearAll("memory-cell-incomplete-wrapper");
            return;
        }

        try
        {
            var cellKey = new CellIdentity(Pointer(controller), episodeMasterId);
            if (!RestoreCell(cellKey))
            {
                SafeLogWarning($"memory-cell episode={episodeMasterId} skip=previous-visibility-restore-failed native-row-retained");
                ClearAll("memory-cell-restore-failure");
                return;
            }

            var originalViewable = args.Viewable;
            if (!TryReadMemoryRelease(controller.memoryDB, episodeMasterId, out var isReleased, out var hasMemoryIdentity))
            {
                // The native release filter remains authoritative if the relation cannot be read.
                var incomplete = new EligibilityDecision(PlaybackMode.Normal, BypassReason.IncompleteEvidence);
                SafeLogWarning($"memory-cell episode={episodeMasterId} memoryRelation=unavailable originalViewable={originalViewable} releaseRead=failed decision={incomplete.Mode}/{incomplete.Reason} skip=native-release-filter");
                Capture(controller, episodeMasterId, args, originalViewable, incomplete);
                SafeLogInfo($"memory-cell episode={episodeMasterId} finalViewable={args.Viewable} includeInCatalog=false catalogDecision={incomplete.Mode}/{incomplete.Reason}");
                return;
            }

            var eligibility = EligibilityPolicy.ForMemory(new MemoryEligibilityFacts(
                episodeMasterId,
                originalViewable,
                isReleased,
                hasMemoryIdentity));
            var locallyAvailable = true;
            var sceneIdentityAvailable = true;
            if (eligibility.IsLocalBypass)
            {
                sceneIdentityAvailable = TryResolveSceneIds(
                    controller.memoryDB,
                    episodeMasterId,
                    out var sceneIds);
                locallyAvailable = sceneIdentityAvailable && HasAvailableScene(sceneIds);
            }

            var decision = EligibilityPolicy.ForMemoryCell(new MemoryCellFacts(
                episodeMasterId,
                originalViewable,
                isReleased,
                hasMemoryIdentity,
                locallyAvailable));
            SafeLogInfo($"memory-cell episode={episodeMasterId} originalViewable={originalViewable} originalOwned={originalViewable} released={isReleased} memoryRelation={hasMemoryIdentity} sceneIdentityAvailable={sceneIdentityAvailable} locallyAvailable={locallyAvailable} includeInCatalog={decision.IncludeInCatalog} decision={decision.Eligibility.Mode}/{decision.Eligibility.Reason}");
            Capture(controller, episodeMasterId, args, originalViewable, decision.Eligibility);
            SafeLogInfo($"memory-cell episode={episodeMasterId} finalViewable={args.Viewable} includeInCatalog={decision.IncludeInCatalog} catalogDecision={decision.Eligibility.Mode}/{decision.Eligibility.Reason}");
        }
        catch (Exception exception)
        {
            // The b56_0/b56_1 release and cell seams are persisted evidence;
            // an adapter error must not leave an older local generation alive.
            ClearAll("memory-cell-exception");
            SafeLogException($"memory cell policy failed for episode {episodeMasterId}; native row retained", exception);
        }
    }

    public static void OnMainChapterCellBuilt(
        EpisodeController controller,
        ChapterGroupMaster chapterGroup,
        MainChapterCell.MainChapterCellArgs args)
    {
        if (!Enabled || controller is null || chapterGroup is null || args is null || args.Viewable)
        {
            return;
        }

        try
        {
            if (HasAvailableChapterGroup(controller.memoryDB, chapterGroup.Id))
            {
                args.Viewable = true;
                SafeLogInfo($"main-chapter-group group={chapterGroup.Id} nativeViewable=false localAvailable=true finalViewable=true");
            }
        }
        catch (Exception exception)
        {
            SafeLogException("main chapter-group presentation failed; native lock retained", exception);
        }
    }

    public static void OnMainChapterButtonBuilt(
        EpisodeController controller,
        ChapterMaster chapter,
        EpisodeController.MainChapterSelectButtonViewModel model)
    {
        if (!Enabled || controller is null || chapter is null || model is null || !model.IsLock)
        {
            return;
        }

        try
        {
            if (HasAvailableChapter(controller.memoryDB, chapter.Id))
            {
                model.IsLock = false;
                SafeLogInfo($"main-chapter-button chapter={chapter.Id} nativeLocked=true localAvailable=true finalLocked=false");
            }
        }
        catch (Exception exception)
        {
            SafeLogException("main chapter-button presentation failed; native lock retained", exception);
        }
    }

    public static void OnEventChapterCellBuilt(
        EpisodeController controller,
        long chapterMasterId,
        Assets.GameUi.Episode.EventEpisodeCell.EventEpisodeCellArgs args)
    {
        if (!Enabled || controller is null || chapterMasterId <= 0 || args is null)
        {
            return;
        }

        try
        {
            var identity = new CellIdentity(Pointer(controller), chapterMasterId);
            if (!RestoreCell(identity))
            {
                SafeLogWarning($"event-chapter chapter={chapterMasterId} skip=previous-presentation-restore-failed native-row-retained");
                ClearAll("event-chapter-restore-failure");
                return;
            }

            if (args.Viewable)
            {
                return;
            }

            if (!HasChapterSceneIdentity(controller.memoryDB, chapterMasterId))
            {
                return;
            }

            var originalUnlockArgs = args.EventEpisodeUnlockArgs;
            var decision = new EligibilityDecision(PlaybackMode.LocalBypass, BypassReason.LocallyAvailableStory);
            Capture(controller, chapterMasterId, args, args.Viewable, decision);
            lock (Gate)
            {
                EventUnlockPresentations[identity] = new EventUnlockPresentationSnapshot(args, originalUnlockArgs);
            }

            try
            {
                // EpisodeController.SelectEventCell d58 checks this nested
                // value before opening EventChapterUnlockWindow
                // (persisted ISIL :978-1025). Null is the native chapter
                // navigation branch; promoting CanUnlock would instead expose
                // the account-backed unlock action.
                args.EventEpisodeUnlockArgs = null;
            }
            catch
            {
                RestoreEventUnlockPresentation(identity);
                RestoreCell(identity);
                throw;
            }

            SafeLogInfo($"event-chapter chapter={chapterMasterId} nativeViewable=false runtimeSceneIdentity=true contentSource=local-or-native finalViewable={args.Viewable} unlockArgsCleared=true");
        }
        catch (Exception exception)
        {
            ClearAll("event-chapter-exception");
            SafeLogException("event chapter presentation failed; native lock retained", exception);
        }
    }

    /// <summary>
    /// EventEpisodeCell.Apply d17 consumes both the inherited Viewable field and
    /// the nested EventEpisodeUnlockArgs relation (persisted ISIL
    /// EventEpisodeCell_NestedType__Apply_d__17.txt:930-1025). The factory can
    /// run after the first Apply call, so re-apply the exact captured local
    /// presentation at that consumer seam. Normal rows have no visibility
    /// lease and are left untouched.
    /// </summary>
    public static void OnEventCatalogApplyStateMachine(
        Assets.GameUi.Episode.EventEpisodeCell._Apply_d__17 stateMachine)
    {
        try
        {
            if (!Enabled || stateMachine is null)
            {
                return;
            }

            var args = stateMachine.args;
            if (!TryGetLocalCellIdentity(args, out var identity))
            {
                return;
            }

            args.Viewable = true;
            args.EventEpisodeUnlockArgs = null;
            SafeLogInfo($"event-cell Apply episode={identity.EpisodeId} localPresentation=true viewable={args.Viewable} unlockArgsCleared=true");
        }
        catch (Exception exception)
        {
            OnHookFailure(stateMachine, exception);
            SafeLogException("event cell Apply presentation failed; native consumer retained", exception);
        }
    }

    /// <summary>
    /// Character ApplySyncPart d32 is the persisted lock-container consumer
    /// (CharacterEpisodeCell_NestedType__ApplySyncPart_d__32.txt:1024-1142).
    /// Its state machine carries a distinct args field, so restore the captured
    /// local presentation immediately before every native resume. This changes
    /// only a LocalBypass args object; native rows retain all ownership and
    /// affection facts.
    /// </summary>
    public static void OnCharacterApplySyncPartStateMachine(
        CharacterEpisodeCell._ApplySyncPart_d__32 stateMachine)
    {
        try
        {
            if (!Enabled || stateMachine is null)
            {
                return;
            }

            var args = stateMachine.args;
            if (!TryGetCharacterPresentation(args, out var snapshot))
            {
                return;
            }

            // d32 checks the inherited Viewable bit before it reaches either
            // lock-container branch (full ISIL :1024-1059). The factory
            // promotion can be overwritten by the native refresh, so restore
            // that exact LocalBypass bit at the consumer as well.
            args.Viewable = true;
            SetCharacterPresentation(args, true, snapshot.LocalAffectionLevel);
            SafeLogInfo($"character-cell ApplySyncPart episode={snapshot.Identity.EpisodeId} localPresentation=true viewable={args.Viewable} hasCharacter={args.HasCharacter} affection={args.AffectionLevel}");
        }
        catch (Exception exception)
        {
            OnHookFailure(stateMachine, exception);
            SafeLogException("character ApplySyncPart presentation failed; native consumer retained", exception);
        }
    }

    /// <summary>
    /// Memory Apply d16 consumes BaseEpisodeCellArgs.Viewable at its lock-image
    /// branch (MemoryEpisodeCell_NestedType__Apply_d__16.txt:423-460). Reapply
    /// only the exact captured LocalBypass lease before that native consumer.
    /// </summary>
    public static void OnMemoryApplyStateMachine(
        MemoryEpisodeCell._Apply_d__16 stateMachine)
    {
        try
        {
            if (!Enabled || stateMachine is null)
            {
                return;
            }

            var args = stateMachine.args;
            if (!TryGetLocalCellIdentity(args, out var identity))
            {
                return;
            }

            args.Viewable = true;
            SafeLogInfo($"memory-cell Apply episode={identity.EpisodeId} localPresentation=true viewable={args.Viewable}");
        }
        catch (Exception exception)
        {
            OnHookFailure(stateMachine, exception);
            SafeLogException("memory Apply presentation failed; native consumer retained", exception);
        }
    }

    /// <summary>
    /// MainEpisodeController b15_1 and EventChapterController b22_0 both build
    /// BaseEpisodeCellArgs-derived rows. Their Apply state machines consume the
    /// inherited Viewable bit directly (MainEpisodeCell Apply d15:517-522 and
    /// :597-602; EventEpisodeCell Apply has the same generated branch), so the
    /// exact factory result is the narrow presentation seam for story/event
    /// masks. Main rows require local content; Event rows may retain the native
    /// SceneFrame acquisition path after runtime episode/scene identity is
    /// proven. The controller memory DB remains the only identity source.
    /// </summary>
    public static void OnMainStoryCellBuilt(
        MainEpisodeController controller,
        long episodeMasterId,
        EpisodeComponent.BaseEpisodeCellArgs args)
    {
        IMemoryDB memoryDB = null;
        try
        {
            memoryDB = controller is null ? null : controller.memoryDB;
        }
        catch (Exception exception)
        {
            OnHookFailure(controller, exception);
            SafeLogException("main story-cell memory-db inspection failed; native row retained", exception);
            return;
        }

        OnStoryCellBuilt(
            controller,
            episodeMasterId,
            args,
            memoryDB,
            allowNativeSceneFetch: false);
    }

    public static void OnEventStoryCellBuilt(
        EventChapterController controller,
        long episodeMasterId,
        EpisodeComponent.BaseEpisodeCellArgs args)
    {
        IMemoryDB memoryDB = null;
        try
        {
            memoryDB = controller is null ? null : controller.memoryDB;
        }
        catch (Exception exception)
        {
            OnHookFailure(controller, exception);
            SafeLogException("event story-cell memory-db inspection failed; native row retained", exception);
            return;
        }

        OnStoryCellBuilt(
            controller,
            episodeMasterId,
            args,
            memoryDB,
            allowNativeSceneFetch: true);
    }

    private static void OnStoryCellBuilt(
        Il2CppObjectBase controller,
        long episodeMasterId,
        EpisodeComponent.BaseEpisodeCellArgs args,
        IMemoryDB memoryDB,
        bool allowNativeSceneFetch)
    {
        var enabled = Enabled;
        SafeLogInfo($"story-cell factory invoked episode={episodeMasterId} enabled={enabled} controller={FormatPointer(SafePointer(controller))} argsPresent={args is not null}");
        if (!enabled)
        {
            SafeLogInfo($"story-cell episode={episodeMasterId} skip=disabled");
            return;
        }

        if (controller is null || args is null)
        {
            SafeLogWarning($"story-cell episode={episodeMasterId} skip=incomplete-wrapper native-row-retained");
            ClearAll("story-cell-incomplete-wrapper");
            return;
        }

        try
        {
            var controllerPointer = Pointer(controller);
            var cellKey = new CellIdentity(controllerPointer, episodeMasterId);
            if (!RestoreCell(cellKey))
            {
                SafeLogWarning($"story-cell episode={episodeMasterId} skip=previous-visibility-restore-failed native-row-retained");
                ClearAll("story-cell-restore-failure");
                return;
            }

            var originalViewable = args.Viewable;
            var sceneIdentityAvailable = TryResolveSceneIds(memoryDB, episodeMasterId, out var sceneIds);
            // Normal/native-visible rows must not cause a remote request. Main
            // hidden rows may use manifest membership as their catalog hint;
            // Event identity is enough and its body provider owns the source
            // chain at entry/content time.
            var localAvailable = !originalViewable
                && !allowNativeSceneFetch
                && sceneIdentityAvailable
                && HasAvailableScene(sceneIds);
            var decision = EligibilityPolicy.ForStoryCell(new StoryCellFacts(
                episodeMasterId,
                originalViewable,
                Released: allowNativeSceneFetch && sceneIdentityAvailable,
                localAvailable));
            SafeLogInfo($"story-cell episode={episodeMasterId} originalViewable={originalViewable} sceneIdentityAvailable={sceneIdentityAvailable} localAvailable={localAvailable} nativeSceneFetchAllowed={allowNativeSceneFetch} decision={decision.Eligibility.Mode}/{decision.Eligibility.Reason}");
            Capture(controller, episodeMasterId, args, originalViewable, decision.Eligibility);
            SafeLogInfo($"story-cell episode={episodeMasterId} finalViewable={args.Viewable} decision={decision.Eligibility.Mode}/{decision.Eligibility.Reason}");
        }
        catch (Exception exception)
        {
            ClearAll("story-cell-exception");
            SafeLogException($"story cell policy failed for episode {episodeMasterId}; native row retained", exception);
        }
    }

    /// <summary>
    /// MainEpisodeController.GenerateEpisodeCellArgs obtains a chapter-scoped
    /// Master list at MainEpisodeController.txt:846-940 and then filters it via
    /// b15_0 before b15_1 constructs the args. Promote only a hidden row whose
    /// runtime SceneMaster relation and configured local SceneFrame are proven;
    /// the native chapter list and every native-eligible result stay unchanged.
    /// </summary>
    public static bool ShouldIncludeMainStoryEpisode(
        MainEpisodeController controller,
        EpisodeMaster episodeMaster,
        bool nativeResult)
    {
        if (nativeResult || !Enabled || controller is null || episodeMaster is null)
        {
            return nativeResult;
        }

        try
        {
            var episodeMasterId = episodeMaster.Id;
            if (!TryResolveSceneIds(controller.memoryDB, episodeMasterId, out var sceneIds)
                || !HasAvailableScene(sceneIds))
            {
                return nativeResult;
            }

            SafeLogInfo($"main-story filter episode={episodeMasterId} native={nativeResult} localAvailable=true include=true");
            return true;
        }
        catch (Exception exception)
        {
            SafeLogException("main-story filter failed; native filter retained", exception);
            return nativeResult;
        }
    }

    /// <summary>
    /// The public typed wrappers only construct the async state machines. The
    /// persisted callers invoke d60/d61.MoveNext directly
    /// (evidence/decomp/full-isil/IsilDump/GameUi/Assets/GameUi/Episode/
    /// EpisodeComponent_NestedType__SelectCharacterCell_d__39.txt:505-510 and
    /// EpisodeComponent_NestedType___ProcessOnCreate_b__32_20_d.txt:351-356),
    /// so entry policy belongs on that real seam. The wrappers initialize d60/
    /// d61 state to -1 (evidence/decomp/full-isil/IsilDump/GameUi/Assets/
    /// GameUi/Episode/EpisodeController.txt:3562 and :3697); a resumed state is
    /// deliberately native and cannot create another pending generation.
    /// </summary>
    public static void OnCharacterMoveToAdventureStateMachine(
        EpisodeController._MoveToAdventure_d__60 stateMachine)
    {
        try
        {
            if (stateMachine is null)
            {
                OnMoveToAdventureStateMachine(null, null, int.MinValue, isMemory: false);
                return;
            }

            OnMoveToAdventureStateMachine(
                stateMachine.__4__this,
                stateMachine.args,
                stateMachine.__1__state,
                isMemory: false);
        }
        catch (Exception exception)
        {
            // Generated value-type field access is adapter code, not native game
            // behavior. Contain an interop failure so the original MoveNext still
            // runs, while invalidating any older local generation.
            SafeLogWarning("character-entry state-machine inspection failed; native flow retained");
            OnHookFailure(stateMachine, exception);
        }
    }

    public static void OnMemoryMoveToAdventureStateMachine(
        EpisodeController._MoveToAdventure_d__61 stateMachine)
    {
        try
        {
            if (stateMachine is null)
            {
                OnMoveToAdventureStateMachine(null, null, int.MinValue, isMemory: true);
                return;
            }

            OnMoveToAdventureStateMachine(
                stateMachine.__4__this,
                stateMachine.args,
                stateMachine.__1__state,
                isMemory: true);
        }
        catch (Exception exception)
        {
            SafeLogWarning("memory-entry state-machine inspection failed; native flow retained");
            OnHookFailure(stateMachine, exception);
        }
    }

    /// <summary>
    /// d60/d61 use the generated args.LockMessage only while their native lock
    /// branch is active. Restore the original cell value after the state
    /// machine completes or faults; a suspended await keeps the message until
    /// its next terminal MoveNext (full ISIL d60:636-669, :928-940 and d61:551-567).
    /// </summary>
    public static void OnCharacterMoveToAdventureStateMachineExit(
        EpisodeController._MoveToAdventure_d__60 stateMachine,
        Exception exception)
    {
        try
        {
            OnMoveToAdventureStateMachineExit(
                stateMachine is null ? null : stateMachine.args,
                stateMachine is null ? int.MinValue : stateMachine.__1__state,
                exception,
                "character");
        }
        catch (Exception cleanupException)
        {
            OnHookFailure(stateMachine, cleanupException);
            SafeLogException("character entry lock-message cleanup failed; native flow retained", cleanupException);
        }
    }

    public static void OnMemoryMoveToAdventureStateMachineExit(
        EpisodeController._MoveToAdventure_d__61 stateMachine,
        Exception exception)
    {
        try
        {
            OnMoveToAdventureStateMachineExit(
                stateMachine is null ? null : stateMachine.args,
                stateMachine is null ? int.MinValue : stateMachine.__1__state,
                exception,
                "memory");
        }
        catch (Exception cleanupException)
        {
            OnHookFailure(stateMachine, cleanupException);
            SafeLogException("memory entry lock-message cleanup failed; native flow retained", cleanupException);
        }
    }

    private static void OnMoveToAdventureStateMachineExit(
        EpisodeComponent.BaseEpisodeCellArgs args,
        int state,
        Exception exception,
        string kind)
    {
        if (exception is null && state != -2)
        {
            return;
        }

        if (!RestoreTemporaryLockMessage(args))
        {
            SafeLogWarning($"{kind}-entry local SceneFrame lock message restore failed state={state} exception={exception is not null}; local state cleared");
            ClearAll($"{kind}-entry-lock-message-restore-failed");
            return;
        }

        SafeLogInfo($"{kind}-entry local SceneFrame lock message restored state={state} exception={exception is not null}");
    }

    /// <summary>
    /// MainEpisodeController.MoveToScenario d14 and EventChapterController
    /// MoveToScenario d19 are generated state-machine entry seams. Their
    /// persisted interop fields expose only state, episodeMasterId, viewable,
    /// and controller (MainEpisodeController.cs:216-314;
    /// EventChapterController.cs:936-1035), so initial state -1 is the only
    /// place to establish a Master-derived local generation. Resumes remain
    /// native, exactly like d60/d61.
    /// </summary>
    public static void OnMainMoveToScenarioStateMachine(
        MainEpisodeController._MoveToScenario_d__14 stateMachine)
    {
        try
        {
            if (stateMachine is null)
            {
                OnStoryMoveToScenarioStateMachine(null, 0, int.MinValue, false, null, null, "main");
                return;
            }

            var controller = stateMachine.__4__this;
            OnStoryMoveToScenarioStateMachine(
                controller,
                stateMachine.episodeMasterId,
                stateMachine.__1__state,
                stateMachine.viewable,
                value => stateMachine.viewable = value,
                controller is null ? null : controller.memoryDB,
                "main");
        }
        catch (Exception exception)
        {
            OnHookFailure(stateMachine, exception);
            SafeLogException("main story entry state-machine inspection failed; native flow retained", exception);
        }
    }

    public static void OnEventMoveToScenarioStateMachine(
        EventChapterController._MoveToScenario_d__19 stateMachine)
    {
        try
        {
            if (stateMachine is null)
            {
                OnStoryMoveToScenarioStateMachine(null, 0, int.MinValue, false, null, null, "event");
                return;
            }

            var controller = stateMachine.__4__this;
            OnStoryMoveToScenarioStateMachine(
                controller,
                stateMachine.episodeMasterId,
                stateMachine.__1__state,
                stateMachine.viewable,
                value => stateMachine.viewable = value,
                controller is null ? null : controller.memoryDB,
                "event");
        }
        catch (Exception exception)
        {
            OnHookFailure(stateMachine, exception);
            SafeLogException("event story entry state-machine inspection failed; native flow retained", exception);
        }
    }

    public static void OnMainMoveToScenarioStateMachineExit(
        MainEpisodeController._MoveToScenario_d__14 stateMachine,
        Exception exception)
    {
        try
        {
            if (stateMachine is null)
            {
                return;
            }

            var terminal = stateMachine.__1__state == -2;
            if (!terminal && exception is null)
            {
                return;
            }

            var controller = stateMachine.__4__this;
            var identity = new CellIdentity(
                Pointer(controller),
                stateMachine.episodeMasterId);
            if (!RestoreTemporaryLockMessage(identity))
            {
                SafeLogWarning($"main-story-entry episode={stateMachine.episodeMasterId} lock-message restore failed; local state cleared");
                ClearAll("main-story-entry-lock-message-restore-failed");
            }
        }
        catch (Exception cleanupException)
        {
            OnHookFailure(stateMachine, cleanupException);
            SafeLogException("main story entry lock-message cleanup failed; native flow retained", cleanupException);
        }
    }

    private static void OnStoryMoveToScenarioStateMachine(
        Il2CppObjectBase controller,
        long episodeMasterId,
        int state,
        bool originalViewable,
        Action<bool> setViewable,
        IMemoryDB memoryDB,
        string kind)
    {
        SafeLogInfo($"{kind}-story-entry state-machine invoked state={state} episode={episodeMasterId} controller={FormatPointer(SafePointer(controller))} viewable={originalViewable}");
        if (state != ScenarioExecutionProvenanceRegistry.InitialState)
        {
            SafeLogInfo($"{kind}-story-entry episode={episodeMasterId} route=Native skip=resume-state");
            return;
        }

        var enabled = Enabled;
        if (!enabled)
        {
            SafeLogInfo($"{kind}-story-entry episode={episodeMasterId} route=Native skip=disabled");
            return;
        }

        if (controller is null
            || episodeMasterId <= 0
            || setViewable is null
            || memoryDB is null)
        {
            SafeLogWarning($"{kind}-story-entry episode={episodeMasterId} route=Native skip=incomplete-wrapper");
            ClearAll($"{kind}-story-entry-incomplete-wrapper");
            return;
        }

        try
        {
            var controllerPointer = Pointer(controller);
            var decision = ReadCapturedDecision(controllerPointer, episodeMasterId, out var captured);
            SafeLogInfo($"{kind}-story-entry episode={episodeMasterId} argsViewable={originalViewable} decisionSource={(captured ? "cell-factory" : "missing")} decision={decision.Mode}/{decision.Reason}");
            if (!captured || !decision.IsLocalBypass)
            {
                SafeLogInfo($"{kind}-story-entry episode={episodeMasterId} route=Native reason={(captured ? decision.Reason : BypassReason.IncompleteEvidence)} skip={(captured ? "normal-policy" : "missing-policy")}");
                ClearAll($"{kind}-story-entry-normal");
                return;
            }

            if (!TryResolveSceneIds(memoryDB, episodeMasterId, out var sceneIds))
            {
                SafeLogWarning($"{kind}-story-entry episode={episodeMasterId} route=Native localDecision={decision.Reason} skip=scene-identity-unresolved");
                ClearAll($"{kind}-story-entry-scene-unresolved");
                return;
            }

            if (kind == "main"
                && (!TryPrepareScenes(sceneIds, out var preparations)
                    || preparations is null
                    || preparations.Length != sceneIds.Count
                    || HasUnavailablePreparation(preparations)))
            {
                var identity = new CellIdentity(controllerPointer, episodeMasterId);
                VisibleCells.TryGetWrapper(identity, out var capturedWrapper);
                var message = BuildMissingSceneFrameMessage(episodeMasterId, sceneIds, preparations);
                ClearAll("main-story-entry-sceneframe-missing");
                if (capturedWrapper is EpisodeComponent.BaseEpisodeCellArgs capturedArgs)
                {
                    TrySetTemporaryLockMessage(identity, capturedArgs, message);
                }

                try
                {
                    setViewable(originalViewable);
                }
                catch (Exception restoreException)
                {
                    SafeLogException($"main-story-entry episode={episodeMasterId} native visibility restore failed", restoreException);
                }

                SafeLogWarning($"main-story-entry episode={episodeMasterId} route=Native reason=sceneframe-source-missing missingScenes=[{FormatUnavailableScenes(preparations)}] lockMessage=sceneframe-source-error");
                return;
            }

            ClearDeferredLeaveForReplacement(controllerPointer);
            if (!Sessions.BeginPending(
                    episodeMasterId,
                    sceneIds,
                    controllerPointer,
                    decision.Reason,
                    out var pending,
                    episodeServiceController: IntPtr.Zero,
                    apiClientController: IntPtr.Zero))
            {
                SafeLogWarning($"{kind}-story-entry episode={episodeMasterId} route=Native skip=pending-session-rejected reason={decision.Reason}");
                ClearAll($"{kind}-story-entry-pending-rejected");
                return;
            }

            if (!RestoreStaleCells(new CellIdentity(controllerPointer, episodeMasterId)))
            {
                SafeLogWarning($"{kind}-story-entry episode={episodeMasterId} route=Native skip=stale-cell-restore-failed");
                ClearAll($"{kind}-story-entry-stale-cell-restore-failed");
                return;
            }

            if (!originalViewable)
            {
                setViewable(true);
            }

            SafeLogInfo($"{kind}-story-entry episode={episodeMasterId} route=LocalBypass reason={decision.Reason} pendingGeneration={pending.Generation} scenes=[{FormatSceneIds(pending.ExpectedSceneIds)}] native-navigation=continue");
        }
        catch (Exception exception)
        {
            ClearAll($"{kind}-story-entry-exception");
            SafeLogException($"{kind} story entry policy failed for episode {episodeMasterId}; native flow retained", exception);
        }
    }

    private static void OnMoveToAdventureStateMachine(
        EpisodeController controller,
        EpisodeComponent.BaseEpisodeCellArgs args,
        int state,
        bool isMemory)
    {
        var kind = isMemory ? "memory" : "character";
        SafeLogInfo($"{kind}-entry state-machine invoked state={state} controller={FormatPointer(SafePointer(controller))} argsPresent={args is not null}");
        if (state != -1)
        {
            SafeLogInfo($"{kind}-entry state={state} route=Native skip=resume-state");
            return;
        }

        OnMoveToAdventure(controller, args, isMemory);
    }

    public static void OnScenarioRefresh(ScenarioController controller)
    {
        var enabled = Enabled;
        SafeLogInfo($"scenario-refresh invoked enabled={enabled} controller={FormatPointer(SafePointer(controller))} scene={SafeSceneId(controller)} session={SafeDescribeSessionState()}");
        if (!enabled)
        {
            SafeLogInfo("scenario-refresh skip=disabled");
            return;
        }

        if (controller is null)
        {
            SafeLogWarning("scenario-refresh skip=incomplete-controller");
            ClearAll("scenario-refresh-incomplete-controller");
            return;
        }

        try
        {
            var scenarioPointer = Pointer(controller);
            var sceneId = controller.sceneMasterId;
            var pending = Sessions.Pending;
            var boundBeforeRefresh = Sessions.Bound;
            if (pending is null && boundBeforeRefresh is null)
            {
                SafeLogInfo($"scenario-refresh scene={sceneId} route=Native skip=no-pending-session");
                return;
            }

            if (sceneId <= 0 || scenarioPointer == IntPtr.Zero)
            {
                // The selected Refresh d__76 seam is after native frame data is
                // assigned. A missing runtime SceneMasterId/controller is not a
                // bindable continuation; restore any visible cell before native
                // refresh continues.
                var incompleteEpisode = pending?.EpisodeId ?? boundBeforeRefresh?.EpisodeId ?? 0;
                var incompleteGeneration = pending?.Generation ?? boundBeforeRefresh?.Generation ?? 0;
                SafeLogWarning($"scenario-refresh episode={incompleteEpisode} generation={incompleteGeneration} scene={sceneId} route=Native skip=incomplete-scene-or-controller");
                ClearAll("scenario-refresh-incomplete-identity");
                return;
            }

            var episodeId = pending?.EpisodeId ?? boundBeforeRefresh!.EpisodeId;
            var episodeController = pending?.EpisodeController ?? boundBeforeRefresh!.EpisodeController;
            var servicePointer = PointerObject(controller.episodeService);
            var apiClientPointer = PointerObject(controller.apiClient);
            if (servicePointer == IntPtr.Zero || apiClientPointer == IntPtr.Zero)
            {
                var incompleteIdentityEpisode = pending?.EpisodeId ?? boundBeforeRefresh!.EpisodeId;
                var incompleteIdentityGeneration = pending?.Generation ?? boundBeforeRefresh!.Generation;
                SafeLogWarning($"scenario-refresh episode={incompleteIdentityEpisode} generation={incompleteIdentityGeneration} scene={sceneId} route=Native skip=service-or-api-identity-incomplete service={FormatPointer(servicePointer)} api={FormatPointer(apiClientPointer)}");
                ClearAll("scenario-refresh-service-api-incomplete");
                return;
            }
            if (!Sessions.TryBind(
                    episodeId,
                    sceneId,
                    episodeController,
                    scenarioPointer,
                    out var bound,
                    servicePointer,
                    apiClientPointer))
            {
                // An unexpected scene/controller is stale or incomplete
                // provenance. Keep the native route visible instead of leaving a
                // LocalBypass Viewable override pending for a later refresh.
                var rejectedExpectedScenes = pending?.ExpectedSceneIds ?? boundBeforeRefresh!.ExpectedSceneIds;
                var rejectedGeneration = pending?.Generation ?? boundBeforeRefresh!.Generation;
                SafeLogWarning($"scenario-refresh episode={episodeId} pendingGeneration={rejectedGeneration} scene={sceneId} controller={FormatPointer(scenarioPointer)} route=Native skip=bind-rejected expectedScenes=[{FormatSceneIds(rejectedExpectedScenes)}]");
                ClearAll("scenario-refresh-bind-rejected");
                return;
            }

            lock (Gate)
            {
                _activeScenarioController = bound.ScenarioController;
            }
            var transition = boundBeforeRefresh is null ? "initial" : "advance";
            SafeLogInfo($"session bound episode={bound.EpisodeId} scene={bound.SceneId} generation={bound.Generation} reason={bound.Reason} transition={transition} scenarioController={FormatPointer(bound.ScenarioController)} service={FormatPointer(bound.EpisodeServiceController)} api={FormatPointer(bound.ApiClientController)} expectedScenes=[{FormatSceneIds(bound.ExpectedSceneIds)}]");
            SafeLogInfo($"scenario-refresh episode={bound.EpisodeId} scene={bound.SceneId} route=LocalBypass reason={bound.Reason} native-refresh=continue");
        }
        catch (Exception exception)
        {
            // Binding is the selected ScenarioController Refresh d__76 seam.
            // An exception is fail-open and invalidates the unbound generation.
            ClearAll("scenario-refresh-exception");
            SafeLogException("scenario binding failed; native flow retained", exception);
        }
    }

    /// <summary>
    /// The persisted ScenarioController PostRead/PostBranchSelection wrappers
    /// create the corresponding d104/d103 state machines. Capture the complete
    /// bound identity at that evidence-backed start seam. The selected
    /// ScenarioController wrapper calls AsyncUniTaskMethodBuilder.Start and then
    /// synchronously invokes the initial MoveNext (ScenarioController.txt
    /// lines 725 and 847), so a one-shot start frame can be consumed only by the
    /// matching d103/d104 initial state.
    /// </summary>
    public static void OnScenarioPostReadStarted(ScenarioController controller)
    {
        SafeLogInfo($"scenario-postread wrapper invoked controller={FormatPointer(SafePointer(controller))}");
        ConsumeDeferredLeaveForPostRead(controller);
        InstallScenarioStartFrame(controller, ScenarioExecutionKind.PostRead);
    }

    public static void OnScenarioPostBranchSelectionStarted(ScenarioController controller)
    {
        SafeLogInfo($"scenario-branch wrapper invoked controller={FormatPointer(SafePointer(controller))}");
        InstallScenarioStartFrame(controller, ScenarioExecutionKind.BranchSelection);
    }

    public static void OnScenarioPostReadFinished(ScenarioController controller)
    {
        SafeLogInfo($"scenario-postread wrapper finished controller={FormatPointer(SafePointer(controller))}");
        ClearScenarioStartFrame(controller, ScenarioExecutionKind.PostRead);
    }

    public static void OnScenarioPostBranchSelectionFinished(ScenarioController controller)
    {
        SafeLogInfo($"scenario-branch wrapper finished controller={FormatPointer(SafePointer(controller))}");
        ClearScenarioStartFrame(controller, ScenarioExecutionKind.BranchSelection);
    }

    public static void OnPostReadStateMachineMoveNext(ScenarioController._PostRead_d__104 stateMachine)
    {
        try
        {
            var scenario = stateMachine is null ? null : stateMachine.__4__this;
            var state = stateMachine is null ? int.MinValue : stateMachine.__1__state;
            SafeLogInfo($"scenario-postread MoveNext state={state} stateMachine={FormatPointer(Pointer(stateMachine))} controller={FormatPointer(Pointer(scenario))}");
            BeginStateMachineExecution(ScenarioExecutionKind.PostRead, stateMachine, scenario, state);
        }
        catch (Exception exception)
        {
            OnHookFailure(stateMachine, exception);
            SafeLogException("post-read state-machine wrapper failed; native flow retained", exception);
        }
    }

    public static long OnPostBranchSelectionStateMachineMoveNext(ScenarioController._PostBranchSelection_d__103 stateMachine)
    {
        ScenarioController scenario = null;
        var originalSceneId = 0L;
        try
        {
            scenario = stateMachine is null ? null : stateMachine.__4__this;
            var state = stateMachine is null ? int.MinValue : stateMachine.__1__state;
            SafeLogInfo($"scenario-branch MoveNext state={state} stateMachine={FormatPointer(Pointer(stateMachine))} controller={FormatPointer(Pointer(scenario))}");
            BeginStateMachineExecution(ScenarioExecutionKind.BranchSelection, stateMachine, scenario, state);

            if (state != ScenarioExecutionProvenanceRegistry.InitialState
                || !Enabled
                || scenario is null
                || !TryGetExecutionIdentity(ScenarioExecutionKind.BranchSelection, out var identity)
                || !HasActiveScenario(identity)
                || !Sessions.TryMatch(identity)
                || !Sessions.TryMatchBranch(identity, PointerObject(scenario.apiClient)))
            {
                return 0;
            }

            originalSceneId = scenario.sceneMasterId;
            if (originalSceneId <= 0 || originalSceneId != identity.SceneId)
            {
                return 0;
            }

            // Persisted d103 ISIL lines 543-544 already contain a native
            // sceneMasterId<=0 path that skips only the remote branch POST and
            // continues into history/answer/choice-close updates. Selecting that
            // guard avoids Harmony touching the generic UniTask<Result<T>> ABI.
            scenario.sceneMasterId = 0;
            SafeLogInfo($"branch scene={originalSceneId} episode={identity.EpisodeId} generation={identity.Generation} route=LocalBypass reason=exact-session-match; native-d103-no-post-guard");
            return originalSceneId;
        }
        catch (Exception exception)
        {
            RestoreScenarioSceneId(scenario, originalSceneId);
            OnHookFailure(stateMachine, exception);
            SafeLogException("branch state-machine wrapper failed; native flow retained", exception);
            return 0;
        }
    }

    public static Exception OnPostReadStateMachineExit(
        ScenarioController._PostRead_d__104 stateMachine,
        Exception exception)
    {
        try
        {
            var scenario = stateMachine is null ? null : stateMachine.__4__this;
            var completed = stateMachine is not null && stateMachine.__1__state == -2;
            SafeLogInfo($"scenario-postread exit state={(stateMachine is null ? int.MinValue : stateMachine.__1__state)} completed={completed} exception={exception is not null} stateMachine={FormatPointer(Pointer(stateMachine))}");
            var result = EndStateMachineExecution(ScenarioExecutionKind.PostRead, stateMachine, exception, completed);
            if (completed || exception is not null)
            {
                // The public PostRead wrapper returns immediately after its
                // initial MoveNext (ScenarioController.txt:725, :847), so its
                // finalizer is not the terminal boundary. Clear a retained
                // Leave only after d104 reaches -2 or faults; this is the
                // point at which EpisodeService.PostRead and its tracking,
                // reward, and progress chain has actually finished.
                CompleteDeferredLeaveAfterPostRead(scenario);
            }

            return result;
        }
        catch (Exception cleanupException)
        {
            OnHookFailure(stateMachine, cleanupException);
            SafeLogException("post-read state-machine cleanup failed; native flow retained", cleanupException);
            return exception;
        }
    }

    public static Exception OnPostBranchSelectionStateMachineExit(
        ScenarioController._PostBranchSelection_d__103 stateMachine,
        long originalSceneId,
        Exception exception)
    {
        try
        {
            var scenario = stateMachine is null ? null : stateMachine.__4__this;
            if (!RestoreScenarioSceneId(scenario, originalSceneId))
            {
                SafeLogWarning($"scenario-branch scene restore failed originalScene={originalSceneId}; local state cleared");
                ClearAll("scenario-branch-scene-restore-failed");
            }

            var completed = stateMachine is not null && stateMachine.__1__state == -2;
            SafeLogInfo($"scenario-branch exit state={(stateMachine is null ? int.MinValue : stateMachine.__1__state)} completed={completed} exception={exception is not null} stateMachine={FormatPointer(Pointer(stateMachine))}");
            return EndStateMachineExecution(ScenarioExecutionKind.BranchSelection, stateMachine, exception, completed);
        }
        catch (Exception cleanupException)
        {
            OnHookFailure(stateMachine, cleanupException);
            SafeLogException("branch state-machine cleanup failed; native flow retained", cleanupException);
            return exception;
        }
    }

    public static void OnScenarioPostReadStartFailure(ScenarioController controller, Exception exception = null)
    {
        SafeLogWarning($"scenario-postread wrapper failed controller={FormatPointer(SafePointer(controller))}");
        ClearScenarioStartFrame(controller, ScenarioExecutionKind.PostRead);
        OnHookFailure(controller, exception);
    }

    public static void OnScenarioPostBranchSelectionStartFailure(ScenarioController controller, Exception exception = null)
    {
        SafeLogWarning($"scenario-branch wrapper failed controller={FormatPointer(SafePointer(controller))}");
        ClearScenarioStartFrame(controller, ScenarioExecutionKind.BranchSelection);
        OnHookFailure(controller, exception);
    }

    public static void OnScenarioLeave(ScenarioController controller)
    {
        try
        {
            var scenarioPointer = Pointer(controller);
            var bound = Sessions.Bound;
            SafeLogInfo($"scenario-leave invoked controller={FormatPointer(scenarioPointer)} session={DescribeSessionState()}");
            if (bound is not null
                && scenarioPointer != IntPtr.Zero
                && bound.ScenarioController == scenarioPointer)
            {
                lock (Gate)
                {
                    if (_deferredLeave is null
                        || _deferredLeave.Value.Identity != bound.Identity)
                    {
                        _deferredLeave = new DeferredLeave(bound.Identity, PostReadStarted: false);
                        SafeLogInfo($"scenario-leave episode={bound.EpisodeId} scene={bound.SceneId} generation={bound.Generation} retain=following-postread");
                    }
                    else
                    {
                        SafeLogInfo($"scenario-leave episode={bound.EpisodeId} scene={bound.SceneId} generation={bound.Generation} retain=already-deferred");
                    }
                }

                return;
            }

            // A pending session has no ScenarioController pointer yet, and a
            // leave from any other controller is an abandon/replacement. Both
            // paths clear immediately so an unrelated refresh cannot bind it.
            ClearAll("scenario-leave");
        }
        catch (Exception exception)
        {
            ClearAll("scenario-leave-exception");
            SafeLogException("scenario cleanup failed", exception);
        }
    }

    private static void ConsumeDeferredLeaveForPostRead(ScenarioController controller)
    {
        var scenarioPointer = SafePointer(controller);
        if (scenarioPointer == IntPtr.Zero)
        {
            return;
        }

        lock (Gate)
        {
            if (_deferredLeave is not null
                && _deferredLeave.Value.Identity.ScenarioController == scenarioPointer)
            {
                _deferredLeave = _deferredLeave.Value with { PostReadStarted = true };
                SafeLogInfo($"scenario-postread episode={_deferredLeave.Value.Identity.EpisodeId} scene={_deferredLeave.Value.Identity.SceneId} generation={_deferredLeave.Value.Identity.Generation} consume=deferred-leave");
            }
        }
    }

    private static void ClearDeferredLeaveForReplacement(IntPtr episodeController)
    {
        if (episodeController == IntPtr.Zero)
        {
            return;
        }

        lock (Gate)
        {
            if (_deferredLeave is not null)
            {
                SafeLogInfo($"deferred leave replaced episode={_deferredLeave.Value.Identity.EpisodeId} generation={_deferredLeave.Value.Identity.Generation} by controller={FormatPointer(episodeController)}");
                _deferredLeave = null;
            }
        }
    }

    private static void CompleteDeferredLeaveAfterPostRead(ScenarioController controller)
    {
        var scenarioPointer = SafePointer(controller);
        var clear = false;
        lock (Gate)
        {
            if (_deferredLeave is not null
                && _deferredLeave.Value.PostReadStarted
                && _deferredLeave.Value.Identity.ScenarioController == scenarioPointer)
            {
                SafeLogInfo($"scenario-postread episode={_deferredLeave.Value.Identity.EpisodeId} scene={_deferredLeave.Value.Identity.SceneId} generation={_deferredLeave.Value.Identity.Generation} terminal=clear-deferred-leave");
                _deferredLeave = null;
                clear = true;
            }
        }

        if (clear)
        {
            ClearAll("scenario-postread-terminal");
        }
    }

    public static bool ShouldSuppressPostRead(EpisodeService service, long episodeMasterId)
    {
        var enabled = Enabled;
        SafeLogInfo($"postread hook invoked episode={episodeMasterId} enabled={enabled} service={FormatPointer(SafePointer(service))}");
        if (!enabled)
        {
            SafeLogInfo($"postread episode={episodeMasterId} route=Native reason=disabled");
            return false;
        }

        if (service is null)
        {
            SafeLogWarning($"postread episode={episodeMasterId} route=Native reason=service-null");
            return false;
        }

        if (episodeMasterId <= 0)
        {
            SafeLogWarning($"postread episode={episodeMasterId} route=Native reason=invalid-episode-id");
            return false;
        }

        try
        {
            var servicePointer = Pointer(service);
            if (!TryGetExecutionIdentity(ScenarioExecutionKind.PostRead, out var identity))
            {
                SafeLogInfo($"postread episode={episodeMasterId} route=Native reason=no-provenance");
                return false;
            }

            if (identity.EpisodeId != episodeMasterId)
            {
                SafeLogInfo($"postread episode={episodeMasterId} route=Native reason=episode-mismatch identityEpisode={identity.EpisodeId} scene={identity.SceneId} generation={identity.Generation}");
                return false;
            }

            if (!HasActiveScenario(identity))
            {
                SafeLogInfo($"postread episode={episodeMasterId} route=Native reason=scenario-not-active scene={identity.SceneId} generation={identity.Generation}");
                return false;
            }

            if (!Sessions.TryMatch(identity))
            {
                SafeLogInfo($"postread episode={episodeMasterId} route=Native reason=session-identity-mismatch scene={identity.SceneId} generation={identity.Generation}");
                return false;
            }

            var matched = Sessions.TryMatchEpisodeService(identity, servicePointer);
            SafeLogInfo($"postread episode={episodeMasterId} scene={identity.SceneId} generation={identity.Generation} service={FormatPointer(servicePointer)} route={(matched ? "LocalBypass" : "Native")} reason={(matched ? "exact-session-match; native PostRead/tracking suppressed" : "episode-service-mismatch")}");
            return matched;
        }
        catch (Exception exception)
        {
            ClearAll("postread-match-exception");
            SafeLogException($"post-read match failed for episode {episodeMasterId}; native call retained", exception);
            return false;
        }
    }

    public static bool TryProvideLocalSceneFrames(
        EpisodeService service,
        long sceneMasterId,
        out UniTask<Il2CppReferenceArray<SceneFrameMaster>> task)
    {
        task = default;
        var enabled = Enabled;
        SafeLogInfo($"scene-provider invoked scene={sceneMasterId} enabled={enabled} service={FormatPointer(SafePointer(service))} session={SafeDescribeSessionState()}");
        if (!enabled)
        {
            SafeLogInfo($"scene-provider scene={sceneMasterId} route=Native reason=disabled");
            return false;
        }

        if (service is null)
        {
            SafeLogWarning($"scene-provider scene={sceneMasterId} route=Native reason=service-null");
            return false;
        }

        if (sceneMasterId <= 0)
        {
            SafeLogWarning($"scene-provider scene={sceneMasterId} route=Native reason=invalid-scene-id");
            return false;
        }

        try
        {
            var servicePointer = Pointer(service);
            var leaseMatched = Sessions.TryBeginSceneContent(sceneMasterId, servicePointer, out var lease);
            SafeLogInfo($"scene-provider scene={sceneMasterId} leaseMatch={leaseMatched} service={FormatPointer(servicePointer)} leaseGeneration={(leaseMatched ? lease.Generation : 0)}");
            if (!leaseMatched)
            {
                SafeLogInfo($"scene-provider scene={sceneMasterId} route=Native reason=no-active-local-lease");
                return false;
            }

            if (_sceneFrames is null)
            {
                SafeLogWarning($"scene-provider scene={sceneMasterId} route=Native source=none status=uninitialized");
                return false;
            }

            var resolution = _sceneFrames.Resolve(sceneMasterId);
            SafeLogInfo($"scene-provider scene={sceneMasterId} source={resolution.Source} status={resolution.Status} remote={resolution.RemoteStatus} local={resolution.LocalStatus} leaseGeneration={lease.Generation}");
            if (!resolution.IsAvailable)
            {
                SafeLogWarning($"scene-provider scene={sceneMasterId} route=Native source={resolution.Source} status={resolution.Status} remote={resolution.RemoteStatus} local={resolution.LocalStatus}");
                return false;
            }

            var localFrames = resolution.Frames;

            var nativeFrames = new Il2CppReferenceArray<SceneFrameMaster>(localFrames.Length);
            for (var index = 0; index < localFrames.Length; index++)
            {
                var local = localFrames[index];
                var native = new SceneFrameMaster
                {
                    Order = local.Order,
                    SceneMasterId = local.SceneId,
                    ConfigurationJson = local.ConfigurationJson,
                };
                if (local.BranchId.HasValue)
                {
                    native.SceneBranchMasterId = new Il2CppSystem.Nullable<long>(local.BranchId.Value);
                }

                if (local.SelectedBranchId.HasValue)
                {
                    native.SelectedSceneBranchSelectionMasterId = new Il2CppSystem.Nullable<long>(local.SelectedBranchId.Value);
                }

                nativeFrames[index] = native;
            }

            var localTask = UniTask.FromResult(nativeFrames);
            if (!Sessions.TryConfirmSceneContent(lease))
            {
                SafeLogWarning($"scene-provider scene={sceneMasterId} route=Native reason=lease-rejected-after-parse leaseGeneration={lease.Generation} session={DescribeSessionState()}");
                return false;
            }

            task = localTask;
            SafeLogInfo($"scene-provider scene={sceneMasterId} route=LocalBypass reason=lease-confirmed frameCount={nativeFrames.Length} generation={lease.Generation}");
            return true;
        }
        catch (Exception exception)
        {
            SafeLogException($"scene-provider scene {sceneMasterId}: local frame adapter failed; native content flow retained", exception);
            return false;
        }
    }

    private static void InstallScenarioStartFrame(
        ScenarioController controller,
        ScenarioExecutionKind kind)
    {
        var enabled = Enabled;
        SafeLogInfo($"scenario-{kind} provenance-start enabled={enabled} controller={FormatPointer(SafePointer(controller))}");
        if (!enabled)
        {
            SafeLogInfo($"scenario-{kind} provenance-start skip=disabled");
            return;
        }

        if (controller is null)
        {
            SafeLogWarning($"scenario-{kind} provenance-start skip=controller-null");
            return;
        }

        try
        {
            var scenarioPointer = Pointer(controller);
            if (!TryReadScenarioIdentity(controller, out var identity))
            {
                SafeLogWarning($"scenario-{kind} provenance-start skip=identity-unavailable session={DescribeSessionState()}");
                return;
            }

            var frame = ExecutionProvenance.CreateStartFrame(kind, scenarioPointer, identity);
            if (frame is null)
            {
                SafeLogWarning($"scenario-{kind} provenance-start skip=start-frame-rejected episode={identity.EpisodeId} scene={identity.SceneId} generation={identity.Generation}");
                return;
            }

            ActiveStartFrame = new StartFrameScope(frame, ActiveStartFrame);
            SafeLogInfo($"scenario-{kind} provenance-start captured episode={identity.EpisodeId} scene={identity.SceneId} generation={identity.Generation} startFrames={ExecutionProvenance.StartFrameCount}");
        }
        catch (Exception exception)
        {
            ClearAll($"scenario-{kind}-provenance-start-exception");
            SafeLogException("scenario provenance capture failed; native flow retained", exception);
        }
    }

    private static void ClearScenarioStartFrame(
        ScenarioController controller,
        ScenarioExecutionKind kind)
    {
        try
        {
            var scenarioPointer = Pointer(controller);
            var scope = ActiveStartFrame;
            if (scope is null
                || scope.Frame.Kind != kind
                || scope.Frame.ScenarioController != scenarioPointer)
            {
                SafeLogInfo($"scenario-{kind} provenance-finish skip=no-matching-start-frame controller={FormatPointer(scenarioPointer)}");
                return;
            }

            SafeLogInfo($"scenario-{kind} provenance-finish clearing episode={scope.Frame.Identity.EpisodeId} scene={scope.Frame.Identity.SceneId} generation={scope.Frame.Identity.Generation}");
            ExecutionProvenance.ReleaseStartFrame(scope.Frame);
            ActiveStartFrame = scope.Previous;
        }
        catch (Exception exception)
        {
            ClearAll($"scenario-{kind}-provenance-finish-exception");
            SafeLogException("scenario provenance cleanup failed; native flow retained", exception);
        }
    }

    private static void ClearActiveStartFrames()
    {
        var scope = ActiveStartFrame;
        while (scope is not null)
        {
            ExecutionProvenance.ReleaseStartFrame(scope.Frame);
            scope = scope.Previous;
        }

        ActiveStartFrame = null;
    }

    private static void BeginStateMachineExecution(
        ScenarioExecutionKind kind,
        Il2CppObjectBase stateMachine,
        ScenarioController scenario,
        int state)
    {
        var key = default(ExecutionKey);
        var context = new ExecutionContext(false, default);
        var captureReason = "none";
        try
        {
            var stateMachinePointer = Pointer(stateMachine);
            key = new ExecutionKey(stateMachinePointer, kind);
            if (stateMachinePointer == IntPtr.Zero)
            {
                captureReason = "state-machine-null";
            }
            else
            {
                var scenarioPointer = Pointer(scenario);
                if (ExecutionProvenance.TryGet(stateMachinePointer, kind, out var activeIdentity))
                {
                    if (scenarioPointer != IntPtr.Zero
                        && activeIdentity.IsComplete
                        && activeIdentity.ScenarioController == scenarioPointer)
                    {
                        context = new ExecutionContext(true, activeIdentity);
                        captureReason = "resumed-generation";
                    }
                    else
                    {
                        captureReason = "active-provenance-mismatch";
                    }
                }
                else if (scenarioPointer != IntPtr.Zero
                    && state == ScenarioExecutionProvenanceRegistry.InitialState)
                {
                    var scope = ActiveStartFrame;
                    if (scope is not null
                        && ExecutionProvenance.BeginInitial(
                            stateMachinePointer,
                            scenarioPointer,
                            kind,
                            state,
                            scope.Frame,
                            out var initialIdentity))
                    {
                        context = new ExecutionContext(true, initialIdentity);
                        captureReason = "wrapper-start-frame";
                        // The one-shot frame has done its job. Clear it now so
                        // a resumed state=0 continuation cannot consume the
                        // wrapper handoff; d103/d104 both write state 0 when
                        // they suspend (selected d103:1464-1466,
                        // d104:511-512).
                        ExecutionProvenance.ReleaseStartFrame(scope.Frame);
                        ActiveStartFrame = scope.Previous;
                    }
                    else
                    {
                        // EpisodeComponent callers can invoke d103.MoveNext
                        // directly just as the persisted d60/d61 callers do.
                        // The exact active Bound identity is sufficient proof at
                        // this generated seam; do not read nullable fields or
                        // manufacture a generic API result. The d103 prefix can
                        // select the state machine's own no-POST guard only while
                        // this synchronous MoveNext frame is active.
                        if (kind == ScenarioExecutionKind.BranchSelection
                            && TryReadScenarioIdentity(scenario, out var directIdentity))
                        {
                            context = new ExecutionContext(true, directIdentity);
                            captureReason = "direct-active-session";
                        }
                        else
                        {
                            captureReason = scope is null ? "initial-state-without-start-frame" : "start-frame-mismatch";
                        }
                    }
                }
                else
                {
                    captureReason = scenarioPointer == IntPtr.Zero
                        ? "scenario-controller-null"
                        : "resume-state-without-active-provenance";
                }
            }

            ActiveExecution = new ExecutionFrame(key, context, ActiveExecution);
            SafeLogInfo($"scenario-{kind} provenance-movenext state={state} stateMachine={FormatPointer(key.StateMachine)} identity={(context.HasIdentity ? $"episode={context.Identity.EpisodeId},scene={context.Identity.SceneId},generation={context.Identity.Generation}" : "none")} capture={captureReason} active={ExecutionProvenance.ActiveCount}");
        }
        catch (Exception exception)
        {
            ClearAll($"scenario-{kind}-provenance-movenext-exception");
            SafeLogException("state-machine provenance capture failed; native flow retained", exception);
        }
    }

    private static Exception EndStateMachineExecution(
        ScenarioExecutionKind kind,
        Il2CppObjectBase stateMachine,
        Exception exception,
        bool completed)
    {
        var key = default(ExecutionKey);
        try
        {
            key = new ExecutionKey(Pointer(stateMachine), kind);
            var current = ActiveExecution;
            if (current is not null && current.Key == key)
            {
                ActiveExecution = current.Previous;
            }

            var removed = ExecutionProvenance.End(
                key.StateMachine,
                kind,
                exception is not null || completed);

            if (exception is not null)
            {
                OnHookFailure(stateMachine, exception);
            }

            SafeLogInfo($"scenario-{kind} provenance-end stateMachine={FormatPointer(key.StateMachine)} terminal={completed} faulted={exception is not null} removed={removed} active={ExecutionProvenance.ActiveCount}");
        }
        catch (Exception cleanupException)
        {
            ClearAll($"scenario-{kind}-provenance-end-exception");
            SafeLogException("state-machine provenance cleanup failed; native flow retained", cleanupException);
        }

        return exception;
    }

    private static bool TryGetExecutionIdentity(ScenarioExecutionKind kind, out PlaybackIdentity identity)
    {
        var current = ActiveExecution;
        if (current is not null
            && current.Key.Kind == kind
            && current.Context.HasIdentity
            && current.Context.Identity.IsComplete)
        {
            identity = current.Context.Identity;
            return true;
        }

        identity = default;
        return false;
    }

    private static bool TryReadScenarioIdentity(ScenarioController controller, out PlaybackIdentity identity)
    {
        identity = default;
        var scenarioPointer = Pointer(controller);
        if (scenarioPointer == IntPtr.Zero)
        {
            SafeLogWarning("scenario identity read failed reason=controller-null");
            return false;
        }

        var sceneId = controller.sceneMasterId;
        if (sceneId <= 0)
        {
            SafeLogWarning($"scenario identity read failed reason=invalid-scene-id scene={sceneId} controller={FormatPointer(scenarioPointer)}");
            return false;
        }

        var episodeMaster = controller.EpisodeMaster;
        var episodeId = episodeMaster is null ? 0 : episodeMaster.Id;
        if (episodeId <= 0)
        {
            SafeLogWarning($"scenario identity read failed reason=invalid-episode-id scene={sceneId} controller={FormatPointer(scenarioPointer)}");
            return false;
        }

        if (!Sessions.TryGetBoundIdentity(out var boundIdentity))
        {
            SafeLogWarning($"scenario identity read failed reason=no-bound-session episode={episodeId} scene={sceneId} controller={FormatPointer(scenarioPointer)}");
            return false;
        }

        if (!Sessions.TryMatch(boundIdentity))
        {
            SafeLogWarning($"scenario identity read failed reason=bound-session-mismatch episode={episodeId} scene={sceneId} generation={boundIdentity.Generation}");
            return false;
        }

        if (boundIdentity.ScenarioController != scenarioPointer)
        {
            SafeLogWarning($"scenario identity read failed reason=scenario-controller-mismatch episode={episodeId} scene={sceneId} boundController={FormatPointer(boundIdentity.ScenarioController)} actualController={FormatPointer(scenarioPointer)}");
            return false;
        }

        if (boundIdentity.EpisodeId != episodeId)
        {
            SafeLogWarning($"scenario identity read failed reason=episode-mismatch boundEpisode={boundIdentity.EpisodeId} actualEpisode={episodeId} scene={sceneId}");
            return false;
        }

        if (boundIdentity.SceneId != sceneId)
        {
            SafeLogWarning($"scenario identity read failed reason=scene-mismatch boundScene={boundIdentity.SceneId} actualScene={sceneId} episode={episodeId}");
            return false;
        }

        identity = boundIdentity;
        return true;
    }

    public static void ClearForEntry(EpisodeController controller, long episodeMasterId, string reason = "entry-cleanup")
    {
        try
        {
            // There is only one local generation. Entry cleanup invalidates its
            // session and temporary presentation state, but the surrounding
            // catalog remains active and sibling d60/d61 selections still need
            // the decisions captured by their cell factories (evidence/decomp/
            // full-isil/IsilDump/GameUi/Assets/GameUi/Episode/
            // EpisodeController.txt:3526-3780).
            ClearState(reason, clearCatalogDecisions: false);
        }
        catch (Exception exception)
        {
            SafeLogException($"entry cleanup failed for episode {episodeMasterId}", exception);
        }
    }

    public static void OnHookFailure(Il2CppObjectBase instance, Exception exception = null)
    {
        try
        {
            // Any hook exception makes the local identity untrustworthy. There
            // is only one active local playback generation, so clearing the
            // whole registry is the safest fail-open action.
            SafeLogError($"hook failure instance={FormatPointer(SafePointer(instance))}; local state cleared", exception);
            ClearAll("hook-failure");
        }
        catch (Exception cleanupException)
        {
            SafeLogException("hook cleanup failed", cleanupException);
        }
    }

    private static void OnMoveToAdventure(
        EpisodeController controller,
        EpisodeComponent.BaseEpisodeCellArgs args,
        bool isMemory)
    {
        var kind = isMemory ? "memory" : "character";
        var enabled = Enabled;
        var episodeMasterId = args is null ? 0 : SafeEpisodeId(args);
        var originalEntryViewable = false;
        var entryVisibilityOverrideAttempted = false;
        var localRouteEstablished = false;
        SafeLogInfo($"{kind}-entry invoked episode={episodeMasterId} enabled={enabled} controller={FormatPointer(SafePointer(controller))} argsPresent={args is not null}");
        if (!enabled)
        {
            SafeLogInfo($"{kind}-entry episode={episodeMasterId} route=Native skip=disabled");
            return;
        }

        if (controller is null || args is null)
        {
            SafeLogWarning($"{kind}-entry episode={episodeMasterId} route=Native skip=incomplete-wrapper");
            ClearForEntry(controller, episodeMasterId, $"{kind}-entry-incomplete-wrapper");
            return;
        }

        try
        {
            episodeMasterId = args.Id;
            var controllerPointer = Pointer(controller);
            var decision = ReadCapturedDecision(controllerPointer, episodeMasterId, out var captured);
            originalEntryViewable = args.Viewable;
            SafeLogInfo($"{kind}-entry episode={episodeMasterId} argsViewable={originalEntryViewable} decisionSource={(captured ? "cell-factory" : "missing")} decision={decision.Mode}/{decision.Reason}");
            if (!captured || !decision.IsLocalBypass)
            {
                SafeLogInfo($"{kind}-entry episode={episodeMasterId} route=Native reason={(captured ? decision.Reason : BypassReason.IncompleteEvidence)} skip={(captured ? "normal-policy" : "missing-policy")}");
                ClearForEntry(controller, episodeMasterId, $"{kind}-entry-normal");
                return;
            }

            if (!TryResolveSceneIds(controller.memoryDB, episodeMasterId, out var sceneIds))
            {
                SafeLogWarning($"{kind}-entry episode={episodeMasterId} route=Native localDecision={decision.Reason} skip=scene-identity-unresolved");
                ClearForEntry(controller, episodeMasterId, $"{kind}-entry-scene-unresolved");
                return;
            }

            if (!TryPrepareScenes(sceneIds, out var preparations)
                || preparations is null
                || preparations.Length != sceneIds.Count
                || HasUnavailablePreparation(preparations))
            {
                // The LocalBypass route is proven only by the complete expected
                // SceneMaster set from remote or local static sources. Keep the
                // native d60/d61 lock branch and use its public transient
                // LockMessage field; this avoids entering with an empty native
                // frame sequence and prevents the misleading EPSD008 path
                // (full ISIL d60:529-559, :616-623 and d61:551-555).
                ClearForEntry(controller, episodeMasterId, $"{kind}-entry-local-sceneframe-missing");
                var message = BuildMissingSceneFrameMessage(episodeMasterId, sceneIds, preparations);
                if (TrySetTemporaryLockMessage(new CellIdentity(controllerPointer, episodeMasterId), args, message))
                {
                    SafeLogWarning($"{kind}-entry episode={episodeMasterId} route=Native reason=sceneframe-source-missing missingScenes=[{FormatUnavailableScenes(preparations)}] lockMessage=sceneframe-source-error");
                }
                else
                {
                    SafeLogWarning($"{kind}-entry episode={episodeMasterId} route=Native reason=sceneframe-source-missing lockMessage=unavailable");
                }

                return;
            }

            SafeLogInfo($"{kind}-entry episode={episodeMasterId} route=LocalBypass reason={decision.Reason} scenes=[{FormatSceneIds(sceneIds)}]");

            var servicePointer = PointerObject(controller.episodeService);
            var apiClientPointer = PointerObject(controller.apiClient);
            if (servicePointer == IntPtr.Zero || apiClientPointer == IntPtr.Zero)
            {
                // The selected PostRead d__16 and PostBranchSelection d__103
                // boundaries need both exact object identities. Without them,
                // a local session could not guard business calls safely; keep
                // the complete entry on the native Normal route.
                ClearForEntry(controller, episodeMasterId, $"{kind}-entry-service-api-incomplete");
                SafeLogWarning($"{kind}-entry episode={episodeMasterId} route=Native skip=service-or-api-identity-incomplete service={FormatPointer(servicePointer)} api={FormatPointer(apiClientPointer)}");
                return;
            }

            ClearDeferredLeaveForReplacement(controllerPointer);
            if (!Sessions.BeginPending(
                episodeMasterId,
                sceneIds,
                controllerPointer,
                decision.Reason,
                out var pending,
                servicePointer,
                apiClientPointer))
            {
                ClearForEntry(controller, episodeMasterId, $"{kind}-entry-pending-rejected");
                SafeLogWarning($"{kind}-entry episode={episodeMasterId} route=Native skip=pending-session-rejected reason={decision.Reason}");
                return;
            }

            SafeLogInfo($"session pending created episode={pending.EpisodeId} generation={pending.Generation} reason={pending.Reason} scenes=[{FormatSceneIds(pending.ExpectedSceneIds)}] episodeController={FormatPointer(pending.EpisodeController)} service={FormatPointer(pending.EpisodeServiceController)} api={FormatPointer(pending.ApiClientController)}");

            // A replacement entry invalidates any previously visible local
            // cells, but retains the current cell until native navigation has
            // observed its Viewable=true value.
            if (!RestoreStaleCells(new CellIdentity(controllerPointer, episodeMasterId)))
            {
                ClearForEntry(controller, episodeMasterId, $"{kind}-entry-stale-cell-restore-failed");
                SafeLogWarning($"{kind}-entry episode={episodeMasterId} route=Native skip=stale-cell-restore-failed");
                return;
            }

            // The initial d60/d61 MoveNext reads this distinct entry object as
            // soon as the prefix returns. Do every fallible route proof first so
            // fail-open paths keep the native bit without depending on rollback.
            var entryViewable = EligibilityPolicy.ResolveEntryViewable(captured, decision, originalEntryViewable);
            if (entryViewable != originalEntryViewable)
            {
                // Mark before the native setter so a setter that mutates then
                // throws is still restored by the fail-open finally block.
                entryVisibilityOverrideAttempted = true;
                args.Viewable = entryViewable;
                SafeLogInfo($"{kind}-entry episode={episodeMasterId} argsViewable={args.Viewable} entryViewablePromoted=true reason={decision.Reason}");
            }

            localRouteEstablished = true;
            SafeLogInfo($"{kind}-entry episode={episodeMasterId} route=LocalBypass pendingGeneration={pending.Generation} native-navigation=continue");
        }
        catch (Exception exception)
        {
            ClearForEntry(controller, episodeMasterId, $"{kind}-entry-exception");
            SafeLogException($"{kind} entry policy failed for episode {episodeMasterId}; native flow retained", exception);
        }
        finally
        {
            if (entryVisibilityOverrideAttempted && !localRouteEstablished)
            {
                if (RestoreVisibleCell(args, originalEntryViewable))
                {
                    SafeLogInfo($"{kind}-entry episode={episodeMasterId} entryViewableRestored={originalEntryViewable} route=Native");
                }
                else
                {
                    SafeLogWarning($"{kind}-entry episode={episodeMasterId} entryViewableRestoreFailed route=Native");
                }
            }
        }
    }

    private static EligibilityDecision ReadCapturedDecision(IntPtr controllerPointer, long episodeMasterId, out bool captured)
    {
        lock (Gate)
        {
            captured = CatalogDecisions.TryGetValue(new CatalogKey(controllerPointer, episodeMasterId), out var decision);
            return captured
                ? decision
                : new EligibilityDecision(PlaybackMode.Normal, BypassReason.IncompleteEvidence);
        }
    }

    private static void Capture(
        Il2CppObjectBase controller,
        long episodeMasterId,
        EpisodeComponent.BaseEpisodeCellArgs args,
        bool originalViewable,
        EligibilityDecision decision)
    {
        var controllerPointer = Pointer(controller);
        var key = new CatalogKey(controllerPointer, episodeMasterId);
        var cellKey = new CellIdentity(controllerPointer, episodeMasterId);
        // The caller restores any previous wrapper before reading the native
        // value. This prevents a regenerated cell from treating our prior
        // Viewable=true override as native truth. The registry owns args
        // strongly until the native restoration succeeds.
        var localVisible = VisibleCells.Capture(cellKey, args, originalViewable, decision, out var viewable);
        if (decision.IsLocalBypass && !localVisible && VisibleCells.Contains(cellKey))
        {
            throw new InvalidOperationException("cell visibility lease is still active");
        }

        lock (Gate)
        {
            CatalogDecisions[key] = decision;
        }

        if (localVisible)
        {
            try
            {
                args.Viewable = viewable;
            }
            catch
            {
                // Do not discard the strong lease before restoration succeeds.
                // The outer factory catch performs the remaining fail-open
                // cleanup and may retry the same native setter.
                RestoreCell(cellKey);
                throw;
            }
        }
    }

    private static void CaptureCharacterPresentation(
        CellIdentity identity,
        CharacterEpisodeCell.CharacterEpisodeCellArgs args,
        bool originalHasCharacter,
        int originalAffectionLevel,
        int requiredAffectionLevel)
    {
        var snapshot = new CharacterPresentationSnapshot(
            identity,
            args,
            originalHasCharacter,
            originalAffectionLevel,
            Math.Max(originalAffectionLevel, requiredAffectionLevel));
        lock (Gate)
        {
            CharacterPresentations[identity] = snapshot;
        }

        try
        {
            SetCharacterPresentation(
                args,
                hasCharacter: true,
                affectionLevel: Math.Max(originalAffectionLevel, requiredAffectionLevel));
        }
        catch
        {
            RestoreCharacterPresentation(identity);
            throw;
        }
    }

    private static bool TryReadMemoryRelease(
        IMemoryDB memoryDB,
        long episodeMasterId,
        out bool isReleased,
        out bool hasMemoryIdentity)
    {
        isReleased = false;
        hasMemoryIdentity = false;
        if (memoryDB is null || episodeMasterId <= 0)
        {
            return false;
        }

        try
        {
            var episode = memoryDB.EpisodesMaster[episodeMasterId];
            var memoryMaster = episode?.MemoryMaster;
            if (memoryMaster is null)
            {
                return true;
            }

            // Full ISIL EpisodeController.txt:3051-3102 uses the
            // EpisodeMaster.MemoryMasterId backing field to reach the
            // MemoryMaster release row. The generated nullable getter/Value is
            // unstable in this IL2CPP build, so use the stable relation object
            // and its release bit; relation presence is the identity proof.
            hasMemoryIdentity = true;
            isReleased = memoryMaster.IsReleased;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryResolveSceneIds(
        IMemoryDB memoryDB,
        long episodeMasterId,
        out List<long> sceneIds)
    {
        sceneIds = new List<long>();
        if (memoryDB is null || episodeMasterId <= 0)
        {
            SafeLogWarning($"scene identity resolve episode={episodeMasterId} failed reason=memory-db-or-episode-id-incomplete");
            return false;
        }

        var episode = memoryDB.EpisodesMaster[episodeMasterId];
        var sceneTable = memoryDB.ScenesMaster;
        var scenesByEpisode = sceneTable?.GetByEpisodeMasterId;
        if (episode is null || sceneTable is null || scenesByEpisode is null)
        {
            SafeLogWarning($"scene identity resolve episode={episodeMasterId} failed reason=master-table-missing episodePresent={episode is not null} sceneTablePresent={sceneTable is not null} relationPresent={scenesByEpisode is not null}");
            return false;
        }

        // The generated Il2Cpp IEnumerable implementation deliberately exposes
        // an Il2Cpp IEnumerator whose MoveNext is not a managed foreach pattern.
        // The persisted SceneMasterMemoryTable relation is instead exposed as
        // GetByEpisodeMasterId; use that runtime Master relation and its public
        // indexed list without embedding any Scene IDs.
        var relatedScenes = scenesByEpisode[episodeMasterId];
        if (relatedScenes is null)
        {
            SafeLogWarning($"scene identity resolve episode={episodeMasterId} failed reason=no-episode-scene-relation");
            return false;
        }

        for (var index = 0; index < relatedScenes.Count; index++)
        {
            var scene = relatedScenes[index];
            if (scene is null || scene.EpisodeMasterId != episodeMasterId)
            {
                continue;
            }

            if (scene.Id > 0 && !sceneIds.Contains(scene.Id))
            {
                sceneIds.Add(scene.Id);
            }
        }

        if (sceneIds.Count == 0)
        {
            SafeLogWarning($"scene identity resolve episode={episodeMasterId} failed reason=no-positive-scene-ids relatedCount={relatedScenes.Count}");
            return false;
        }

        SafeLogInfo($"scene identity resolve episode={episodeMasterId} scenes=[{FormatSceneIds(sceneIds)}]");
        return true;
    }

    private static bool TryPrepareScenes(
        IReadOnlyList<long> sceneIds,
        out SceneFrameResolution[] preparations)
    {
        preparations = Array.Empty<SceneFrameResolution>();
        if (_sceneFrames is null)
        {
            return false;
        }

        try
        {
            return _sceneFrames.TryPrepareAll(sceneIds, out preparations);
        }
        catch (Exception exception)
        {
            SafeLogException("SceneFrame source preparation failed; native lock retained", exception);
            return false;
        }
    }

    private static bool HasUnavailablePreparation(
        IReadOnlyList<SceneFrameResolution> preparations)
    {
        if (preparations is null || preparations.Count == 0)
        {
            return true;
        }

        for (var index = 0; index < preparations.Count; index++)
        {
            if (!preparations[index].IsAvailable)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Main catalog visibility may use remote manifest membership or a
    /// validated local document. Entry preparation always calls TryPrepareScenes
    /// and validates every body before creating a generation.
    /// </summary>
    private static bool HasAvailableScene(IReadOnlyList<long> sceneIds)
    {
        if (sceneIds is null || sceneIds.Count == 0 || _sceneFrames is null)
        {
            return false;
        }

        try
        {
            for (var index = 0; index < sceneIds.Count; index++)
            {
                if (!_sceneFrames.HasPotentialScene(sceneIds[index]))
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception exception)
        {
            SafeLogException("SceneFrame catalog availability read failed; native row retained", exception);
            return false;
        }
    }

    private static string FormatUnavailableScenes(
        IReadOnlyList<SceneFrameResolution> preparations)
    {
        if (preparations is null || preparations.Count == 0)
        {
            return "none";
        }

        var values = new List<string>();
        for (var index = 0; index < preparations.Count; index++)
        {
            var preparation = preparations[index];
            if (!preparation.IsAvailable)
            {
                values.Add($"{preparation.SceneId}({preparation.Status})");
            }
        }

        return values.Count == 0 ? "none" : string.Join(",", values);
    }

    private static bool HasAvailableChapter(IMemoryDB memoryDB, long chapterMasterId)
    {
        if (memoryDB is null || chapterMasterId <= 0)
        {
            return false;
        }

        var byChapter = memoryDB.EpisodesMaster?.GetByChapterMasterId;
        if (byChapter is null || !byChapter.ContainsKey(chapterMasterId))
        {
            return false;
        }

        var episodes = byChapter[chapterMasterId];
        if (episodes is null)
        {
            return false;
        }

        for (var index = 0; index < episodes.Count; index++)
        {
            var episode = episodes[index];
            if (episode is not null
                && TryResolveSceneIds(memoryDB, episode.Id, out var sceneIds)
                && HasAvailableScene(sceneIds))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasChapterSceneIdentity(IMemoryDB memoryDB, long chapterMasterId)
    {
        if (memoryDB is null || chapterMasterId <= 0)
        {
            return false;
        }

        var byChapter = memoryDB.EpisodesMaster?.GetByChapterMasterId;
        if (byChapter is null || !byChapter.ContainsKey(chapterMasterId))
        {
            return false;
        }

        var episodes = byChapter[chapterMasterId];
        if (episodes is null)
        {
            return false;
        }

        for (var index = 0; index < episodes.Count; index++)
        {
            var episode = episodes[index];
            if (episode is not null && TryResolveSceneIds(memoryDB, episode.Id, out _))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasAvailableChapterGroup(IMemoryDB memoryDB, long chapterGroupMasterId)
    {
        if (memoryDB is null || chapterGroupMasterId <= 0)
        {
            return false;
        }

        var chapterGroups = memoryDB.ChapterGroupsMaster;
        if (chapterGroups is null || !chapterGroups.Contains(chapterGroupMasterId))
        {
            return false;
        }

        var chapterGroup = chapterGroups[chapterGroupMasterId];
        var chapters = chapterGroup?.ChaptersMaster;
        if (chapters is null)
        {
            return false;
        }

        // ChapterGroupMaster.ChaptersMaster is the exact generated relation
        // getter (evidence/decomp/interop-src/Api/Assets.Api.Client/
        // ChapterGroupMaster.cs:386-398). Do not index MemoryDB.ChaptersMaster
        // with an ordinal: that KeyedCollection<long,T> indexer treats the
        // value as a chapter ID and produced the live presentation exception.
        // The generated Il2Cpp IEnumerator interface does not expose the
        // managed MoveNext member to this assembly. Materialize through the
        // Il2Cpp LINQ adapter instead of indexing MemoryDB.ChaptersMaster by
        // ordinal (the original live bug).
        var chapterArray = Il2CppSystem.Linq.Enumerable.ToArray(chapters);
        if (chapterArray is null)
        {
            return false;
        }

        for (var index = 0; index < chapterArray.Length; index++)
        {
            var chapter = chapterArray[index];
            if (chapter is not null
                && chapter.Id > 0
                && HasAvailableChapter(memoryDB, chapter.Id))
            {
                return true;
            }
        }

        return false;
    }

    private static string BuildMissingSceneFrameMessage(
        long episodeMasterId,
        IReadOnlyList<long> expectedSceneIds,
        IReadOnlyList<SceneFrameResolution> preparations)
    {
        var statuses = new List<string>();
        if (expectedSceneIds is not null)
        {
            for (var index = 0; index < expectedSceneIds.Count; index++)
            {
                var sceneId = expectedSceneIds[index];
                if (sceneId <= 0)
                {
                    continue;
                }

                var preparation = preparations is not null && index < preparations.Count
                    ? preparations[index]
                    : default;
                statuses.Add(
                    $"{sceneId}: remote={preparation.RemoteStatus}, local={preparation.LocalStatus}, path=scene/{sceneId.ToString(System.Globalization.CultureInfo.InvariantCulture)}/scene.json");
            }
        }

        var statusText = statuses.Count == 0 ? "<未知>" : string.Join("；", statuses);
        return $"远端与本地 SceneFrame 均缺失或不兼容，无法播放此 Episode。\nEpisodeMasterId: {episodeMasterId}\n来源状态: {statusText}";
    }

    private static bool HasActiveScenario(PlaybackIdentity identity)
    {
        lock (Gate)
        {
            return identity.ScenarioController != IntPtr.Zero
                && _activeScenarioController != IntPtr.Zero
                && identity.ScenarioController == _activeScenarioController;
        }
    }

    private static bool RestoreScenarioSceneId(ScenarioController scenario, long originalSceneId)
    {
        if (originalSceneId <= 0)
        {
            return true;
        }

        if (scenario is null)
        {
            return false;
        }

        try
        {
            var currentSceneId = scenario.sceneMasterId;
            if (currentSceneId == 0)
            {
                scenario.sceneMasterId = originalSceneId;
                currentSceneId = scenario.sceneMasterId;
                SafeLogInfo($"scenario-branch scene restored scene={originalSceneId}");
            }

            return currentSceneId != 0;
        }
        catch (Exception exception)
        {
            SafeLogException($"scenario-branch scene restore threw originalScene={originalSceneId}", exception);
            return false;
        }
    }

    /// <summary>
    /// Captures and replaces only the transient BaseEpisodeCellArgs.LockMessage
    /// consumed by the native d60/d61 locked-entry branches. The public setter
    /// and backing-field fallback are both persisted in interop
    /// EpisodeComponent.cs:957-1033; no modal/navigation method is hooked.
    /// </summary>
    private static bool TrySetTemporaryLockMessage(
        CellIdentity identity,
        EpisodeComponent.BaseEpisodeCellArgs args,
        string message)
    {
        if (!identity.IsComplete || args is null || string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        var pointer = SafePointer(args);
        if (pointer == IntPtr.Zero)
        {
            // Without a stable native wrapper identity there is no safe
            // terminal restoration point; keep the original native message.
            return false;
        }

        string originalMessage;
        try
        {
            originalMessage = args.LockMessage;
        }
        catch
        {
            try
            {
                originalMessage = args._LockMessage_k__BackingField;
            }
            catch
            {
                return false;
            }
        }

        if (pointer != IntPtr.Zero)
        {
            lock (Gate)
            {
                if (TemporaryLockMessages.TryGetValue(pointer, out var existing))
                {
                    if (!ReferenceEquals(existing.Args, args))
                    {
                        return false;
                    }

                    // Preserve the first native value when a generated entry
                    // seam retries before d60/d61 reaches its terminal state.
                    originalMessage = existing.OriginalMessage;
                }

                TemporaryLockMessages[pointer] = new LockMessageSnapshot(
                    identity,
                    args,
                    originalMessage);
            }
        }

        try
        {
            SetLockMessage(args, message);
            return true;
        }
        catch (Exception exception)
        {
            lock (Gate)
            {
                if (TemporaryLockMessages.TryGetValue(pointer, out var current)
                    && ReferenceEquals(current.Args, args))
                {
                    TemporaryLockMessages.Remove(pointer);
                }
            }

            SafeLogException($"temporary local SceneFrame lock message failed episode={identity.EpisodeId}; native lock retained", exception);
            return false;
        }
    }

    private static void SetLockMessage(
        EpisodeComponent.BaseEpisodeCellArgs args,
        string message)
    {
        try
        {
            args.LockMessage = message;
        }
        catch
        {
            args._LockMessage_k__BackingField = message;
        }
    }

    private static bool RestoreTemporaryLockMessage(CellIdentity identity)
    {
        if (!identity.IsComplete)
        {
            return true;
        }

        IntPtr pointer = IntPtr.Zero;
        lock (Gate)
        {
            foreach (var pair in TemporaryLockMessages)
            {
                if (pair.Value.Identity == identity)
                {
                    pointer = pair.Key;
                    break;
                }
            }
        }

        return RestoreTemporaryLockMessage(pointer);
    }

    private static bool RestoreTemporaryLockMessage(EpisodeComponent.BaseEpisodeCellArgs args)
    {
        return RestoreTemporaryLockMessage(SafePointer(args));
    }

    private static bool RestoreTemporaryLockMessage(IntPtr pointer)
    {
        if (pointer == IntPtr.Zero)
        {
            return true;
        }

        LockMessageSnapshot snapshot;
        lock (Gate)
        {
            if (!TemporaryLockMessages.TryGetValue(pointer, out snapshot))
            {
                return true;
            }
        }

        try
        {
            SetLockMessage(snapshot.Args, snapshot.OriginalMessage);
        }
        catch (Exception exception)
        {
            SafeLogException($"temporary local SceneFrame lock message restore failed episode={snapshot.Identity.EpisodeId}; native wrapper retained", exception);
            return false;
        }

        lock (Gate)
        {
            if (TemporaryLockMessages.TryGetValue(pointer, out var current)
                && ReferenceEquals(current.Args, snapshot.Args))
            {
                TemporaryLockMessages.Remove(pointer);
            }
        }

        return true;
    }

    private static bool RestoreTemporaryLockMessagesExcept(CellIdentity keep)
    {
        IntPtr[] pointers;
        lock (Gate)
        {
            var selected = new List<IntPtr>();
            foreach (var pair in TemporaryLockMessages)
            {
                if (pair.Value.Identity != keep)
                {
                    selected.Add(pair.Key);
                }
            }

            pointers = selected.ToArray();
        }

        var restored = true;
        for (var index = 0; index < pointers.Length; index++)
        {
            restored &= RestoreTemporaryLockMessage(pointers[index]);
        }

        return restored;
    }

    private static bool RestoreAllTemporaryLockMessages()
    {
        IntPtr[] pointers;
        lock (Gate)
        {
            pointers = new IntPtr[TemporaryLockMessages.Count];
            TemporaryLockMessages.Keys.CopyTo(pointers, 0);
        }

        var restored = true;
        for (var index = 0; index < pointers.Length; index++)
        {
            restored &= RestoreTemporaryLockMessage(pointers[index]);
        }

        return restored;
    }

    private static bool RestoreCell(CellIdentity identity)
    {
        var lockMessageRestored = RestoreTemporaryLockMessage(identity);
        var eventUnlockRestored = RestoreEventUnlockPresentation(identity);
        var presentationRestored = RestoreCharacterPresentation(identity);
        var visibilityRestored = VisibleCells.TryRestore(identity, RestoreVisibleCell);
        return lockMessageRestored && eventUnlockRestored && presentationRestored && visibilityRestored;
    }

    private static bool RestoreStaleCells(CellIdentity keep)
    {
        var lockMessagesRestored = RestoreTemporaryLockMessagesExcept(keep);
        var eventUnlocksRestored = RestoreEventUnlockPresentationsExcept(keep);
        var presentationsRestored = RestoreCharacterPresentationsExcept(keep);
        var stale = VisibleCells.RestoreAllExcept(keep, RestoreVisibleCell);
        for (var index = 0; index < stale.Count; index++)
        {
            if (VisibleCells.Contains(stale[index].Identity))
            {
                return false;
            }
        }

        return lockMessagesRestored && eventUnlocksRestored && presentationsRestored;
    }

    private static bool RestoreAllCells()
    {
        var lockMessagesRestored = RestoreAllTemporaryLockMessages();
        var eventUnlocksRestored = RestoreAllEventUnlockPresentations();
        var presentationsRestored = RestoreAllCharacterPresentations();
        var visibilityRestored = VisibleCells.TryRestoreAll(RestoreVisibleCell);
        lock (Gate)
        {
            _activeScenarioController = IntPtr.Zero;
        }

        return lockMessagesRestored && eventUnlocksRestored && presentationsRestored && visibilityRestored;
    }

    private static bool TryGetLocalCellIdentity(object args, out CellIdentity identity)
    {
        identity = default;
        var resolved = args is not null && VisibleCells.TryGetIdentity(args, out identity);
        if (!resolved && args is EpisodeComponent.BaseEpisodeCellArgs cellArgs)
        {
            resolved = VisibleCells.TryGetUniqueIdentity(SafeEpisodeId(cellArgs), out identity);
        }

        if (resolved)
        {
            lock (Gate)
            {
                if (CatalogDecisions.TryGetValue(new CatalogKey(identity.EpisodeController, identity.EpisodeId), out var decision)
                    && decision.IsLocalBypass)
                {
                    return true;
                }
            }
        }

        identity = default;
        return false;
    }

    private static bool TryGetCharacterPresentation(
        CharacterEpisodeCell.CharacterEpisodeCellArgs args,
        out CharacterPresentationSnapshot snapshot)
    {
        if (TryGetLocalCellIdentity(args, out var identity))
        {
            lock (Gate)
            {
                if (CharacterPresentations.TryGetValue(identity, out snapshot))
                {
                    return true;
                }
            }
        }

        snapshot = default;
        return false;
    }

    private static bool RestoreEventUnlockPresentation(CellIdentity identity)
    {
        EventUnlockPresentationSnapshot snapshot;
        lock (Gate)
        {
            if (!EventUnlockPresentations.TryGetValue(identity, out snapshot))
            {
                return true;
            }
        }

        try
        {
            snapshot.Args.EventEpisodeUnlockArgs = snapshot.OriginalUnlockArgs;
        }
        catch (Exception exception)
        {
            SafeLogException($"event unlock presentation restore failed chapter={identity.EpisodeId}", exception);
            return false;
        }

        lock (Gate)
        {
            if (EventUnlockPresentations.TryGetValue(identity, out var current)
                && ReferenceEquals(current.Args, snapshot.Args))
            {
                EventUnlockPresentations.Remove(identity);
            }
        }

        return true;
    }

    private static bool RestoreEventUnlockPresentationsExcept(CellIdentity keep)
    {
        CellIdentity[] identities;
        lock (Gate)
        {
            identities = new CellIdentity[EventUnlockPresentations.Count];
            EventUnlockPresentations.Keys.CopyTo(identities, 0);
        }

        var restored = true;
        for (var index = 0; index < identities.Length; index++)
        {
            if (identities[index] != keep)
            {
                restored &= RestoreEventUnlockPresentation(identities[index]);
            }
        }

        return restored;
    }

    private static bool RestoreAllEventUnlockPresentations()
    {
        return RestoreEventUnlockPresentationsExcept(default);
    }

    private static bool RestoreCharacterPresentation(CellIdentity identity)
    {
        CharacterPresentationSnapshot snapshot;
        lock (Gate)
        {
            if (!CharacterPresentations.TryGetValue(identity, out snapshot))
            {
                return true;
            }
        }

        try
        {
            SetCharacterPresentation(
                snapshot.Args,
                snapshot.OriginalHasCharacter,
                snapshot.OriginalAffectionLevel);
        }
        catch (Exception exception)
        {
            SafeLogException($"character presentation restore failed episode={identity.EpisodeId}", exception);
            return false;
        }

        lock (Gate)
        {
            if (CharacterPresentations.TryGetValue(identity, out var current)
                && ReferenceEquals(current.Args, snapshot.Args))
            {
                CharacterPresentations.Remove(identity);
            }
        }

        return true;
    }

    private static bool RestoreCharacterPresentationsExcept(CellIdentity keep)
    {
        CellIdentity[] identities;
        lock (Gate)
        {
            identities = new CellIdentity[CharacterPresentations.Count];
            CharacterPresentations.Keys.CopyTo(identities, 0);
        }

        var restored = true;
        for (var index = 0; index < identities.Length; index++)
        {
            if (identities[index] != keep)
            {
                restored &= RestoreCharacterPresentation(identities[index]);
            }
        }

        return restored;
    }

    private static bool RestoreAllCharacterPresentations()
    {
        return RestoreCharacterPresentationsExcept(default);
    }

    private static void SetCharacterPresentation(
        CharacterEpisodeCell.CharacterEpisodeCellArgs args,
        bool hasCharacter,
        int affectionLevel)
    {
        try
        {
            args.HasCharacter = hasCharacter;
        }
        catch
        {
            args._HasCharacter_k__BackingField = hasCharacter;
        }

        try
        {
            args.AffectionLevel = affectionLevel;
        }
        catch
        {
            args._AffectionLevel_k__BackingField = affectionLevel;
        }
    }

    /// <summary>
    /// Restore through the ordinary Viewable accessor first. If a stale
    /// IL2CPP wrapper makes that generated method unavailable, the persisted
    /// interop source exposes the same native backing-field accessor at
    /// EpisodeComponent.cs lines 971-981. Both routes target the original
    /// native bit consumed by d60:418-421/d61:268-271; a double failure retains
    /// the strong registry lease for a later lifecycle retry.
    /// </summary>
    private static bool RestoreVisibleCell(object wrapper, bool originalViewable)
    {
        if (wrapper is not EpisodeComponent.BaseEpisodeCellArgs args)
        {
            return false;
        }

        try
        {
            args.Viewable = originalViewable;
            return true;
        }
        catch
        {
            try
            {
                args._Viewable_k__BackingField = originalViewable;
                return true;
            }
            catch (Exception exception)
            {
                SafeLogException("cell visibility restore failed", exception);
                return false;
            }
        }
    }

    private static IntPtr Pointer(Il2CppObjectBase instance)
    {
        return instance is null ? IntPtr.Zero : IL2CPP.Il2CppObjectBaseToPtr(instance);
    }

    private static IntPtr SafePointer(Il2CppObjectBase instance)
    {
        try
        {
            return Pointer(instance);
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    private static long SafeSceneId(ScenarioController controller)
    {
        try
        {
            return controller is null ? 0 : controller.sceneMasterId;
        }
        catch
        {
            return 0;
        }
    }

    private static long SafeEpisodeId(EpisodeComponent.BaseEpisodeCellArgs args)
    {
        try
        {
            return args is null ? 0 : args.Id;
        }
        catch
        {
            return 0;
        }
    }

    private static IntPtr PointerObject(object instance)
    {
        return instance is Il2CppObjectBase il2CppInstance
            ? Pointer(il2CppInstance)
            : IntPtr.Zero;
    }

    private static string DescribeState()
    {
        int characterPresentations;
        int temporaryLockMessages;
        lock (Gate)
        {
            characterPresentations = CharacterPresentations.Count;
            temporaryLockMessages = TemporaryLockMessages.Count;
        }

        return $"{DescribeSessionState()} visibility={VisibleCells.Count} characterPresentation={characterPresentations} temporaryLockMessages={temporaryLockMessages} activeProvenance={ExecutionProvenance.ActiveCount} startFrames={ExecutionProvenance.StartFrameCount}";
    }

    private static string SafeDescribeState()
    {
        try
        {
            return DescribeState();
        }
        catch
        {
            return "unavailable";
        }
    }

    private static string DescribeSessionState()
    {
        var bound = Sessions.Bound;
        if (bound is not null)
        {
            return $"Bound(episode={bound.EpisodeId},scene={bound.SceneId},generation={bound.Generation})";
        }

        var pending = Sessions.Pending;
        if (pending is not null)
        {
            return $"Pending(episode={pending.EpisodeId},generation={pending.Generation},scenes=[{FormatSceneIds(pending.ExpectedSceneIds)}])";
        }

        return "Empty";
    }

    private static string SafeDescribeSessionState()
    {
        try
        {
            return DescribeSessionState();
        }
        catch
        {
            return "unavailable";
        }
    }

    private static string FormatPointer(IntPtr pointer)
    {
        return pointer == IntPtr.Zero
            ? "0"
            : $"0x{pointer.ToInt64():X}";
    }

    private static string FormatSceneIds(IReadOnlyList<long> sceneIds)
    {
        return sceneIds is null
            ? "none"
            : string.Join(",", sceneIds);
    }

    private static void SafeLogInfo(string message)
    {
        try
        {
            _log?.LogInfo($"{DiagnosticPrefix} {message}");
        }
        catch
        {
            // Diagnostics must never change native behavior.
        }
    }

    private static void SafeLogWarning(string message)
    {
        try
        {
            _log?.LogWarning($"{DiagnosticPrefix} {message}");
        }
        catch
        {
            // Diagnostics must never change native behavior.
        }
    }

    private static void SafeLogException(string message, Exception exception)
    {
        SafeLogError(message, exception);
    }

    private static void SafeLogError(string message, Exception exception)
    {
        try
        {
            var exceptionType = exception?.GetType().FullName ?? "none";
            var stack = exception?.StackTrace ?? "none";
            _log?.LogError($"{DiagnosticPrefix} {message}; exception={exceptionType}; stack={stack}");
        }
        catch
        {
            // Diagnostics must never change native behavior.
        }
    }

    private readonly record struct CatalogKey(IntPtr Controller, long EpisodeId);

    private readonly record struct CharacterPresentationSnapshot(
        CellIdentity Identity,
        CharacterEpisodeCell.CharacterEpisodeCellArgs Args,
        bool OriginalHasCharacter,
        int OriginalAffectionLevel,
        int LocalAffectionLevel);

    private readonly record struct EventUnlockPresentationSnapshot(
        Assets.GameUi.Episode.EventEpisodeCell.EventEpisodeCellArgs Args,
        Assets.GameUi.Episode.EventEpisodeCell.EventEpisodeUnlockArgs OriginalUnlockArgs);

    private readonly record struct LockMessageSnapshot(
        CellIdentity Identity,
        EpisodeComponent.BaseEpisodeCellArgs Args,
        string OriginalMessage);
}
