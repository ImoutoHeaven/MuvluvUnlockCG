using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Assets.Api.Client;
using Assets.GameUi.Episode;
using Assets.GameUi.Episode.EventChapter;
using Assets.GameUi.Episode.EventChapter.EpisodeCell;
using Assets.GameUi.Episode.MainChapter;
using Assets.GameUi.Episode.MainChapter.EpisodeCell;
using Assets.GameUi.Scenario;
using Assets.GameUi.Service;
using BepInEx.Logging;
using Cysharp.Threading.Tasks;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using MuvluvUnlockCG.Core;
using EventChapterEpisodeCell = Assets.GameUi.Episode.EventChapter.EpisodeCell.EventEpisodeCell;
using EventCatalogCell = Assets.GameUi.Episode.EventEpisodeCell;
using MainChapterEpisodeCell = Assets.GameUi.Episode.MainChapter.EpisodeCell.MainEpisodeCell;

namespace MuvluvUnlockCG;

internal static class HarmonyFailureCleanup
{
    internal static Exception Return(Exception exception, Il2CppObjectBase instance)
    {
        if (exception is not null)
        {
            MuvluvUnlockRuntime.OnHookFailure(instance, exception);
        }

        return exception;
    }

    internal static Exception ReturnAfterLeave(Exception exception, ScenarioController instance)
    {
        MuvluvUnlockRuntime.OnScenarioLeave(instance);
        if (exception is not null)
        {
            MuvluvUnlockRuntime.OnHookFailure(instance, exception);
        }

        return exception;
    }
}

internal static class HarmonyPatchDiagnostics
{
    /// <summary>
    /// Every Harmony patch class in this assembly, discovered by scanning rather than by hand
    /// registration, so a newly added patch class is covered without a second edit. Ordered by name
    /// to keep diagnostics stable across builds.
    /// </summary>
    private static readonly Type[] PatchTypes = ScanPatchTypes();

    private static Type[] ScanPatchTypes() => typeof(HarmonyPatchDiagnostics).Assembly
        .GetTypes()
        .Where(type => type.GetCustomAttributes(typeof(HarmonyPatch), inherit: false).Length > 0)
        .OrderBy(type => type.Name, StringComparer.Ordinal)
        .ToArray();

    /// <summary>
    /// Resolves every declared target before Harmony installs anything. A game update that moves a
    /// generated seam is reported per site, and the caller aborts loading so a partially patched
    /// plugin cannot run.
    /// </summary>
    internal static IReadOnlyList<string> Precheck(ManualLogSource log)
    {
        var failures = PatchPreflightPolicy.Check(
            PatchTypes.Select(patchType =>
                (patchType.Name, new Func<System.Reflection.MethodBase>(() => ResolveTarget(patchType)))));

        foreach (var failure in failures)
        {
            log.LogError($"{MuvluvUnlockRuntime.DiagnosticPrefix} precheck {failure}");
        }

        if (failures.Count == 0)
        {
            log.LogInfo(
                $"{MuvluvUnlockRuntime.DiagnosticPrefix} precheck passed: patches={PatchTypes.Length}");
        }

        return failures;
    }

    internal static bool LogInstall(Harmony harmony, ManualLogSource log)
    {
        try
        {
            var patchedMethods = new HashSet<MethodBase>(harmony.GetPatchedMethods());
            var allInstalled = true;
            foreach (var patchType in PatchTypes)
            {
                var target = ResolveTarget(patchType);
                var installed = target is not null && patchedMethods.Contains(target);
                allInstalled &= installed;
                var targetName = target is null
                    ? "<unresolved>"
                    : $"{target.DeclaringType?.FullName}.{target.Name}";
                log.LogInfo($"{MuvluvUnlockRuntime.DiagnosticPrefix} harmony patch={patchType.Name} target={targetName} installed={installed}");
            }

            return allInstalled;
        }
        catch (Exception exception)
        {
            log.LogError($"{MuvluvUnlockRuntime.DiagnosticPrefix} harmony install inspection failed; exception={exception.GetType().FullName}; stack={exception.StackTrace}");
            return false;
        }
    }

    private static MethodBase ResolveTarget(Type patchType)
    {
        var targetFactory = patchType.GetMethod(
            "TargetMethod",
            BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly);
        if (targetFactory is null)
        {
            throw new MissingMethodException(patchType.FullName, "TargetMethod");
        }

        return targetFactory.Invoke(null, null) as MethodBase;
    }
}

/// <summary>
/// Hooks are intentionally limited to the generated story/Character/Memory
/// outer chapter and individual-cell factories, the actual entry state machines, the high-level
/// SceneFrame provider, the post-frame Scenario refresh, the high-level read,
/// the generated branch state-machine handoff and its native no-POST guard, and
/// leave. Tracking remains inside the native high-level PostRead state machine
/// and is skipped by that boundary.
/// Lower-level dataSource, Blob, BestHTTP, Addressables, CDN, and DirectCache
/// paths remain untouched.
/// </summary>
/// <summary>
/// The runtime-resolved generated seams, named once. The compiler renumbers the numeric suffix
/// of a generated lambda when its enclosing method changes, so each entry pairs the exact current
/// name with the factory-family prefix that <see cref="GeneratedLambdaTargetResolver"/> may fall
/// back to. The hook contract asserts these exact values against the installed interop.
/// </summary>
internal static class GeneratedLambdaSeams
{
    internal const string CharacterCellName = "_GenerateCharacterCellArgs_b__60_0";
    internal const string CharacterCellFamily = "_GenerateCharacterCellArgs_b__";
    internal const string MemoryCellName = "_GenerateMemoryCellArgs_b__62_1";
    internal const string MemoryCellFamily = "_GenerateMemoryCellArgs_b__";
    internal const string MainChapterCellName = "_GenerateMainChapterCellArgs_b__79_1";
    internal const string MainChapterCellFamily = "_GenerateMainChapterCellArgs_b__";
    internal const string EventCellName = "_GenerateEventCellArgs_b__57_1";
    internal const string EventCellFamily = "_GenerateEventCellArgs_b__";
}

/// <summary>
/// Evidence: evidence/target-map.md row 1; evidence/GameUi-inventory.tsv:55907
/// (with 55848) records
/// _GenerateCharacterCellArgs_b__60_0 returning CharacterEpisodeCellArgs for
/// one Int64, and evidence/ilspy/EpisodeController.cs:5108 shows the same.
/// This is the individual character cell factory seam; the separately named
/// Viewable consumption in the entry state machine rests on the persisted ISIL
/// dumps cited by EligibilityPolicy.ResolveEntryViewable.
/// </summary>
[HarmonyPatch]
internal static class CharacterCellFactoryPatch
{
    private static MethodBase TargetMethod()
    {
        return GeneratedLambdaTargetResolver.Resolve(
            typeof(EpisodeController),
            GeneratedLambdaSeams.CharacterCellName,
            GeneratedLambdaSeams.CharacterCellFamily,
            new[] { typeof(long) },
            typeof(CharacterEpisodeCell.CharacterEpisodeCellArgs));
    }

    [HarmonyPostfix]
    private static void Postfix(
        EpisodeController __instance,
        long episodeMasterId,
        CharacterEpisodeCell.CharacterEpisodeCellArgs __result)
    {
        MuvluvUnlockRuntime.OnCharacterCellBuilt(__instance, episodeMasterId, __result);
    }
}

/// <summary>
/// Evidence: evidence/target-map.md row 2; evidence/GameUi-inventory.tsv:55910
/// (with 55851) records
/// _GenerateMemoryCellArgs_b__62_1 returning MemoryEpisodeCellArgs for one
/// Int64, and evidence/ilspy/EpisodeController.cs:5147 shows the same. The
/// sibling b__62_0 release filter remains native. The identically typed b__63_1
/// belongs to the separate GenerateSpecialMemoryCellArgs factory and is out of
/// this seam's family prefix. This postfix changes only LocalBypass visibility.
/// </summary>
[HarmonyPatch]
internal static class MemoryCellFactoryPatch
{
    private static MethodBase TargetMethod()
    {
        return GeneratedLambdaTargetResolver.Resolve(
            typeof(EpisodeController),
            GeneratedLambdaSeams.MemoryCellName,
            GeneratedLambdaSeams.MemoryCellFamily,
            new[] { typeof(long) },
            typeof(MemoryEpisodeCell.MemoryEpisodeCellArgs));
    }

    [HarmonyPostfix]
    private static void Postfix(
        EpisodeController __instance,
        long episodeMasterId,
        MemoryEpisodeCell.MemoryEpisodeCellArgs __result)
    {
        MuvluvUnlockRuntime.OnMemoryCellBuilt(__instance, episodeMasterId, __result);
    }
}

/// <summary>
/// Evidence: evidence/GameUi-inventory.tsv:193988 identifies
/// CharacterEpisodeCell+_ApplySyncPart_d__32 with args : CharacterEpisodeCellArgs.
/// The persisted ISIL dump for that state machine reads those args and drives
/// characterLockContainer and progressLockContainer. The factory args postfix can run before/after this
/// async consumer, so reapply only the captured LocalBypass presentation here;
/// Normal rows have no lease and remain native.
/// </summary>
[HarmonyPatch]
internal static class CharacterApplySyncPartStateMachinePatch
{
    private static MethodBase TargetMethod()
    {
        return AccessTools.Method(
            typeof(CharacterEpisodeCell._ApplySyncPart_d__32),
            "MoveNext",
            Type.EmptyTypes);
    }

    [HarmonyPrefix]
    private static void Prefix(CharacterEpisodeCell._ApplySyncPart_d__32 __instance)
    {
        MuvluvUnlockRuntime.OnCharacterApplySyncPartStateMachine(__instance);
    }

    [HarmonyFinalizer]
    private static Exception Finalizer(
        Exception __exception,
        CharacterEpisodeCell._ApplySyncPart_d__32 __instance)
    {
        return HarmonyFailureCleanup.Return(__exception, __instance);
    }
}

/// <summary>
/// Evidence: evidence/GameUi-inventory.tsv:194243 identifies
/// MemoryEpisodeCell+_Apply_d__16 with args : MemoryEpisodeCellArgs. The
/// persisted ISIL dump for that state machine reads args.Viewable to set
/// lockImage active. Reapply the exact
/// captured LocalBypass Viewable bit at that generated consumer seam.
/// </summary>
[HarmonyPatch]
internal static class MemoryApplyStateMachinePatch
{
    private static MethodBase TargetMethod()
    {
        return AccessTools.Method(
            typeof(MemoryEpisodeCell._Apply_d__16),
            "MoveNext",
            Type.EmptyTypes);
    }

    [HarmonyPrefix]
    private static void Prefix(MemoryEpisodeCell._Apply_d__16 __instance)
    {
        MuvluvUnlockRuntime.OnMemoryApplyStateMachine(__instance);
    }

    [HarmonyFinalizer]
    private static Exception Finalizer(
        Exception __exception,
        MemoryEpisodeCell._Apply_d__16 __instance)
    {
        return HarmonyFailureCleanup.Return(__exception, __instance);
    }
}

/// <summary>
/// Evidence: evidence/GameUi-inventory.tsv:194125 identifies the outer
/// Episode.EventEpisodeCell+_Apply_d__17 with its nested EventEpisodeUnlockArgs.
/// The persisted ISIL dump shows it consuming that relation's CanUnlock bit.
/// This seam keeps the rendered card aligned with its captured local decision.
/// </summary>
[HarmonyPatch]
internal static class EventCatalogApplyStateMachinePatch
{
    private static MethodBase TargetMethod()
    {
        return AccessTools.Method(
            typeof(EventCatalogCell._Apply_d__17),
            "MoveNext",
            Type.EmptyTypes);
    }

    [HarmonyPrefix]
    private static void Prefix(EventCatalogCell._Apply_d__17 __instance)
    {
        MuvluvUnlockRuntime.OnEventCatalogApplyStateMachine(__instance);
    }

    [HarmonyFinalizer]
    private static Exception Finalizer(
        Exception __exception,
        EventCatalogCell._Apply_d__17 __instance)
    {
        return HarmonyFailureCleanup.Return(__exception, __instance);
    }
}

/// <summary>
/// Evidence: evidence/target-map.md row 6; evidence/GameUi-inventory.tsv:195587
/// and evidence/ilspy/EpisodeController.cs:3613 show _SelectEventCell_d__65 with
/// __4__this, args, and __1__state. The persisted ISIL dump shows its MoveNext
/// reading args.Viewable and clearing EventEpisodeUnlockArgs through the nested
/// unlock relation. The prefix applies
/// the captured presentation only at state -1.
/// </summary>
[HarmonyPatch]
internal static class EventSelectCellStateMachinePatch
{
    private static MethodBase TargetMethod()
    {
        return AccessTools.Method(
            typeof(EpisodeController._SelectEventCell_d__65),
            "MoveNext",
            Type.EmptyTypes);
    }

    [HarmonyPrefix]
    private static void Prefix(EpisodeController._SelectEventCell_d__65 __instance)
    {
        MuvluvUnlockRuntime.OnEventSelectCellStateMachine(__instance);
    }

    [HarmonyFinalizer]
    private static Exception Finalizer(
        Exception __exception,
        EpisodeController._SelectEventCell_d__65 __instance)
    {
        return HarmonyFailureCleanup.Return(__exception, __instance);
    }
}

/// <summary>
/// Evidence: evidence/target-map.md row 7; evidence/GameUi-inventory.tsv:55913
/// and evidence/ilspy/EpisodeController.cs:5186 show
/// &lt;GenerateMainChapterCellArgs&gt;b__79_1(ChapterGroupMaster) building the outer
/// MainChapterCellArgs; the persisted ISIL dump of that lambda assigns the
/// native CanAccessChapter result to Viewable. Promote only a group containing
/// a chapter whose Master-derived SceneFrame exists locally.
/// </summary>
[HarmonyPatch]
internal static class MainChapterCellFactoryPatch
{
    private static MethodBase TargetMethod()
    {
        return GeneratedLambdaTargetResolver.Resolve(
            typeof(EpisodeController),
            GeneratedLambdaSeams.MainChapterCellName,
            GeneratedLambdaSeams.MainChapterCellFamily,
            new[] { typeof(ChapterGroupMaster) },
            typeof(MainChapterCell.MainChapterCellArgs));
    }

    [HarmonyPostfix]
    private static void Postfix(
        EpisodeController __instance,
        ChapterGroupMaster v,
        MainChapterCell.MainChapterCellArgs __result)
    {
        MuvluvUnlockRuntime.OnMainChapterCellBuilt(__instance, v, __result);
    }
}

/// <summary>
/// Evidence: evidence/target-map.md row 8; evidence/GameUi-inventory.tsv:195411
/// and evidence/ilspy/EpisodeController.cs:2585-2657 show
/// __c__DisplayClass78_0._RefreshMainChapterPanelModel_b__2(ChapterMaster)
/// returning MainChapterSelectButtonViewModel; the persisted ISIL dump shows
/// that lambda writing !CanAccessChapter to IsLock.
/// Local corpus availability may clear only that presentation lock.
/// </summary>
[HarmonyPatch]
internal static class MainChapterButtonFactoryPatch
{
    private static MethodBase TargetMethod()
    {
        return AccessTools.Method(
            typeof(EpisodeController.__c__DisplayClass78_0),
            "_RefreshMainChapterPanelModel_b__2",
            new[] { typeof(ChapterMaster) });
    }

    [HarmonyPostfix]
    private static void Postfix(
        EpisodeController.__c__DisplayClass78_0 __instance,
        ChapterMaster v,
        EpisodeController.MainChapterSelectButtonViewModel __result)
    {
        MuvluvUnlockRuntime.OnMainChapterButtonBuilt(
            __instance is null ? null : __instance.__4__this,
            v,
            __result);
    }
}

/// <summary>
/// Evidence: evidence/target-map.md row 9; evidence/GameUi-inventory.tsv:55901
/// and evidence/ilspy/EpisodeController.cs:5030 show
/// &lt;GenerateEventCellArgs&gt;b__57_1(long) returning EventEpisodeCellArgs; the
/// persisted ISIL dump of that lambda stores native access in Viewable. Promote only an exact chapter backed
/// by runtime Chapter -> Episode -> Scene Master relations; a missing local JSON
/// stays on the native SceneFrame acquisition path.
/// </summary>
[HarmonyPatch]
internal static class EventChapterCellFactoryPatch
{
    private static MethodBase TargetMethod()
    {
        return GeneratedLambdaTargetResolver.Resolve(
            typeof(EpisodeController),
            GeneratedLambdaSeams.EventCellName,
            GeneratedLambdaSeams.EventCellFamily,
            new[] { typeof(long) },
            typeof(EventCatalogCell.EventEpisodeCellArgs));
    }

    [HarmonyPostfix]
    private static void Postfix(
        EpisodeController __instance,
        long chapterMasterId,
        EventCatalogCell.EventEpisodeCellArgs __result)
    {
        MuvluvUnlockRuntime.OnEventChapterCellBuilt(__instance, chapterMasterId, __result);
    }
}

/// <summary>
/// Evidence: MainEpisodeController.txt:846-940 obtains the chapter-scoped
/// EpisodeMaster list, then applies b15_0 before b15_1. The postfix includes a
/// row only when the runtime Master relation and configured local SceneFrame
/// corpus prove availability; native chapter/location filtering remains
/// authoritative for every other row.
/// </summary>
[HarmonyPatch]
internal static class MainEpisodeFilterPatch
{
    private static MethodBase TargetMethod()
    {
        return AccessTools.Method(
            typeof(MainEpisodeController),
            "_GenerateEpisodeCellArgs_b__15_0",
            new[] { typeof(EpisodeMaster) });
    }

    [HarmonyPostfix]
    private static void Postfix(
        MainEpisodeController __instance,
        EpisodeMaster v,
        ref bool __result)
    {
        __result = MuvluvUnlockRuntime.ShouldIncludeMainStoryEpisode(__instance, v, __result);
    }
}

/// <summary>
/// Evidence: MainEpisodeController.txt:1320-1971 identifies b15_1 as the
/// individual MainEpisodeCellArgs factory and :1912-1914 writes Viewable. Only
/// that exact result object is handed to the local presentation registry.
/// </summary>
[HarmonyPatch]
internal static class MainEpisodeCellFactoryPatch
{
    private static MethodBase TargetMethod()
    {
        return AccessTools.Method(
            typeof(MainEpisodeController),
            "_GenerateEpisodeCellArgs_b__15_1",
            new[] { typeof(EpisodeMaster) });
    }

    [HarmonyPostfix]
    private static void Postfix(
        MainEpisodeController __instance,
        EpisodeMaster episodeMaster,
        MainChapterEpisodeCell.MainEpisodeCellArgs __result)
    {
        try
        {
            MuvluvUnlockRuntime.OnMainStoryCellBuilt(__instance, episodeMaster is null ? 0 : episodeMaster.Id, __result);
        }
        catch (Exception exception)
        {
            MuvluvUnlockRuntime.OnHookFailure(__instance, exception);
        }
    }
}

/// <summary>
/// Evidence: EventChapterController.txt:1343-1445 maps the runtime event Master
/// list through display-class b22_0, and its persisted ISIL :831-861 assigns the
/// native access result to EventEpisodeCellArgs.Viewable. The postfix changes
/// only that exact generated row.
/// </summary>
[HarmonyPatch]
internal static class EventEpisodeCellFactoryPatch
{
    private static MethodBase TargetMethod()
    {
        return AccessTools.Method(
            typeof(EventChapterController.__c__DisplayClass22_0),
            "_GenerateEpisodeCellArgs_b__0",
            new[] { typeof(EpisodeMaster) });
    }

    [HarmonyPostfix]
    private static void Postfix(
        EventChapterController.__c__DisplayClass22_0 __instance,
        EpisodeMaster episodeMaster,
        EventChapterEpisodeCell.EventEpisodeCellArgs __result)
    {
        try
        {
            var controller = __instance is null ? null : __instance.__4__this;
            MuvluvUnlockRuntime.OnEventStoryCellBuilt(controller, episodeMaster is null ? 0 : episodeMaster.Id, __result);
        }
        catch (Exception exception)
        {
            MuvluvUnlockRuntime.OnHookFailure(__instance, exception);
        }
    }
}

/// <summary>
/// Evidence: evidence/target-map.md row 13; evidence/GameUi-inventory.tsv:195428
/// and evidence/ilspy/EpisodeController.cs:2727 show _MoveToAdventure_d__67 with
/// args : CharacterEpisodeCellArgs and no type field, which identifies the
/// character entry state machine. Persisted callers invoke its MoveNext
/// directly, bypassing the public typed wrapper, and the persisted ISIL dump
/// shows the resulting state and args reads. The prefix therefore handles only
/// initial state -1; continuations are native resumes.
/// </summary>
[HarmonyPatch]
internal static class CharacterMoveToAdventureStateMachinePatch
{
    private static MethodBase TargetMethod()
    {
        return AccessTools.Method(
            typeof(EpisodeController._MoveToAdventure_d__67),
            "MoveNext",
            Type.EmptyTypes);
    }

    [HarmonyPrefix]
    private static void Prefix(
        EpisodeController._MoveToAdventure_d__67 __instance)
    {
        MuvluvUnlockRuntime.OnCharacterMoveToAdventureStateMachine(__instance);
    }

    [HarmonyFinalizer]
    private static Exception Finalizer(
        Exception __exception,
        EpisodeController._MoveToAdventure_d__67 __instance)
    {
        MuvluvUnlockRuntime.OnCharacterMoveToAdventureStateMachineExit(__instance, __exception);
        return HarmonyFailureCleanup.Return(__exception, __instance);
    }
}

/// <summary>
/// Evidence: evidence/target-map.md row 14; evidence/GameUi-inventory.tsv:195455
/// and evidence/ilspy/EpisodeController.cs:2879 show _MoveToAdventure_d__68 with
/// args : MemoryEpisodeCellArgs and type : EpisodeService.EpisodeTypes, which
/// identifies the memory entry state machine. Persisted callers invoke its
/// MoveNext directly, bypassing the public typed wrapper, and the persisted
/// ISIL dump shows the resulting state and args reads. The prefix therefore
/// handles only initial state -1; continuations are native resumes.
/// </summary>
[HarmonyPatch]
internal static class MemoryMoveToAdventureStateMachinePatch
{
    private static MethodBase TargetMethod()
    {
        return AccessTools.Method(
            typeof(EpisodeController._MoveToAdventure_d__68),
            "MoveNext",
            Type.EmptyTypes);
    }

    [HarmonyPrefix]
    private static void Prefix(
        EpisodeController._MoveToAdventure_d__68 __instance)
    {
        MuvluvUnlockRuntime.OnMemoryMoveToAdventureStateMachine(__instance);
    }

    [HarmonyFinalizer]
    private static Exception Finalizer(
        Exception __exception,
        EpisodeController._MoveToAdventure_d__68 __instance)
    {
        MuvluvUnlockRuntime.OnMemoryMoveToAdventureStateMachineExit(__instance, __exception);
        return HarmonyFailureCleanup.Return(__exception, __instance);
    }
}

/// <summary>
/// Evidence: MainEpisodeController_NestedType__MoveToScenario_d__14.txt:315-318
/// branches on the state machine's viewable field, and interop source lines
/// 216-300 exposes state=-1, episodeMasterId, viewable, and controller. The
/// prefix establishes an exact local generation only at initial state; all
/// resumed native states are untouched.
/// </summary>
[HarmonyPatch]
internal static class MainMoveToScenarioStateMachinePatch
{
    private static MethodBase TargetMethod()
    {
        return AccessTools.Method(
            typeof(MainEpisodeController._MoveToScenario_d__14),
            "MoveNext",
            Type.EmptyTypes);
    }

    [HarmonyPrefix]
    private static void Prefix(MainEpisodeController._MoveToScenario_d__14 __instance)
    {
        MuvluvUnlockRuntime.OnMainMoveToScenarioStateMachine(__instance);
    }

    [HarmonyFinalizer]
    private static Exception Finalizer(
        Exception __exception,
        MainEpisodeController._MoveToScenario_d__14 __instance)
    {
        MuvluvUnlockRuntime.OnMainMoveToScenarioStateMachineExit(__instance, __exception);
        return HarmonyFailureCleanup.Return(__exception, __instance);
    }
}

/// <summary>
/// Evidence: EventChapterController_NestedType__MoveToScenario_d__19.txt:293-296
/// branches on viewable, and interop source lines 936-1021 exposes its exact
/// initial state/episode/controller fields. The local entry seam is the
/// generated state machine, not a broad navigator or content hook.
/// </summary>
[HarmonyPatch]
internal static class EventMoveToScenarioStateMachinePatch
{
    private static MethodBase TargetMethod()
    {
        return AccessTools.Method(
            typeof(EventChapterController._MoveToScenario_d__19),
            "MoveNext",
            Type.EmptyTypes);
    }

    [HarmonyPrefix]
    private static void Prefix(EventChapterController._MoveToScenario_d__19 __instance)
    {
        MuvluvUnlockRuntime.OnEventMoveToScenarioStateMachine(__instance);
    }

    [HarmonyFinalizer]
    private static Exception Finalizer(
        Exception __exception,
        EventChapterController._MoveToScenario_d__19 __instance)
    {
        return HarmonyFailureCleanup.Return(__exception, __instance);
    }
}

/// <summary>
/// Evidence: selected ScenarioController_NestedType_Refresh_d__76.txt shows
/// the awaited frame data is assigned before it calls the no-argument private
/// ScenarioController.Refresh. Binding here therefore occurs only after native
/// refresh reaches the expected runtime SceneMasterId.
/// </summary>
[HarmonyPatch]
internal static class ScenarioRefreshPatch
{
    private static MethodBase TargetMethod()
    {
        return AccessTools.Method(typeof(ScenarioController), "Refresh", Type.EmptyTypes);
    }

    [HarmonyPostfix]
    private static void Postfix(ScenarioController __instance)
    {
        MuvluvUnlockRuntime.OnScenarioRefresh(__instance);
    }

    [HarmonyFinalizer]
    private static Exception Finalizer(Exception __exception, ScenarioController __instance)
    {
        return HarmonyFailureCleanup.Return(__exception, __instance);
    }
}

/// <summary>
/// Evidence: interop ScenarioController.cs lines 12755-12765 expose the private
/// PostRead() wrapper and selected d104 lines 417-432 call EpisodeService.PostRead.
/// The wrapper installs a one-shot start frame. Persisted ScenarioController.txt
/// lines 810-812 and 847 show the wrapper starts the builder and synchronously
/// invokes the initial state-machine MoveNext before returning; the wrapper
/// initializes d104 to -1 (full ISIL around line 13903), while d104 lines
/// 511-512 write zero on suspension.
/// </summary>
[HarmonyPatch]
internal static class ScenarioPostReadProvenancePatch
{
    private static MethodBase TargetMethod()
    {
        return AccessTools.Method(typeof(ScenarioController), "PostRead", Type.EmptyTypes);
    }

    [HarmonyPrefix]
    private static void Prefix(ScenarioController __instance)
    {
        MuvluvUnlockRuntime.OnScenarioPostReadStarted(__instance);
    }

    [HarmonyFinalizer]
    private static Exception Finalizer(Exception __exception, ScenarioController __instance)
    {
        if (__exception is not null)
        {
            MuvluvUnlockRuntime.OnScenarioPostReadStartFailure(__instance, __exception);
        }
        else
        {
            MuvluvUnlockRuntime.OnScenarioPostReadFinished(__instance);
        }

        return __exception;
    }
}

/// <summary>
/// Evidence: selected ScenarioController_NestedType__PostRead_d__104.txt lines
/// 417-432 and interop ScenarioController.cs lines 10541-10620 expose the exact
/// generated MoveNext seam. Each continuation restores the operation-scoped
/// identity after the native call, so stale same-ID continuations cannot borrow
/// a newer generation.
/// </summary>
[HarmonyPatch]
internal static class ScenarioPostReadStateMachinePatch
{
    private static MethodBase TargetMethod()
    {
        return AccessTools.Method(typeof(ScenarioController._PostRead_d__104), "MoveNext");
    }

    [HarmonyPrefix]
    private static void Prefix(ScenarioController._PostRead_d__104 __instance)
    {
        MuvluvUnlockRuntime.OnPostReadStateMachineMoveNext(__instance);
    }

    [HarmonyFinalizer]
    private static Exception Finalizer(
        Exception __exception,
        ScenarioController._PostRead_d__104 __instance)
    {
        return MuvluvUnlockRuntime.OnPostReadStateMachineExit(__instance, __exception);
    }
}

/// <summary>
/// Evidence: interop ScenarioController.cs lines 12741-12753 expose
/// PostBranchSelection(long, Nullable&lt;long&gt;) and selected d103 line 1383 calls
/// the shared branch API. The wrapper installs a one-shot start frame before
/// its synchronous initial MoveNext (ScenarioController.txt lines 683-685 and
/// 725); the wrapper initializes d103 to -1 (full ISIL around line 13770), while
/// d103 lines 1464-1466 write zero again when awaiting.
/// </summary>
[HarmonyPatch]
internal static class ScenarioPostBranchSelectionProvenancePatch
{
    private static MethodBase TargetMethod()
    {
        return AccessTools.Method(
            typeof(ScenarioController),
            "PostBranchSelection",
            new[] { typeof(long), typeof(Il2CppSystem.Nullable<long>) });
    }

    [HarmonyPrefix]
    private static void Prefix(ScenarioController __instance)
    {
        MuvluvUnlockRuntime.OnScenarioPostBranchSelectionStarted(__instance);
    }

    [HarmonyFinalizer]
    private static Exception Finalizer(Exception __exception, ScenarioController __instance)
    {
        if (__exception is not null)
        {
            MuvluvUnlockRuntime.OnScenarioPostBranchSelectionStartFailure(__instance, __exception);
        }
        else
        {
            MuvluvUnlockRuntime.OnScenarioPostBranchSelectionFinished(__instance);
        }

        return __exception;
    }
}

/// <summary>
/// Evidence: selected ScenarioController_NestedType__PostBranchSelection_d__103.txt
/// lines 543-544 skip the remote branch block when sceneMasterId is non-positive,
/// while lines 1108-1270 still apply history, answer, and choice-close state.
/// The prefix temporarily selects that native guard only for an exact local
/// generation; the finalizer restores the original scene ID.
/// </summary>
[HarmonyPatch]
internal static class ScenarioPostBranchSelectionStateMachinePatch
{
    private static MethodBase TargetMethod()
    {
        return AccessTools.Method(typeof(ScenarioController._PostBranchSelection_d__103), "MoveNext");
    }

    [HarmonyPrefix]
    private static void Prefix(
        ScenarioController._PostBranchSelection_d__103 __instance,
        out long __state)
    {
        __state = MuvluvUnlockRuntime.OnPostBranchSelectionStateMachineMoveNext(__instance);
    }

    [HarmonyFinalizer]
    private static Exception Finalizer(
        Exception __exception,
        ScenarioController._PostBranchSelection_d__103 __instance,
        long __state)
    {
        return MuvluvUnlockRuntime.OnPostBranchSelectionStateMachineExit(__instance, __state, __exception);
    }
}

/// <summary>
/// Evidence: selected ScenarioController.txt and
/// ScenarioController_NestedType__Leave_d__85.txt identify Leave as the native
/// scenario lifecycle boundary where an asynchronous local session is cleared.
/// </summary>
[HarmonyPatch]
internal static class ScenarioLeavePatch
{
    private static MethodBase TargetMethod()
    {
        return AccessTools.Method(typeof(ScenarioController), "Leave", Type.EmptyTypes);
    }

    [HarmonyPrefix]
    private static void Prefix(ScenarioController __instance)
    {
        MuvluvUnlockRuntime.OnScenarioLeave(__instance);
    }

    [HarmonyFinalizer]
    private static Exception Finalizer(Exception __exception, ScenarioController __instance)
    {
        return HarmonyFailureCleanup.ReturnAfterLeave(__exception, __instance);
    }
}

/// <summary>
/// Evidence: selected DownloadSceneFrameMasters d__15 lines 542-624 check the
/// native SceneFrame dictionary first and call GetApiEpisodeScenesDataSource only
/// on a miss. This prefix supplies the same typed array only for the exact active
/// LocalBypass service/Scene generation; every other call remains native.
/// </summary>
[HarmonyPatch]
internal static class EpisodeServiceSceneFramesPatch
{
    private static MethodBase TargetMethod()
    {
        return AccessTools.Method(
            typeof(EpisodeService),
            "DownloadSceneFrameMasters",
            new[] { typeof(long) });
    }

    [HarmonyPrefix]
    private static bool Prefix(
        EpisodeService __instance,
        long sceneMasterId,
        ref UniTask<Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<SceneFrameMaster>> __result)
    {
        return !MuvluvUnlockRuntime.TryProvideLocalSceneFrames(
            __instance,
            sceneMasterId,
            out __result);
    }

    [HarmonyFinalizer]
    private static Exception Finalizer(Exception __exception, EpisodeService __instance)
    {
        return HarmonyFailureCleanup.Return(__exception, __instance);
    }
}

/// <summary>
/// Evidence: selected EpisodeService.txt and PostRead_d__16.txt lines 2335 and
/// 2403 show tracking calls inside this native high-level read/reward/progress
/// boundary. A local guard returns an already completed task only after an exact
/// bound service match.
/// </summary>
[HarmonyPatch]
internal static class EpisodeServicePostReadPatch
{
    private static MethodBase TargetMethod()
    {
        return AccessTools.Method(
            typeof(EpisodeService),
            "PostRead",
            new[] { typeof(long), typeof(bool), typeof(bool) });
    }

    [HarmonyPrefix]
    private static bool Prefix(
        EpisodeService __instance,
        long episodeMasterId,
        ref UniTask __result)
    {
        if (!MuvluvUnlockRuntime.ShouldSuppressPostRead(__instance, episodeMasterId))
        {
            return true;
        }

        __result = UniTask.CompletedTask;
        return false;
    }

    [HarmonyFinalizer]
    private static Exception Finalizer(Exception __exception, EpisodeService __instance)
    {
        return HarmonyFailureCleanup.Return(__exception, __instance);
    }
}
