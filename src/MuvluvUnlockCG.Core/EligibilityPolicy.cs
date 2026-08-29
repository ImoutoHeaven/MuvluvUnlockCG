using System;

namespace MuvluvUnlockCG.Core;

public enum PlaybackMode
{
    Normal,
    LocalBypass,
}

public enum BypassReason
{
    None,
    UnownedCharacter,
    AffectionBelowRequirement,
    UnownedMemory,
    LocallyAvailableStory,
    IncompleteEvidence,
}

public readonly record struct EligibilityDecision(PlaybackMode Mode, BypassReason Reason)
{
    public bool IsLocalBypass => Mode == PlaybackMode.LocalBypass;
}

/// <summary>
/// Immutable facts copied from the original CharacterEpisodeCellArgs seam.
/// The nullable values make an adapter's incomplete read explicit; incomplete
/// evidence fails open to Normal instead of suppressing native behavior.
/// </summary>
public readonly record struct CharacterEligibilityFacts(
    long EpisodeId,
    bool OriginalHasCharacter,
    int? AffectionLevel,
    int? UnlockConditionAffectionLevel)
{
    public bool IsComplete =>
        EpisodeId > 0 && AffectionLevel.HasValue && UnlockConditionAffectionLevel.HasValue;
}

/// <summary>
/// Immutable facts copied from the original MemoryEpisodeCellArgs seam and
/// the runtime MemoryMaster publication relation.
/// </summary>
public readonly record struct MemoryEligibilityFacts(
    long EpisodeId,
    bool OriginalViewable,
    bool MemoryIsReleased,
    bool HasMemoryIdentity)
{
    public bool IsComplete => EpisodeId > 0 && HasMemoryIdentity;
}

public readonly record struct CharacterCellFacts(
    long EpisodeId,
    bool OriginalViewable,
    bool OriginalHasCharacter,
    int? AffectionLevel,
    int? UnlockConditionAffectionLevel);

public readonly record struct CharacterCellDecision(
    long EpisodeId,
    bool OriginalViewable,
    bool Viewable,
    EligibilityDecision Eligibility);

public readonly record struct MemoryCellFacts(
    long EpisodeId,
    bool OriginalViewable,
    bool MemoryIsReleased,
    bool HasMemoryIdentity);

public readonly record struct MemoryCellDecision(
    long EpisodeId,
    bool OriginalViewable,
    bool IncludeInCatalog,
    bool Viewable,
    EligibilityDecision Eligibility);

/// <summary>
/// Facts copied from the generated Main/Event episode-cell seams. Story rows
/// never use account ownership or affection; the only local promotion proof is
/// a valid Master row whose SceneFrame content is available under the configured
/// remote/local source chain. The Released flag is retained for callers that can prove
/// publication from a runtime Master relation without inventing an ID list.
/// </summary>
public readonly record struct StoryCellFacts(
    long EpisodeId,
    bool OriginalViewable,
    bool Released,
    bool LocallyAvailable)
{
    public bool IsComplete => EpisodeId > 0;
}

public readonly record struct StoryCellDecision(
    long EpisodeId,
    bool OriginalViewable,
    bool Viewable,
    EligibilityDecision Eligibility);

public static class EligibilityPolicy
{
    /// <summary>
    /// Evidence: selected/GameUi__Assets__GameUi__Episode__EpisodeController.txt,
    /// GenerateCharacterCellArgs and &lt;GenerateCharacterCellArgs&gt;b__54_0. The
    /// native seam's HasCharacter and affection fields are the only Character
    /// route facts; HasMemory is a RewardPackage ThingTypes.Memory marker.
    /// </summary>
    public static EligibilityDecision ForCharacter(CharacterEligibilityFacts facts)
    {
        if (!facts.IsComplete)
        {
            return new EligibilityDecision(PlaybackMode.Normal, BypassReason.IncompleteEvidence);
        }

        if (!facts.OriginalHasCharacter)
        {
            return new EligibilityDecision(PlaybackMode.LocalBypass, BypassReason.UnownedCharacter);
        }

        return facts.AffectionLevel!.Value >= facts.UnlockConditionAffectionLevel!.Value
            ? new EligibilityDecision(PlaybackMode.Normal, BypassReason.None)
            : new EligibilityDecision(PlaybackMode.LocalBypass, BypassReason.AffectionBelowRequirement);
    }

    /// <summary>
    /// Evidence: selected/GameUi__Assets__GameUi__Episode__EpisodeController.txt,
    /// GenerateMemoryCellArgs and &lt;GenerateMemoryCellArgs&gt;b__56_0/b__56_1.
    /// Release is retained as a native catalog boundary; only a released row
    /// whose original generated cell was not viewable is locally playable.
    /// </summary>
    public static EligibilityDecision ForMemory(MemoryEligibilityFacts facts)
    {
        if (!facts.IsComplete)
        {
            return new EligibilityDecision(PlaybackMode.Normal, BypassReason.IncompleteEvidence);
        }

        if (!facts.MemoryIsReleased || facts.OriginalViewable)
        {
            return new EligibilityDecision(PlaybackMode.Normal, BypassReason.None);
        }

        return new EligibilityDecision(PlaybackMode.LocalBypass, BypassReason.UnownedMemory);
    }

    public static CharacterCellDecision ForCharacterCell(CharacterCellFacts facts)
    {
        var eligibility = ForCharacter(new CharacterEligibilityFacts(
            facts.EpisodeId,
            facts.OriginalHasCharacter,
            facts.AffectionLevel,
            facts.UnlockConditionAffectionLevel));
        var viewable = eligibility.IsLocalBypass || facts.OriginalViewable;
        return new CharacterCellDecision(facts.EpisodeId, facts.OriginalViewable, viewable, eligibility);
    }

    /// <summary>
    /// The real d60/d61 entry state machines read their own args.Viewable before
    /// native navigation (persisted ISIL:
    /// evidence/decomp/full-isil/IsilDump/GameUi/Assets/GameUi/Episode/
    /// EpisodeController_NestedType__MoveToAdventure_d__60.txt:399-421 and
    /// ...d__61.txt:249-271). Only an exact captured LocalBypass decision may
    /// promote that distinct entry object; Normal or missing evidence keeps its
    /// original value.
    /// </summary>
    public static bool ResolveEntryViewable(
        bool captured,
        EligibilityDecision decision,
        bool originalViewable)
    {
        return captured && decision.IsLocalBypass
            ? true
            : originalViewable;
    }

    public static MemoryCellDecision ForMemoryCell(MemoryCellFacts facts)
    {
        var eligibility = ForMemory(new MemoryEligibilityFacts(
            facts.EpisodeId,
            facts.OriginalViewable,
            facts.MemoryIsReleased,
            facts.HasMemoryIdentity));
        var include = facts.EpisodeId > 0 && facts.HasMemoryIdentity && facts.MemoryIsReleased;
        var viewable = eligibility.IsLocalBypass || facts.OriginalViewable;
        if (!include)
        {
            viewable = facts.OriginalViewable;
        }

        return new MemoryCellDecision(facts.EpisodeId, facts.OriginalViewable, include, viewable, eligibility);
    }

    /// <summary>
    /// Evidence: MainEpisodeController.txt:846-940 obtains the chapter rows
    /// from the runtime Master table and EventChapterController.txt:1343-1445
    /// maps its runtime event rows into cell args. A hidden story row is local
    /// only when publication or local SceneFrame availability is proven; all
    /// incomplete and native-visible rows stay on the native policy.
    /// </summary>
    public static StoryCellDecision ForStoryCell(StoryCellFacts facts)
    {
        if (!facts.IsComplete
            || facts.OriginalViewable
            || (!facts.Released && !facts.LocallyAvailable))
        {
            return new StoryCellDecision(
                facts.EpisodeId,
                facts.OriginalViewable,
                facts.OriginalViewable,
                new EligibilityDecision(PlaybackMode.Normal, facts.IsComplete ? BypassReason.None : BypassReason.IncompleteEvidence));
        }

        return new StoryCellDecision(
            facts.EpisodeId,
            facts.OriginalViewable,
            true,
            new EligibilityDecision(PlaybackMode.LocalBypass, BypassReason.LocallyAvailableStory));
    }
}

public enum PlaybackOperation
{
    SceneFrameContent,
    AssetContent,
    LocalScenarioState,
    EpisodeReadApi,
    MemoryDbMutation,
    RewardProgress,
    BranchSelectionRemote,
    EpisodeTracking,
}

public static class SideEffectPolicy
{
    public static bool Allows(PlaybackMode mode, PlaybackOperation operation)
    {
        if (mode == PlaybackMode.Normal)
        {
            return true;
        }

        return operation == PlaybackOperation.SceneFrameContent
            || operation == PlaybackOperation.AssetContent
            || operation == PlaybackOperation.LocalScenarioState;
    }
}
