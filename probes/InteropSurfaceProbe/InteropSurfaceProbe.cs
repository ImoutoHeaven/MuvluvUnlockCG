using Assets.Api.Client;
using Assets.Api.MemoryDB;
using Assets.GameUi.Episode;
using Assets.GameUi.Scenario.Configuration;
using Assets.GameUi.Service;

namespace MuvluvUnlockCG.Probes;

/// <summary>
/// Compile-time proof that the installed game's generated IL2CPP interop surface
/// exposes the local model and patch seams required by the proposed plugin.
/// This assembly is never installed into the game.
/// </summary>
public static class InteropSurfaceProbe
{
    public enum PlaybackRoute
    {
        Normal,
        LocalBypass,
    }

    /// <summary>
    /// The proposed patch boundary.  Normal ownership/affection flow must not
    /// be intercepted; only a request that the stock client would reject is a
    /// candidate for a local, no-progress playback session.
    /// </summary>
    public static PlaybackRoute SelectPlaybackRoute(
        bool hasCharacter,
        int currentAffectionLevel,
        int requiredAffectionLevel)
        => hasCharacter && currentAffectionLevel >= requiredAffectionLevel
            ? PlaybackRoute.Normal
            : PlaybackRoute.LocalBypass;

    public static PlaybackRoute SelectPlaybackRoute(
        CharacterEpisodeCell.CharacterEpisodeCellArgs args)
        => SelectPlaybackRoute(
            args.HasCharacter,
            args.AffectionLevel,
            args.UnlockConditionAffectionLevel);

    public static bool CheckCharacterEpisodeGate(
        LocationService service,
        long episodeMasterId,
        long characterMasterId,
        out int requiredAffectionLevel)
        => service.CanAccessCharacterEpisode(
            episodeMasterId,
            characterMasterId,
            out requiredAffectionLevel);

    public static bool CheckControllerGate(
        EpisodeController controller,
        long episodeMasterId,
        out Il2CppSystem.Nullable<long> characterId)
        => controller.CanAccessCharacterEpisode(episodeMasterId, out characterId);

    public static void MarkViewable(EpisodeComponent.BaseEpisodeCellArgs args)
    {
        args.Viewable = true;
        args.LockMessage = string.Empty;
        args.DisablePopup = false;
    }

    public static void TouchGeneratedRows(
        EpisodeController controller,
        Il2CppSystem.Collections.Generic.IEnumerable<long> characterEpisodeIds,
        Il2CppSystem.Collections.Generic.List<long> memoryEpisodeIds)
    {
        _ = controller.GenerateCharacterCellArgs(characterEpisodeIds);
        _ = controller.GenerateMemoryCellArgs(memoryEpisodeIds);
    }

    public static void TouchPlaybackBoundaries(
        EpisodeService service,
        long sceneMasterId,
        long episodeMasterId)
    {
        _ = service.DownloadSceneFrameMasters(sceneMasterId);
        _ = service.PostRead(
            episodeMasterId,
            isNewStory: false,
            updateSequenceWhenPostRead: false);
        _ = service.MoveToScenario(
            episodeMasterId,
            updateSequenceWhenPostRead: false);
    }

    public static string TouchMasterAssetIds(
        EpisodeMaster episode,
        HomeIllustrationMaster homeIllustration,
        HomeScenarioMaster homeScenario)
        => $"{episode.AssetId}|{homeIllustration.AssetId}|{homeScenario.AssetId}";

    /// <summary>
    /// Compile-time evidence for data-driven discovery.  The plugin can read
    /// the installed build's runtime Master tables and account Character rows;
    /// it does not need a hard-coded episode or character ID list.
    /// </summary>
    public static void TouchDynamicCatalogs(IMemoryDB memoryDB)
    {
        _ = memoryDB.Characters;
        _ = memoryDB.CharactersMaster;
        _ = memoryDB.EpisodesMaster;
        _ = memoryDB.CharacterEpisodeUnlockConditionsMaster;
        _ = memoryDB.ScenesMaster;
    }

    public static string TouchGateRows(
        EpisodeMaster episode,
        Character character,
        CharacterEpisodeUnlockConditionMaster unlockCondition,
        SceneMaster scene)
    {
        _ = episode.CharacterMasterId;
        _ = episode.AffectionLevel;
        _ = episode.MemoryMasterId;
        _ = character.CharacterMasterId;
        _ = character.AffectionLevel;
        _ = unlockCondition.CharacterMasterId;
        _ = unlockCondition.EpisodeMasterId;
        _ = unlockCondition.AffectionLevel;
        _ = scene.EpisodeMasterId;
        _ = scene.IsAdult;
        return $"{episode.AssetId}|{scene.AssetId}";
    }

    public static ScenarioAdultStillAnimationTypes TouchAdultScenarioConfiguration(
        ScenarioBackgroundConfiguration configuration)
    {
        _ = configuration.AssetId;
        _ = configuration.BgmAssetId;
        _ = configuration.VoiceAssetId;
        return configuration.AdultStillAnimationType;
    }
}
