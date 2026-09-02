using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MuvluvUnlockCG.Core;

namespace MuvluvUnlockCG.Tests;

internal static class Program
{
    private static int _passed;

    private static void Main()
    {
        Run("character eligibility truth table", CharacterEligibilityTruthTable);
        Run("memory eligibility truth table", MemoryEligibilityTruthTable);
        Run("story eligibility truth table", StoryEligibilityTruthTable);
        Run("side effect matrix", SideEffectMatrix);
        Run("cell decisions preserve native fields", CellDecisionsPreserveNativeFields);
        Run("character and memory source availability gate", CharacterAndMemorySourceAvailabilityGate);
        Run("entry visibility promotion policy", EntryVisibilityPromotionPolicy);
        Run("cell visibility resolves only unique episode lease", CellVisibilityUniqueEpisodeLease);
        Run("cell visibility restores on fail-open lifecycle paths", CellVisibilityRestorationLifecycle);
        Run("cell visibility strong lease and setter failure", CellVisibilityStrongLeaseAndSetterFailure);
        Run("unload visibility cleanup retry", UnloadVisibilityCleanupRetry);
        Run("branch local policy keeps remote operation denied", BranchLocalPolicy);
        Run("session pending and multi-scene binding", SessionPendingAndSceneBinding);
        Run("local scene content matches only active generation", LocalSceneContentMatching);
        Run("session identity and generation matching", SessionIdentityAndGenerationMatching);
        Run("session replacement leave and unload cleanup", SessionCleanup);
        Run("incomplete identity fails open", IncompleteIdentityFailsOpen);
        Run("scenario provenance start frames and clear", ScenarioProvenanceStartFrames);
        Run("G4 scene document maps lossless frame fields", G4SceneDocumentMapping);
        Run("remote scene source hit and process cache", RemoteSceneSourceHitAndCache);
        Run("remote scene source fallback statuses", RemoteSceneSourceFallbackStatuses);
        Run("remote scene source prepare gate", RemoteSceneSourcePrepareGate);
        Console.WriteLine($"tests: {_passed}/22 passed");
    }

    private static void Run(string name, Action test)
    {
        try
        {
            test();
            _passed++;
            Console.WriteLine($"PASS {name}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL {name}: {ex.Message}");
            Environment.Exit(1);
        }
    }

    private static void CharacterEligibilityTruthTable()
    {
        var unowned = EligibilityPolicy.ForCharacter(new CharacterEligibilityFacts(101, false, 0, 10));
        Equal(PlaybackMode.LocalBypass, unowned.Mode);
        Equal(BypassReason.UnownedCharacter, unowned.Reason);

        var lowAffection = EligibilityPolicy.ForCharacter(new CharacterEligibilityFacts(102, true, 9, 10));
        Equal(PlaybackMode.LocalBypass, lowAffection.Mode);
        Equal(BypassReason.AffectionBelowRequirement, lowAffection.Reason);

        var exact = EligibilityPolicy.ForCharacter(new CharacterEligibilityFacts(103, true, 10, 10));
        Equal(PlaybackMode.Normal, exact.Mode);
        Equal(BypassReason.None, exact.Reason);

        var above = EligibilityPolicy.ForCharacter(new CharacterEligibilityFacts(104, true, 11, 10));
        Equal(PlaybackMode.Normal, above.Mode);
    }

    private static void MemoryEligibilityTruthTable()
    {
        var owned = EligibilityPolicy.ForMemory(new MemoryEligibilityFacts(201, true, true, true));
        Equal(PlaybackMode.Normal, owned.Mode);

        var releasedUnowned = EligibilityPolicy.ForMemory(new MemoryEligibilityFacts(202, false, true, true));
        Equal(PlaybackMode.LocalBypass, releasedUnowned.Mode);
        Equal(BypassReason.UnownedMemory, releasedUnowned.Reason);

        var unreleased = EligibilityPolicy.ForMemory(new MemoryEligibilityFacts(203, false, false, true));
        Equal(PlaybackMode.Normal, unreleased.Mode);

        var missingIdentity = EligibilityPolicy.ForMemory(new MemoryEligibilityFacts(0, false, true, false));
        Equal(PlaybackMode.Normal, missingIdentity.Mode);
        Equal(BypassReason.IncompleteEvidence, missingIdentity.Reason);
    }

    private static void StoryEligibilityTruthTable()
    {
        // Evidence: MainEpisodeController.txt:846-940 and
        // EventChapterController.txt:1343-1445 use runtime Master rows; local
        // promotion is allowed only when a hidden row has a released/local
        // content proof. The production adapter supplies LocalSceneFrame
        // availability from the configured corpus and never an ID list.
        var local = EligibilityPolicy.ForStoryCell(new StoryCellFacts(210, false, false, true));
        Equal(PlaybackMode.LocalBypass, local.Eligibility.Mode);
        Equal(BypassReason.LocallyAvailableStory, local.Eligibility.Reason);
        True(local.Viewable, "locally available story is visible");

        // EventChapterController.GenerateEpisodeCellArgs selects every runtime
        // chapter EpisodeMaster without a local-file filter (persisted full
        // ISIL :1343-1445). A runtime-released Event row therefore stays
        // playable while its SceneFrame provider may fall back to native fetch.
        var eventNativeContent = EligibilityPolicy.ForStoryCell(new StoryCellFacts(211, false, true, false));
        Equal(PlaybackMode.LocalBypass, eventNativeContent.Eligibility.Mode);
        True(eventNativeContent.Viewable, "released event story may use native content acquisition");

        var nativeVisible = EligibilityPolicy.ForStoryCell(new StoryCellFacts(212, true, false, true));
        Equal(PlaybackMode.Normal, nativeVisible.Eligibility.Mode);
        True(nativeVisible.Viewable, "native visible story stays native");

        var unavailable = EligibilityPolicy.ForStoryCell(new StoryCellFacts(213, false, false, false));
        Equal(PlaybackMode.Normal, unavailable.Eligibility.Mode);
        False(unavailable.Viewable, "unavailable story keeps native lock");

        var incomplete = EligibilityPolicy.ForStoryCell(new StoryCellFacts(0, false, true, true));
        Equal(PlaybackMode.Normal, incomplete.Eligibility.Mode);
        Equal(BypassReason.IncompleteEvidence, incomplete.Eligibility.Reason);
    }

    private static void SideEffectMatrix()
    {
        // Evidence: selected EpisodeService PostRead_d__16 and ScenarioController
        // PostBranchSelection_d__103 establish the two business boundaries;
        // SceneFrame content remains a native read in either mode.
        foreach (var operation in Enum.GetValues<PlaybackOperation>())
        {
            True(SideEffectPolicy.Allows(PlaybackMode.Normal, operation), $"normal must allow {operation}");
        }

        var allowed = new[]
        {
            PlaybackOperation.SceneFrameContent,
            PlaybackOperation.AssetContent,
            PlaybackOperation.LocalScenarioState,
        };
        foreach (var operation in allowed)
        {
            True(SideEffectPolicy.Allows(PlaybackMode.LocalBypass, operation), $"local must allow {operation}");
        }

        var denied = new[]
        {
            PlaybackOperation.EpisodeReadApi,
            PlaybackOperation.MemoryDbMutation,
            PlaybackOperation.RewardProgress,
            PlaybackOperation.BranchSelectionRemote,
            PlaybackOperation.EpisodeTracking,
        };
        foreach (var operation in denied)
        {
            False(SideEffectPolicy.Allows(PlaybackMode.LocalBypass, operation), $"local must deny {operation}");
        }
    }

    private static void CellDecisionsPreserveNativeFields()
    {
        // Evidence: selected EpisodeController <GenerateCharacterCellArgs>b__54_0
        // is the individual factory seam. LocalBypass may set only Viewable.
        var nativeCharacter = EligibilityPolicy.ForCharacterCell(
            new CharacterCellFacts(301, false, false, 9, 10, LocallyAvailable: true));
        Equal(PlaybackMode.LocalBypass, nativeCharacter.Eligibility.Mode);
        True(nativeCharacter.Viewable, "local character row is viewable");
        Equal(false, nativeCharacter.OriginalViewable);
        Equal(301L, nativeCharacter.EpisodeId);

        var nativeNormal = EligibilityPolicy.ForCharacterCell(
            new CharacterCellFacts(302, true, true, 10, 10));
        Equal(PlaybackMode.Normal, nativeNormal.Eligibility.Mode);
        True(nativeNormal.Viewable, "normal character keeps native visibility");

        var nativeNormalHidden = EligibilityPolicy.ForCharacterCell(
            new CharacterCellFacts(305, false, true, 10, 10));
        Equal(PlaybackMode.Normal, nativeNormalHidden.Eligibility.Mode);
        False(nativeNormalHidden.Viewable, "normal character does not gain visibility");

        // Evidence: selected EpisodeController <GenerateMemoryCellArgs>b__56_1
        // plus b__56_0 keeps the native release boundary independent of bypass.
        var releasedMemory = EligibilityPolicy.ForMemoryCell(
            new MemoryCellFacts(303, false, true, true, LocallyAvailable: true));
        Equal(PlaybackMode.LocalBypass, releasedMemory.Eligibility.Mode);
        True(releasedMemory.IncludeInCatalog, "released memory remains in catalog");
        True(releasedMemory.Viewable, "released local memory is viewable");

        var unreleasedMemory = EligibilityPolicy.ForMemoryCell(
            new MemoryCellFacts(304, false, false, true));
        Equal(PlaybackMode.Normal, unreleasedMemory.Eligibility.Mode);
        False(unreleasedMemory.IncludeInCatalog, "unreleased memory remains filtered");
        False(unreleasedMemory.Viewable, "unreleased memory keeps native visibility");
    }

    private static void CharacterAndMemorySourceAvailabilityGate()
    {
        // Character and Memory rows require every expected SceneFrame from a
        // plugin source before navigation. A double miss keeps the native lock;
        // the native downloader may return an empty array that Refresh cannot
        // consume (selected DownloadSceneFrameMasters d15:711-715 and
        // ScenarioController Refresh:632-637).
        var characterMissingSource = EligibilityPolicy.ForCharacterCell(
            new CharacterCellFacts(306, false, false, 0, 1, LocallyAvailable: false));
        Equal(PlaybackMode.Normal, characterMissingSource.Eligibility.Mode);
        Equal(BypassReason.IncompleteEvidence, characterMissingSource.Eligibility.Reason);
        False(characterMissingSource.Viewable, "character without SceneFrame source stays locked");
        var ownedCharacterMissingSource = EligibilityPolicy.ForCharacterCell(
            new CharacterCellFacts(308, false, true, 1, 10, LocallyAvailable: false));
        Equal(PlaybackMode.Normal, ownedCharacterMissingSource.Eligibility.Mode);
        Equal(BypassReason.IncompleteEvidence, ownedCharacterMissingSource.Eligibility.Reason);
        False(ownedCharacterMissingSource.Viewable, "owned affection bypass requires a plugin SceneFrame source");

        var characterAvailable = EligibilityPolicy.ForCharacterCell(
            new CharacterCellFacts(306, false, false, 0, 1, LocallyAvailable: true));
        Equal(PlaybackMode.LocalBypass, characterAvailable.Eligibility.Mode);
        True(characterAvailable.Viewable, "character with SceneFrame source is visible");

        var memoryMissingSource = EligibilityPolicy.ForMemoryCell(
            new MemoryCellFacts(307, false, true, true, LocallyAvailable: false));
        Equal(PlaybackMode.Normal, memoryMissingSource.Eligibility.Mode);
        Equal(BypassReason.IncompleteEvidence, memoryMissingSource.Eligibility.Reason);
        True(memoryMissingSource.IncludeInCatalog, "released memory remains under native release filtering");
        False(memoryMissingSource.Viewable, "memory without SceneFrame source stays locked");

        var memoryAvailable = EligibilityPolicy.ForMemoryCell(
            new MemoryCellFacts(307, false, true, true, LocallyAvailable: true));
        Equal(PlaybackMode.LocalBypass, memoryAvailable.Eligibility.Mode);
        True(memoryAvailable.Viewable, "memory with SceneFrame source is visible");
    }

    private static void EntryVisibilityPromotionPolicy()
    {
        // Evidence: persisted ISIL
        // evidence/decomp/full-isil/IsilDump/GameUi/Assets/GameUi/Episode/
        // EpisodeController_NestedType__MoveToAdventure_d__60.txt:399-421 and
        // ...d__61.txt:249-271 read the entry state's own args.Viewable before
        // the native route. The entry object is distinct from the catalog
        // factory object, so only an exact captured LocalBypass decision may
        // promote its value.
        var bypass = new EligibilityDecision(PlaybackMode.LocalBypass, BypassReason.UnownedCharacter);
        True(
            EligibilityPolicy.ResolveEntryViewable(true, bypass, false),
            "captured LocalBypass promotes a hidden entry");

        var normal = new EligibilityDecision(PlaybackMode.Normal, BypassReason.None);
        False(
            EligibilityPolicy.ResolveEntryViewable(true, normal, false),
            "captured Normal preserves a hidden entry");

        var missing = new EligibilityDecision(PlaybackMode.Normal, BypassReason.IncompleteEvidence);
        False(
            EligibilityPolicy.ResolveEntryViewable(false, missing, false),
            "missing decision preserves a hidden entry");
        True(
            EligibilityPolicy.ResolveEntryViewable(false, missing, true),
            "missing decision preserves a visible entry");
    }

    private static void SessionPendingAndSceneBinding()
    {
        // Evidence: selected EpisodeController MoveToAdventure d__60/d__61 and
        // ScenarioController Refresh d__76 require a pending generation to bind
        // only after an expected runtime SceneMasterId is observed.
        var registry = new LocalSessionRegistry();
        True(registry.BeginPending(401, new[] { 501L, 502L }, (IntPtr)11, BypassReason.UnownedCharacter, out var pending, (IntPtr)31, (IntPtr)41), "pending created");
        Equal(SessionState.Pending, registry.State);
        Equal(401L, pending.EpisodeId);
        var incompletePendingIdentity = new PlaybackIdentity(401, 501, (IntPtr)11, (IntPtr)21, pending.Generation);
        False(registry.TryMatchBranch(incompletePendingIdentity, (IntPtr)41), "pending cannot suppress branch API");
        False(registry.TryMatchEpisodeService(incompletePendingIdentity, (IntPtr)31), "pending cannot suppress service calls");
        False(registry.TryBind(401, 999, (IntPtr)11, (IntPtr)21, out _), "unexpected scene does not bind");
        Equal(SessionState.Pending, registry.State);
        True(registry.TryBind(401, 502, (IntPtr)11, (IntPtr)21, out var bound), "expected scene binds");
        Equal(SessionState.Bound, registry.State);
        Equal(502L, bound.SceneId);
        Equal(pending.Generation, bound.Generation);
        True(registry.TryBind(401, 501, (IntPtr)11, (IntPtr)21, out var advanced), "same generation advances to another expected scene");
        Equal(501L, advanced.SceneId);
        Equal(pending.Generation, advanced.Generation);
        False(registry.TryBind(401, 999, (IntPtr)11, (IntPtr)21, out _), "unexpected bound scene does not advance");
        Equal(SessionState.Bound, registry.State);

        var storyRegistry = new LocalSessionRegistry();
        True(
            storyRegistry.BeginPending(
                402,
                new[] { 601L },
                (IntPtr)12,
                BypassReason.LocallyAvailableStory,
                out var storyPending),
            "story pending can attach service and API during Scenario.Refresh");
        Equal(IntPtr.Zero, storyPending.EpisodeServiceController);
        Equal(IntPtr.Zero, storyPending.ApiClientController);
        True(
            storyRegistry.TryBind(402, 601, (IntPtr)12, (IntPtr)22, out var storyBound, (IntPtr)31, (IntPtr)41),
            "story pending binds runtime Scenario identities");
        Equal((IntPtr)31, storyBound.EpisodeServiceController);
        Equal((IntPtr)41, storyBound.ApiClientController);
    }

    private static void LocalSceneContentMatching()
    {
        // Evidence: selected DownloadSceneFrameMasters d__15 checks its local
        // dictionary before GetApiEpisodeScenesDataSource (:542-624), then feeds
        // the returned SceneFrameMaster[] directly to Scenario refresh.
        var registry = new LocalSessionRegistry();
        False(registry.BeginPending(410, new[] { 510L }, (IntPtr)10, BypassReason.None, out _, (IntPtr)30, (IntPtr)40), "Normal reason cannot create local session");
        False(registry.BeginPending(410, new[] { 510L }, (IntPtr)10, BypassReason.IncompleteEvidence, out _, (IntPtr)30, (IntPtr)40), "incomplete reason cannot create local session");
        True(registry.BeginPending(410, new[] { 510L, 511L }, (IntPtr)10, BypassReason.UnownedCharacter, out _, (IntPtr)30, (IntPtr)40), "pending created");
        True(registry.TryBeginSceneContent(510, (IntPtr)30, out var pendingLease), "expected pending scene and exact service match");
        True(registry.TryConfirmSceneContent(pendingLease), "pending lease confirms");
        False(registry.TryBeginSceneContent(999, (IntPtr)30, out _), "unexpected pending scene fails open");
        False(registry.TryBeginSceneContent(510, (IntPtr)31, out _), "different service fails open");
        True(registry.TryBind(410, 510, (IntPtr)10, (IntPtr)20, out _), "pending binds");
        True(registry.TryBeginSceneContent(511, (IntPtr)30, out var boundLease), "bound generation keeps its expected next scene local");
        True(registry.TryConfirmSceneContent(boundLease), "bound lease confirms");
        True(registry.BeginPending(410, new[] { 510L, 511L }, (IntPtr)10, BypassReason.UnownedMemory, out _, (IntPtr)30, (IntPtr)40), "replacement generation starts");
        False(registry.TryConfirmSceneContent(boundLease), "replacement invalidates stale lease");
        True(registry.TryBeginSceneContent(511, (IntPtr)30, out var replacementLease), "replacement scene begins");
        True(registry.TryConfirmSceneContent(replacementLease), "replacement lease confirms");
        registry.ClearAll();
        False(registry.TryConfirmSceneContent(replacementLease), "cleared generation cannot provide content");

        var storyRegistry = new LocalSessionRegistry();
        True(storyRegistry.BeginPending(420, new[] { 520L }, (IntPtr)12, BypassReason.LocallyAvailableStory, out _), "story pending starts before service identity exists");
        True(storyRegistry.TryBeginSceneContent(520, (IntPtr)32, out var storyLease), "first story provider attaches its exact service identity");
        True(storyRegistry.TryConfirmSceneContent(storyLease), "attached story provider lease confirms");
        False(storyRegistry.TryBeginSceneContent(520, (IntPtr)33, out _), "a second story service cannot borrow the attached generation");
        True(storyRegistry.TryBind(420, 520, (IntPtr)12, (IntPtr)22, out var storyBound, (IntPtr)32, (IntPtr)42), "story generation binds after provider-before-refresh");
        Equal((IntPtr)32, storyBound.EpisodeServiceController);
        Equal((IntPtr)42, storyBound.ApiClientController);

        var affectionRegistry = new LocalSessionRegistry();
        True(
            affectionRegistry.BeginPending(
                430,
                new[] { 530L },
                (IntPtr)13,
                BypassReason.AffectionBelowRequirement,
                out _,
                (IntPtr)33,
                (IntPtr)43),
            "owned affection bypass starts after plugin content preparation");
        True(
            affectionRegistry.TryBeginSceneContent(530, (IntPtr)33, out _),
            "prepared plugin SceneFrame content matches the affection session");
        True(
            affectionRegistry.TryBind(430, 530, (IntPtr)13, (IntPtr)23, out _),
            "prepared affection bypass binds the LocalBypass session");
    }

    private static void G4SceneDocumentMapping()
    {
        // Evidence: original SceneFrameMaster.cs has the five fields below; the
        // persisted full-corpus comparison found zero mismatches for every cache
        // overlap after parsing configuration JSON.
        const string document = "{\"assets\":[],\"commands\":["
            + "{\"type\":\"text\",\"text\":\"ignored\"},"
            + "{\"type\":\"muvluvFrame\",\"frame\":{\"order\":7,\"sceneId\":40003301,"
            + "\"branchId\":null,\"selectedBranchId\":9,\"configuration\":{\"Phrase\":{\"Text\":\"fixture\"}},"
            + "\"background\":{},\"characters\":[],\"needsHideText\":false}}"
            + "],\"id\":\"40003301\",\"notes\":[],\"preloaded\":false,\"title\":\"fixture\"}";
        True(G4SceneFrameDocument.TryParse(40003301, document, out var frames), "compatible scene parses");
        Equal(1, frames.Length);
        Equal(7, frames[0].Order);
        Equal(40003301L, frames[0].SceneId);
        Equal(null, frames[0].BranchId);
        Equal(9L, frames[0].SelectedBranchId);
        True(frames[0].ConfigurationJson.Contains("\"Phrase\"", StringComparison.Ordinal), "configuration remains JSON");
        False(G4SceneFrameDocument.TryParse(40003302, document, out _), "mismatched Scene ID fails closed");
        False(G4SceneFrameDocument.TryParse(40003301, "{}", out _), "incomplete scene fails closed");
    }

    private static void CellVisibilityRestorationLifecycle()
    {
        // Evidence: selected EpisodeController MoveToAdventure d__60/d__61
        // lines d60:418-421 and d61:268-271 branch on the same Viewable bit
        // changed by the b54_0/b56_1 cell factories. Every bypass path must
        // therefore restore the captured native value before native continuation.
        var registry = new CellVisibilityRegistry();
        var key = new CellIdentity((IntPtr)91, 901);
        var local = new EligibilityDecision(PlaybackMode.LocalBypass, BypassReason.UnownedCharacter);
        var normalHidden = new EligibilityDecision(PlaybackMode.Normal, BypassReason.None);
        var cell = new FakeCell { Viewable = false };
        var viewable = false;

        True(registry.Capture(key, cell, originalViewable: false, local, out viewable), "local cell is captured");
        cell.Viewable = viewable;
        True(viewable, "local cell is made visible");

        // Disabled after the factory ran: restore the original hidden state.
        True(registry.TryRestore(key, RestoreFakeCell), "disabled path restores captured cell");
        False(cell.Viewable, "disabled path restores native hidden value");

        // Missing decision/scene/identity and a failed BeginPending (including
        // an adapter exception) all use the same idempotent fail-open restore.
        True(registry.Capture(key, cell, originalViewable: false, local, out viewable), "second local cell is captured");
        cell.Viewable = viewable;
        True(registry.TryRestore(key, RestoreFakeCell), "missing scene path restores cell");
        False(cell.Viewable, "missing scene keeps native hidden value");
        var failedPending = new LocalSessionRegistry();
        False(failedPending.BeginPending(901, new[] { 999L }, IntPtr.Zero, BypassReason.UnownedCharacter, out _), "failed BeginPending fails open");
        True(registry.Capture(key, cell, originalViewable: false, local, out viewable), "failed BeginPending path can recapture cell");
        cell.Viewable = viewable;
        var failedPendingRestores = registry.RestoreAll(RestoreFakeCell);
        Equal(1, failedPendingRestores.Count);
        False(cell.Viewable, "failed BeginPending restores native hidden value");
        False(registry.Capture(new CellIdentity(IntPtr.Zero, 902), cell, false, local, out viewable), "missing identity does not enable local visibility");
        False(viewable, "missing identity preserves native hidden value");
        False(registry.Capture(key, cell, false, normalHidden, out viewable), "missing decision fails open to normal");
        False(viewable, "missing decision preserves native hidden value");

        cell.Viewable = true;
        True(registry.Capture(key, cell, originalViewable: true, local, out viewable), "third local cell is captured");
        cell.Viewable = viewable;
        True(viewable, "third local cell is visible");
        var unloadRestores = registry.RestoreAll(RestoreFakeCell);
        Equal(1, unloadRestores.Count);
        True(cell.Viewable, "unload restores native visible value");
        False(registry.Contains(key), "unload clears visibility state");

        // A replacement entry restores stale wrappers while retaining only the
        // current Local cell until native navigation has consumed Viewable=true.
        var staleKey = new CellIdentity((IntPtr)93, 904);
        var currentKey = new CellIdentity((IntPtr)94, 905);
        var staleCell = new FakeCell { Viewable = false };
        var currentCell = new FakeCell { Viewable = false };
        True(registry.Capture(staleKey, staleCell, originalViewable: false, local, out var staleViewable), "stale replacement cell is captured");
        staleCell.Viewable = staleViewable;
        True(registry.Capture(currentKey, currentCell, originalViewable: false, local, out var currentViewable), "current replacement cell is captured");
        currentCell.Viewable = currentViewable;
        var staleRestores = registry.RestoreAllExcept(currentKey, RestoreFakeCell);
        Equal(1, staleRestores.Count);
        Equal(staleKey, staleRestores[0].Identity);
        True(registry.Contains(currentKey), "current replacement cell remains visible");
        False(staleCell.Viewable, "stale cell is restored before replacement");
        var currentRestore = registry.RestoreAll(RestoreFakeCell);
        Equal(1, currentRestore.Count);
        Equal(currentKey, currentRestore[0].Identity);
        False(currentCell.Viewable, "current cell is restored on unload");

        // An originally hidden Normal row is never made visible by the
        // visibility seam.
        False(registry.Capture(new CellIdentity((IntPtr)92, 903), cell, false, normalHidden, out viewable), "Normal row is not captured");
        False(viewable, "Normal hidden row remains hidden");
    }

    private static void CellVisibilityUniqueEpisodeLease()
    {
        // Evidence: Character ApplySyncPart d32 and Memory Apply d16 consume
        // their state-machine args after the factory. A regenerated IL2CPP
        // wrapper may differ, so EpisodeId is safe only when one active lease
        // owns that ID; ambiguity must retain native presentation.
        var registry = new CellVisibilityRegistry();
        var local = new EligibilityDecision(PlaybackMode.LocalBypass, BypassReason.UnownedCharacter);
        var first = new CellIdentity((IntPtr)91, 901);
        True(registry.Capture(first, new FakeCell(), false, local, out _), "first lease captured");
        True(registry.TryGetUniqueIdentity(901, out var unique), "single episode lease resolves");
        Equal(first, unique);

        True(registry.Capture(new CellIdentity((IntPtr)93, 902), new FakeCell(), false, local, out _), "unrelated lease captured");
        True(registry.TryGetUniqueIdentity(901, out unique), "unrelated episode does not create ambiguity");
        Equal(first, unique);

        var duplicate = new CellIdentity((IntPtr)92, 901);
        True(registry.Capture(duplicate, new FakeCell(), false, local, out _), "duplicate episode lease captured");
        False(registry.TryGetUniqueIdentity(901, out _), "duplicate episode lease fails open");
        True(registry.TryRestore(duplicate, RestoreFakeCell), "duplicate lease restored");
        True(registry.TryGetUniqueIdentity(901, out unique), "identity resolves after ambiguity is removed");
        Equal(first, unique);
    }

    private static void CellVisibilityStrongLeaseAndSetterFailure()
    {
        // Evidence: interop EpisodeComponent.cs lines 971-981 expose the native
        // Viewable backing field and d60:418-421/d61:268-271 consume it. The
        // lease must keep the wrapper alive until that bit is restored.
        var local = new EligibilityDecision(PlaybackMode.LocalBypass, BypassReason.UnownedMemory);
        var strongKey = new CellIdentity((IntPtr)95, 906);
        var strongRegistry = new CellVisibilityRegistry();
        var weak = CaptureAndReleaseWrapper(strongRegistry, strongKey, local);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        GC.KeepAlive(strongRegistry);
        True(weak.IsAlive, "strong visibility lease keeps wrapper alive through restore");
        True(strongRegistry.TryRestore(strongKey, RestoreFakeCell), "strong lease can restore after GC");
        False(strongRegistry.Contains(strongKey), "successful GC fixture restore clears lease");
        Equal(0, strongRegistry.Count);

        var registry = new CellVisibilityRegistry();
        var failureKey = new CellIdentity((IntPtr)96, 907);
        var failureCell = new FakeCell { Viewable = false };
        True(registry.Capture(failureKey, failureCell, false, local, out var viewable), "setter failure cell is captured");
        failureCell.Viewable = viewable;
        False(registry.TryRestore(failureKey, (_, _) => false), "failed setter reports failure");
        True(registry.Contains(failureKey), "failed setter retains lease");
        Equal(1, registry.Count);
        False(registry.TryRestore(failureKey, (_, _) => throw new InvalidOperationException("fixture setter failure")), "throwing setter reports failure");
        True(registry.Contains(failureKey), "throwing setter retains lease");
        True(registry.TryRestore(failureKey, RestoreFakeCell), "successful retry restores setter");
        False(failureCell.Viewable, "successful retry restores original bit");
        False(registry.Contains(failureKey), "successful retry clears lease");
        Equal(0, registry.Count);
    }

    private static void UnloadVisibilityCleanupRetry()
    {
        // Evidence: d60:418-421/d61:268-271 consume the native Viewable bit and
        // EpisodeComponent.cs:971-981 exposes its restore route. The plugin
        // unload seam must not unpatch while this lease is still active.
        var local = new EligibilityDecision(PlaybackMode.LocalBypass, BypassReason.UnownedMemory);
        var registry = new CellVisibilityRegistry();
        var key = new CellIdentity((IntPtr)97, 908);
        var cell = new FakeCell { Viewable = false };
        True(registry.Capture(key, cell, false, local, out var viewable), "unload fixture captures cell");
        cell.Viewable = viewable;
        var unpatchCalls = 0;
        False(
            UnloadLifecyclePolicy.TryUnload(
                () => throw new InvalidOperationException("fixture restore infrastructure failure"),
                () => unpatchCalls++),
            "unload contains a throwing restore callback");
        Equal(0, unpatchCalls);
        Equal(1, registry.Count);
        // Unpatch exception behavior is an infrastructure seam rather than a
        // game claim; the d60/d61 evidence above establishes only why the lease
        // must remain intact until the restore callback succeeds.
        False(
            UnloadLifecyclePolicy.TryUnload(
                () => registry.TryRestoreAll(RestoreFakeCell),
                () =>
                {
                    unpatchCalls++;
                    throw new InvalidOperationException("fixture unpatch infrastructure failure");
                }),
            "unpatch exception is contained after restore");
        Equal(1, unpatchCalls);
        Equal(0, registry.Count);
        True(
            UnloadLifecyclePolicy.TryUnload(
                () => registry.TryRestoreAll(RestoreFakeCell),
                () => unpatchCalls++),
            "unload retry unpatches after infrastructure recovers");
        Equal(2, unpatchCalls);
        Equal(0, registry.Count);
        False(cell.Viewable, "unload retry restores original bit");
    }

    private static WeakReference CaptureAndReleaseWrapper(
        CellVisibilityRegistry registry,
        CellIdentity identity,
        EligibilityDecision decision)
    {
        var leased = new FakeCell { Viewable = false };
        var weak = new WeakReference(leased);
        True(registry.Capture(identity, leased, false, decision, out var viewable), "GC fixture creates active lease");
        leased.Viewable = viewable;
        return weak;
    }

    private static bool RestoreFakeCell(object wrapper, bool originalViewable)
    {
        if (wrapper is not FakeCell cell)
        {
            return false;
        }

        cell.Viewable = originalViewable;
        return true;
    }

    private sealed class FakeCell
    {
        public bool Viewable { get; set; }
    }

    private static void BranchLocalPolicy()
    {
        // Evidence: selected d103:1383 awaits the API, then applies native
        // history/selection state at :1108-1270 without inspecting the boolean
        // payload. The safe adapter therefore makes the existing d103
        // sceneMasterId<=0 guard skip only the remote operation; it never hooks,
        // constructs, or assigns Result<BooleanResult>.
        True(SideEffectPolicy.Allows(PlaybackMode.Normal, PlaybackOperation.BranchSelectionRemote), "normal branch remains native");
        False(SideEffectPolicy.Allows(PlaybackMode.LocalBypass, PlaybackOperation.BranchSelectionRemote), "local branch denies remote API");
        True(SideEffectPolicy.Allows(PlaybackMode.LocalBypass, PlaybackOperation.LocalScenarioState), "local branch keeps native state continuation");
    }

    private static void SessionIdentityAndGenerationMatching()
    {
        // Evidence: selected ScenarioController d103 line 1383 and EpisodeService
        // PostRead d16 lines 2335/2403 require exact API/service object identity
        // in addition to complete Episode/Scene/controller/generation provenance.
        // Tracking is inside PostRead d16 and has no separate business boundary.
        var registry = new LocalSessionRegistry();
        True(registry.BeginPending(402, new[] { 503L }, (IntPtr)12, BypassReason.UnownedMemory, out var pending, (IntPtr)31, (IntPtr)41), "first pending created");
        False(registry.TryBind(402, 503, (IntPtr)99, (IntPtr)22, out _), "unexpected episode controller does not bind");
        True(registry.TryBind(402, 503, (IntPtr)12, (IntPtr)22, out var bound), "first bound created");
        var exact = new PlaybackIdentity(402, 503, (IntPtr)12, (IntPtr)22, bound.Generation);
        True(registry.TryMatch(exact), "exact identity matches");
        True(registry.TryMatchBranch(exact, (IntPtr)41), "exact branch API identity matches");
        False(registry.TryMatchBranch(exact, (IntPtr)42), "different branch API identity fails");
        True(registry.TryMatchEpisodeService(exact, (IntPtr)31), "exact service identity matches");
        False(registry.TryMatchEpisodeService(exact, (IntPtr)32), "different service identity fails");
        False(registry.TryMatch(exact with { ScenarioController = (IntPtr)23 }), "different controller fails");
        False(registry.TryMatch(exact with { Generation = pending.Generation + 1 }), "stale generation fails");
        False(registry.TryMatch(exact with { SceneId = 504 }), "different scene fails");

        // Same episode/scene with a replacement generation must not allow a
        // stale continuation carrying the first full identity to suppress.
        True(registry.BeginPending(402, new[] { 503L }, (IntPtr)12, BypassReason.UnownedCharacter, out var replacement, (IntPtr)31, (IntPtr)41), "same episode replacement pending created");
        True(registry.TryBind(402, 503, (IntPtr)12, (IntPtr)24, out var rebound), "same episode replacement bound");
        False(registry.TryMatch(exact), "stale same episode/scene generation fails");
        False(registry.TryMatchEpisodeService(exact, (IntPtr)31), "stale service continuation fails full provenance");
        False(registry.TryMatchBranch(exact, (IntPtr)41), "stale branch continuation fails full provenance");
        True(registry.TryMatch(new PlaybackIdentity(402, 503, (IntPtr)12, (IntPtr)24, rebound.Generation)), "new generation full identity matches");
        True(replacement.Generation != bound.Generation, "replacement advances generation");
    }

    private static void SessionCleanup()
    {
        // Evidence: full ISIL callers invoke d60/d61.MoveNext directly at
        // evidence/decomp/full-isil/IsilDump/GameUi/Assets/GameUi/Episode/
        // EpisodeComponent_NestedType__SelectCharacterCell_d__39.txt:505-510
        // and evidence/decomp/full-isil/IsilDump/GameUi/Assets/GameUi/Episode/
        // EpisodeComponent_NestedType___ProcessOnCreate_b__32_20_d.txt:351-356;
        // ScenarioController Leave d__85 defines cleanup.
        var registry = new LocalSessionRegistry();
        True(registry.BeginPending(403, new[] { 505L }, (IntPtr)13, BypassReason.UnownedCharacter, out _, (IntPtr)33, (IntPtr)43), "pending created");
        registry.ClearForEpisode(403);
        Equal(SessionState.Empty, registry.State);

        True(registry.BeginPending(404, new[] { 506L }, (IntPtr)14, BypassReason.UnownedMemory, out _, (IntPtr)34, (IntPtr)44), "replacement pending created");
        True(registry.TryBind(404, 506, (IntPtr)14, (IntPtr)24, out _), "replacement bound");
        registry.ClearForScenarioController((IntPtr)24);
        Equal(SessionState.Empty, registry.State);

        True(registry.BeginPending(405, new[] { 507L }, (IntPtr)15, BypassReason.UnownedMemory, out _, (IntPtr)35, (IntPtr)45), "unload pending created");
        registry.ClearAll();
        Equal(SessionState.Empty, registry.State);
    }

    private static void IncompleteIdentityFailsOpen()
    {
        var registry = new LocalSessionRegistry();
        False(registry.BeginPending(0, new[] { 508L }, (IntPtr)16, BypassReason.UnownedCharacter, out _), "zero episode cannot create pending");
        False(registry.BeginPending(406, Array.Empty<long>(), (IntPtr)16, BypassReason.UnownedCharacter, out _), "empty scene set cannot create pending");
        False(registry.BeginPending(406, new[] { 508L }, IntPtr.Zero, BypassReason.UnownedCharacter, out _), "zero controller cannot create pending");
        False(registry.BeginPending(406, new[] { 508L }, (IntPtr)16, BypassReason.UnownedCharacter, out _, IntPtr.Zero, (IntPtr)46), "zero service cannot create pending");
        False(registry.BeginPending(406, new[] { 508L }, (IntPtr)16, BypassReason.UnownedCharacter, out _, (IntPtr)36, IntPtr.Zero), "zero API client cannot create pending");
        False(registry.TryMatch(new PlaybackIdentity(406, 508, (IntPtr)16, (IntPtr)26, 1)), "unbound identity fails open");
        False(registry.TryMatchBranch(new PlaybackIdentity(406, 508, (IntPtr)16, (IntPtr)26, 1), IntPtr.Zero), "missing branch API identity fails open");
        False(registry.TryMatchEpisodeService(new PlaybackIdentity(406, 508, (IntPtr)16, (IntPtr)26, 1), IntPtr.Zero), "missing service identity fails open");
        True(SideEffectPolicy.Allows(PlaybackMode.Normal, PlaybackOperation.EpisodeReadApi), "fail-open route keeps native API");
    }

    private static void ScenarioProvenanceStartFrames()
    {
        // Evidence: selected ScenarioController.txt lines 725 and 847 call the
        // generated MoveNext synchronously after Start. d103:86-98 and
        // d104:25-38 read the wrapper-initialized -1 state; d103:1464-1466 and
        // d104:511-512 also write 0 on await suspension, so only the wrapper
        // frame may grant
        // a new identity.
        var registry = new ScenarioExecutionProvenanceRegistry();
        var identity = new PlaybackIdentity(501, 601, (IntPtr)71, (IntPtr)72, 1);
        var frame = registry.CreateStartFrame(ScenarioExecutionKind.PostRead, (IntPtr)72, identity);
        True(frame is not null, "complete identity creates start frame");
        Equal(1, registry.StartFrameCount);
        True(registry.BeginInitial((IntPtr)701, (IntPtr)72, ScenarioExecutionKind.PostRead, ScenarioExecutionProvenanceRegistry.InitialState, frame, out var captured), "initial state consumes wrapper frame");
        Equal(identity, captured);
        True(frame!.IsConsumed, "start frame is one-shot");
        registry.ReleaseStartFrame(frame);
        True(frame.IsCleared, "successful wrapper finalizer clears start frame");
        Equal(0, registry.StartFrameCount);
        True(registry.TryGet((IntPtr)701, ScenarioExecutionKind.PostRead, out var resumed), "suspended continuation keeps active identity");
        Equal(identity, resumed);
        False(registry.BeginInitial((IntPtr)701, (IntPtr)72, ScenarioExecutionKind.PostRead, ScenarioExecutionProvenanceRegistry.InitialState, frame, out _), "consumed frame cannot be reused");
        False(registry.End((IntPtr)701, ScenarioExecutionKind.PostRead, terminalOrFaulted: false), "suspension retains active identity");
        Equal(1, registry.ActiveCount);

        registry.ClearAll();
        Equal(0, registry.ActiveCount);
        Equal(0, registry.StartFrameCount);
        False(registry.TryGet((IntPtr)701, ScenarioExecutionKind.PostRead, out _), "ClearAll drops abandoned suspended machine");

        // An old same-ID continuation has no wrapper frame after ClearAll and
        // cannot acquire a new generation. A fresh initial machine can.
        var replacement = identity with { Generation = 2 };
        var replacementFrame = registry.CreateStartFrame(ScenarioExecutionKind.PostRead, (IntPtr)72, replacement);
        False(registry.BeginInitial((IntPtr)701, (IntPtr)72, ScenarioExecutionKind.PostRead, ScenarioExecutionProvenanceRegistry.InitialState, null, out _), "old continuation cannot borrow replacement");
        True(registry.BeginInitial((IntPtr)702, (IntPtr)72, ScenarioExecutionKind.PostRead, ScenarioExecutionProvenanceRegistry.InitialState, replacementFrame, out var replacementCaptured), "new initial machine consumes new frame");
        Equal(replacement, replacementCaptured);

        var clearedOnException = registry.CreateStartFrame(ScenarioExecutionKind.BranchSelection, (IntPtr)72, replacement);
        True(clearedOnException is not null, "exception fixture creates frame");
        registry.ReleaseStartFrame(clearedOnException);
        False(registry.BeginInitial((IntPtr)703, (IntPtr)72, ScenarioExecutionKind.BranchSelection, ScenarioExecutionProvenanceRegistry.InitialState, clearedOnException, out _), "exception-cleared frame fails open");
        False(registry.BeginInitial((IntPtr)704, (IntPtr)72, ScenarioExecutionKind.BranchSelection, 0, registry.CreateStartFrame(ScenarioExecutionKind.BranchSelection, (IntPtr)72, replacement), out _), "await-resume state cannot consume a new initial frame");
        False(registry.BeginInitial((IntPtr)705, (IntPtr)99, ScenarioExecutionKind.PostRead, ScenarioExecutionProvenanceRegistry.InitialState, replacementFrame, out _), "mismatched scenario fails open");
        False(registry.CreateStartFrame(ScenarioExecutionKind.PostRead, (IntPtr)72, identity with { ScenarioController = IntPtr.Zero }) is not null, "incomplete identity has no frame");
        registry.ClearAll();
        Equal(0, registry.StartFrameCount);
        Equal(0, registry.ActiveCount);
    }

    private static void RemoteSceneSourceHitAndCache()
    {
        // Evidence: CONTEXT.md:47-50 and spec.md:95-104 establish the static
        // manifest/scene protocol and the five-field G4 mapping. The fake
        // handler exposes only the canonical paths; the manifest sceneUrl is
        // intentionally unrelated and must never be followed.
        const long sceneId = 40003301;
        Equal(
            "https://raw.githubusercontent.com/ImoutoHeaven/MuvluvSceneFrame/main/",
            SceneFrameSource.DefaultRemoteBaseUrl);
        True(
            SceneFrameSource.TryNormalizeRemoteBaseUrl(SceneFrameSource.DefaultRemoteBaseUrl, out _),
            "default GitHub raw base is valid");
        var body = Encoding.UTF8.GetBytes(SceneJson(sceneId));
        var handler = new FakeHttpMessageHandler();
        handler.Add("/corpus/manifest.json", ManifestJson(sceneId, body));
        handler.Add($"/corpus/scene/{sceneId}/scene.json", body);
        var root = NewTempDirectory();
        try
        {
            var source = new SceneFrameSource(
                "https://source.invalid/corpus",
                root,
                handler);
            True(source.RemoteConfigured, "absolute HTTPS base enables remote source");
            True(source.TryPrepare(sceneId, out var first), "remote scene prepares");
            Equal(SceneFrameSourceKind.Remote, first.Source);
            Equal(SceneFrameSourceStatus.RemoteValid, first.Status);
            Equal(SceneFrameSourceStatus.RemoteValid, first.RemoteStatus);
            Equal(1, first.Frames.Length);

            var requestCount = handler.Paths.Count;
            True(source.TryPrepare(sceneId, out var second), "cached remote scene prepares");
            Equal(requestCount, handler.Paths.Count);
            Equal(first.Frames[0], second.Frames[0]);
            True(handler.Paths.Contains("/corpus/manifest.json"), "manifest uses canonical path");
            True(handler.Paths.Contains($"/corpus/scene/{sceneId}/scene.json"), "scene uses canonical path");
            False(handler.Paths.Any(path => path.Contains("evil", StringComparison.Ordinal)), "sceneUrl is not followed");

            False(SceneFrameSource.TryNormalizeRemoteBaseUrl("http://source.invalid/corpus", out _), "HTTP base is disabled");
            False(SceneFrameSource.TryNormalizeRemoteBaseUrl("https://source.invalid/corpus?token=secret", out _), "query-bearing base is disabled");
        }
        finally
        {
            DeleteTempDirectory(root);
        }
    }

    private static void RemoteSceneSourceFallbackStatuses()
    {
        // Evidence: spec.md:97-103 requires remote validation followed by the
        // plugin-local fallback, with no directory enumeration or disk HTTP
        // cache. Each rejected remote body is replaced by one valid local body.
        const long absentId = 40003302;
        var absentBody = Encoding.UTF8.GetBytes(SceneJson(absentId));
        var absentRoot = NewTempDirectory();
        try
        {
            WriteLocalScene(absentRoot, absentId, absentBody);
            var absentHandler = new FakeHttpMessageHandler();
            absentHandler.Add("/corpus/manifest.json", ManifestJson(40003301, Encoding.UTF8.GetBytes(SceneJson(40003301))));
            var absentSource = new SceneFrameSource("https://source.invalid/corpus", absentRoot, absentHandler);
            var absent = absentSource.Resolve(absentId);
            Equal(SceneFrameSourceKind.Local, absent.Source);
            Equal(SceneFrameSourceStatus.LocalValid, absent.Status);
            Equal(SceneFrameSourceStatus.IdAbsent, absent.RemoteStatus);
            False(absentHandler.Paths.Any(path => path.Contains(absentId.ToString(), StringComparison.Ordinal)), "unlisted scene is not requested");
        }
        finally
        {
            DeleteTempDirectory(absentRoot);
        }

        var incompatibleRoot = NewTempDirectory();
        try
        {
            WriteLocalScene(incompatibleRoot, absentId, absentBody);
            var incompatibleHandler = new FakeHttpMessageHandler();
            incompatibleHandler.Add("/corpus/manifest.json", Encoding.UTF8.GetBytes("{}"));
            var incompatibleSource = new SceneFrameSource("https://source.invalid/corpus", incompatibleRoot, incompatibleHandler);
            var incompatible = incompatibleSource.Resolve(absentId);
            Equal(SceneFrameSourceKind.Local, incompatible.Source);
            Equal(SceneFrameSourceStatus.ManifestIncompatible, incompatible.RemoteStatus);
            False(incompatibleHandler.Paths.Any(path => path.Contains(absentId.ToString(), StringComparison.Ordinal)), "incompatible manifest does not request a scene");
        }
        finally
        {
            DeleteTempDirectory(incompatibleRoot);
        }

        var unavailableRoot = NewTempDirectory();
        try
        {
            WriteLocalScene(unavailableRoot, absentId, absentBody);
            var unavailableHandler = new FakeHttpMessageHandler();
            var unavailableSource = new SceneFrameSource("https://source.invalid/corpus", unavailableRoot, unavailableHandler);
            var unavailable = unavailableSource.Resolve(absentId);
            Equal(SceneFrameSourceKind.Local, unavailable.Source);
            Equal(SceneFrameSourceStatus.ManifestUnavailable, unavailable.RemoteStatus);
        }
        finally
        {
            DeleteTempDirectory(unavailableRoot);
        }

        var remoteBodies = new[]
        {
            Encoding.UTF8.GetBytes(SceneJson(40003303)),
            Encoding.UTF8.GetBytes(SceneJson(40003304)),
            Encoding.UTF8.GetBytes("{}"),
        };
        var expectedBodies = new[]
        {
            remoteBodies[0],
            remoteBodies[1],
            remoteBodies[2],
        };
        var ids = new[] { 40003303L, 40003304L, 40003305L };
        var roots = new[] { NewTempDirectory(), NewTempDirectory(), NewTempDirectory() };
        try
        {
            for (var index = 0; index < roots.Length; index++)
            {
                var localBody = Encoding.UTF8.GetBytes(SceneJson(ids[index]));
                WriteLocalScene(roots[index], ids[index], localBody);
                var handler = new FakeHttpMessageHandler();
                var expected = expectedBodies[index];
                var expectedHash = Sha256(expected);
                var manifestHash = index == 0 ? new string('0', 64) : expectedHash;
                var manifestBytes = index == 1 ? expected.Length + 1 : expected.Length;
                handler.Add("/corpus/manifest.json", ManifestJson(ids[index], manifestBytes, manifestHash));
                handler.Add($"/corpus/scene/{ids[index]}/scene.json", expected);
                var source = new SceneFrameSource("https://source.invalid/corpus", roots[index], handler);
                var resolution = source.Resolve(ids[index]);
                Equal(SceneFrameSourceKind.Local, resolution.Source);
                Equal(SceneFrameSourceStatus.LocalValid, resolution.Status);
                Equal(SceneFrameSourceStatus.RemoteFetchInvalid, resolution.RemoteStatus);
                Equal(1, resolution.Frames.Length);
            }
        }
        finally
        {
            for (var index = 0; index < roots.Length; index++)
            {
                DeleteTempDirectory(roots[index]);
            }
        }

        var disabledRoot = NewTempDirectory();
        try
        {
            WriteLocalScene(disabledRoot, absentId, absentBody);
            var disabledHandler = new FakeHttpMessageHandler();
            var disabled = new SceneFrameSource("http://source.invalid/corpus", disabledRoot, disabledHandler).Resolve(absentId);
            Equal(SceneFrameSourceKind.Local, disabled.Source);
            Equal(SceneFrameSourceStatus.RemoteUnconfigured, disabled.RemoteStatus);
            Equal(0, disabledHandler.Paths.Count);
        }
        finally
        {
            DeleteTempDirectory(disabledRoot);
        }

        var missingRoot = NewTempDirectory();
        try
        {
            var missingHandler = new FakeHttpMessageHandler();
            var missingId = 40003306L;
            var missingBody = Encoding.UTF8.GetBytes(SceneJson(missingId));
            missingHandler.Add("/corpus/manifest.json", ManifestJson(missingId, missingBody));
            missingHandler.Add("/corpus/scene/40003306/scene.json", Array.Empty<byte>(), HttpStatusCode.NotFound);
            var missing = new SceneFrameSource("https://source.invalid/corpus", missingRoot, missingHandler).Resolve(missingId);
            Equal(SceneFrameSourceKind.None, missing.Source);
            Equal(SceneFrameSourceStatus.LocalMissing, missing.Status);
            Equal(SceneFrameSourceStatus.RemoteFetchUnavailable, missing.RemoteStatus);
            Equal(SceneFrameSourceStatus.LocalMissing, missing.LocalStatus);
        }
        finally
        {
            DeleteTempDirectory(missingRoot);
        }
    }

    private static void RemoteSceneSourcePrepareGate()
    {
        // Evidence: spec.md:101-112 requires Character/Memory/Main to prepare
        // every runtime-Master-derived Scene before BeginPending, while Event
        // may keep its native content-acquisition fallback after a double miss.
        var firstId = 40003307L;
        var secondId = 40003308L;
        var firstBody = Encoding.UTF8.GetBytes(SceneJson(firstId));
        var secondBody = Encoding.UTF8.GetBytes(SceneJson(secondId));
        var handler = new FakeHttpMessageHandler();
        handler.Add("/corpus/manifest.json", ManifestJson(firstId, firstBody, secondId, secondBody));
        handler.Add($"/corpus/scene/{firstId}/scene.json", firstBody);
        handler.Add($"/corpus/scene/{secondId}/scene.json", secondBody);
        var root = NewTempDirectory();
        try
        {
            var source = new SceneFrameSource("https://source.invalid/corpus", root, handler);

            True(source.TryPrepareAll(new[] { firstId, secondId }, out var prepared), "all expected scenes prepare");
            Equal(2, prepared.Length);
            Equal(SceneFrameSourceKind.Remote, prepared[0].Source);
            Equal(SceneFrameSourceKind.Remote, prepared[1].Source);

            False(source.TryPrepareAll(new[] { firstId, 40003309L }, out var partial), "one missing scene rejects the complete gate");
            Equal(2, partial.Length);
            True(partial[0].IsAvailable, "available scene remains prepared");
            False(partial[1].IsAvailable, "missing scene is reported unavailable");
            Equal(SceneFrameSourceStatus.IdAbsent, partial[1].RemoteStatus);
        }
        finally
        {
            DeleteTempDirectory(root);
        }
    }

    private static string SceneJson(long sceneId)
    {
        return "{\"assets\":[],\"commands\":[{\"type\":\"muvluvFrame\",\"frame\":{"
            + "\"background\":{},\"branchId\":null,\"characters\":[],"
            + "\"configuration\":{\"Phrase\":{\"Text\":\"fixture\"}},"
            + "\"needsHideText\":false,\"order\":1,\"sceneId\":"
            + sceneId.ToString()
            + ",\"selectedBranchId\":null}}],\"id\":\""
            + sceneId.ToString()
            + "\",\"notes\":[],\"preloaded\":false,\"title\":\"fixture\"}";
    }

    private static byte[] ManifestJson(params object[] values)
    {
        var builder = new StringBuilder("{\"format\":\"muvluv-g4-scene-export-v1\",\"scenes\":[");
        for (var index = 0; index < values.Length; index += 2)
        {
            var sceneId = Convert.ToInt64(values[index], System.Globalization.CultureInfo.InvariantCulture);
            var body = (byte[])values[index + 1];
            if (index > 0)
            {
                builder.Append(',');
            }

            builder.Append("{\"id\":\"")
                .Append(sceneId)
                .Append("\",\"sceneUrl\":\"https://evil.invalid/not-used.json\",\"bytes\":")
                .Append(body.Length)
                .Append(",\"sha256\":\"")
                .Append(Sha256(body))
                .Append("\"}");
        }

        builder.Append("]}");
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    private static byte[] ManifestJson(long sceneId, int bytes, string sha256)
    {
        var builder = new StringBuilder("{\"format\":\"muvluv-g4-scene-export-v1\",\"scenes\":[{\"id\":\"");
        builder.Append(sceneId)
            .Append("\",\"sceneUrl\":\"https://evil.invalid/not-used.json\",\"bytes\":")
            .Append(bytes)
            .Append(",\"sha256\":\"")
            .Append(sha256)
            .Append("\"}]}");
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    private static string Sha256(byte[] bytes)
    {
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static void WriteLocalScene(string root, long sceneId, byte[] body)
    {
        var path = Path.Combine(root, "scene", sceneId.ToString(), "scene.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, body);
    }

    private static string NewTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "muvluv-scenes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTempDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, byte[] Body)> _responses = new();

        public List<string> Paths { get; } = new();

        public void Add(string path, byte[] body, HttpStatusCode status = HttpStatusCode.OK)
        {
            _responses[path] = (status, body);
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            Paths.Add(path);
            if (!_responses.TryGetValue(path, out var response))
            {
                response = (HttpStatusCode.NotFound, Array.Empty<byte>());
            }

            return Task.FromResult(new HttpResponseMessage(response.Status)
            {
                Content = new ByteArrayContent(response.Body),
                RequestMessage = request,
            });
        }
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"expected {expected}, actual {actual}");
        }
    }

    private static void True(bool value, string message)
    {
        if (!value)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void False(bool value, string message)
    {
        True(!value, message);
    }
}
