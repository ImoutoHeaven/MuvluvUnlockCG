using System;
using System.Collections;
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
using HarmonyLib;
using Cysharp.Threading.Tasks;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using EventChapterEpisodeCell = Assets.GameUi.Episode.EventChapter.EpisodeCell.EventEpisodeCell;
using EventCatalogCell = Assets.GameUi.Episode.EventEpisodeCell;
using MainChapterEpisodeCell = Assets.GameUi.Episode.MainChapter.EpisodeCell.MainEpisodeCell;

namespace MuvluvUnlockCG.HookContract;

/// <summary>
/// Normalized public-in-test representation of an installable Harmony callback.
/// Types are retained as reflection Type objects instead of ambiguous display
/// strings, and every parameter keeps its source name and ref/out/in modifier.
/// </summary>
public readonly record struct CallbackParameterSignature(
    string Name,
    Type ParameterType,
    CallbackParameterModifier Modifier);

public enum CallbackParameterModifier
{
    Value,
    Ref,
    Out,
    In,
}

public readonly record struct CallbackSignature(
    string MethodName,
    Type AnnotationType,
    bool IsStatic,
    Type ReturnType,
    CallbackParameterSignature[] Parameters);

internal static class Program
{
    private readonly record struct HookCase(
        string PatchType,
        string DeclaringType,
        string MethodName,
        string[] ParameterTypes,
        CallbackSignature[] Callbacks,
        string Evidence);

    private static readonly HookCase[] Cases =
    {
        new HookCase(
            "CharacterCellFactoryPatch",
            "Assets.GameUi.Episode.EpisodeController",
            "_GenerateCharacterCellArgs_b__54_0",
            new[] { "System.Int64" },
            new[]
            {
                Postfix(
                    Value("__instance", typeof(EpisodeController)),
                    Value("episodeMasterId", typeof(long)),
                    Value("__result", typeof(CharacterEpisodeCell.CharacterEpisodeCellArgs))),
            },
            "evidence/decomp/selected/GameUi__Assets__GameUi__Episode__EpisodeController.txt:<GenerateCharacterCellArgs>b__54_0"),
        new HookCase(
            "MemoryCellFactoryPatch",
            "Assets.GameUi.Episode.EpisodeController",
            "_GenerateMemoryCellArgs_b__56_1",
            new[] { "System.Int64" },
            new[]
            {
                Postfix(
                    Value("__instance", typeof(EpisodeController)),
                    Value("episodeMasterId", typeof(long)),
                    Value("__result", typeof(MemoryEpisodeCell.MemoryEpisodeCellArgs))),
            },
            "evidence/decomp/selected/GameUi__Assets__GameUi__Episode__EpisodeController.txt:<GenerateMemoryCellArgs>b__56_1"),
        new HookCase(
            "CharacterApplySyncPartStateMachinePatch",
            "Assets.GameUi.Episode.CharacterEpisodeCell+_ApplySyncPart_d__32",
            "MoveNext",
            Array.Empty<string>(),
            new[]
            {
                Prefix(
                    typeof(void),
                    Value("__instance", typeof(CharacterEpisodeCell._ApplySyncPart_d__32))),
                Finalizer(
                    Value("__exception", typeof(Exception)),
                    Value("__instance", typeof(CharacterEpisodeCell._ApplySyncPart_d__32))),
            },
            "evidence/decomp/full-isil/IsilDump/GameUi/Assets/GameUi/Episode/CharacterEpisodeCell_NestedType__ApplySyncPart_d__32.txt:1024-1142"),
        new HookCase(
            "MemoryApplyStateMachinePatch",
            "Assets.GameUi.Episode.MemoryEpisodeCell+_Apply_d__16",
            "MoveNext",
            Array.Empty<string>(),
            new[]
            {
                Prefix(
                    typeof(void),
                    Value("__instance", typeof(MemoryEpisodeCell._Apply_d__16))),
                Finalizer(
                    Value("__exception", typeof(Exception)),
                    Value("__instance", typeof(MemoryEpisodeCell._Apply_d__16))),
            },
            "evidence/decomp/full-isil/IsilDump/GameUi/Assets/GameUi/Episode/MemoryEpisodeCell_NestedType__Apply_d__16.txt:423-460"),
        new HookCase(
            "EventCatalogApplyStateMachinePatch",
            "Assets.GameUi.Episode.EventEpisodeCell+_Apply_d__17",
            "MoveNext",
            Array.Empty<string>(),
            new[]
            {
                Prefix(
                    typeof(void),
                    Value("__instance", typeof(EventCatalogCell._Apply_d__17))),
                Finalizer(
                    Value("__exception", typeof(Exception)),
                    Value("__instance", typeof(EventCatalogCell._Apply_d__17))),
            },
            "evidence/decomp/full-isil/IsilDump/GameUi/Assets/GameUi/Episode/EventEpisodeCell_NestedType__Apply_d__17.txt:930-1025"),
        new HookCase(
            "EventSelectCellStateMachinePatch",
            "Assets.GameUi.Episode.EpisodeController+_SelectEventCell_d__58",
            "MoveNext",
            Array.Empty<string>(),
            new[]
            {
                Prefix(
                    typeof(void),
                    Value("__instance", typeof(EpisodeController._SelectEventCell_d__58))),
                Finalizer(
                    Value("__exception", typeof(Exception)),
                    Value("__instance", typeof(EpisodeController._SelectEventCell_d__58))),
            },
            "evidence/decomp/full-isil/IsilDump/GameUi/Assets/GameUi/Episode/EpisodeComponent_NestedType___ProcessOnCreate_b__32_18_d.txt:359-364; evidence/decomp/full-isil/IsilDump/GameUi/Assets/GameUi/Episode/EpisodeController_NestedType__SelectEventCell_d__58.txt:803-832,978-1013; evidence/decomp/interop-src/GameUi/Assets.GameUi.Episode/EpisodeController.cs:3299-3377"),
        new HookCase(
            "MainChapterCellFactoryPatch",
            "Assets.GameUi.Episode.EpisodeController",
            "_GenerateMainChapterCellArgs_b__72_1",
            new[] { "Assets.Api.Client.ChapterGroupMaster" },
            new[]
            {
                Postfix(
                    Value("__instance", typeof(EpisodeController)),
                    Value("v", typeof(ChapterGroupMaster)),
                    Value("__result", typeof(MainChapterCell.MainChapterCellArgs))),
            },
            "evidence/decomp/full-isil/IsilDump/GameUi/Assets/GameUi/Episode/EpisodeController.txt:13642-13764"),
        new HookCase(
            "MainChapterButtonFactoryPatch",
            "Assets.GameUi.Episode.EpisodeController+__c__DisplayClass71_0",
            "_RefreshMainChapterPanelModel_b__2",
            new[] { "Assets.Api.Client.ChapterMaster" },
            new[]
            {
                Postfix(
                    Value("__instance", typeof(EpisodeController.__c__DisplayClass71_0)),
                    Value("v", typeof(ChapterMaster)),
                    Value("__result", typeof(EpisodeController.MainChapterSelectButtonViewModel))),
            },
            "evidence/decomp/full-isil/IsilDump/GameUi/Assets/GameUi/Episode/EpisodeController_NestedType___c__DisplayClass71_0.txt:480-515"),
        new HookCase(
            "EventChapterCellFactoryPatch",
            "Assets.GameUi.Episode.EpisodeController",
            "_GenerateEventCellArgs_b__51_1",
            new[] { "System.Int64" },
            new[]
            {
                Postfix(
                    Value("__instance", typeof(EpisodeController)),
                    Value("chapterMasterId", typeof(long)),
                    Value("__result", typeof(EventCatalogCell.EventEpisodeCellArgs))),
            },
            "evidence/decomp/full-isil/IsilDump/GameUi/Assets/GameUi/Episode/EpisodeController.txt:8955-9008"),
        new HookCase(
            "MainEpisodeFilterPatch",
            "Assets.GameUi.Episode.MainChapter.MainEpisodeController",
            "_GenerateEpisodeCellArgs_b__15_0",
            new[] { "Assets.Api.Client.EpisodeMaster" },
            new[]
            {
                Postfix(
                    Value("__instance", typeof(MainEpisodeController)),
                    Value("v", typeof(EpisodeMaster)),
                    Ref("__result", typeof(bool))),
            },
            "evidence/decomp/full-isil/IsilDump/GameUi/Assets/GameUi/Episode/MainChapter/MainEpisodeController.txt:846-940"),
        new HookCase(
            "MainEpisodeCellFactoryPatch",
            "Assets.GameUi.Episode.MainChapter.MainEpisodeController",
            "_GenerateEpisodeCellArgs_b__15_1",
            new[] { "Assets.Api.Client.EpisodeMaster" },
            new[]
            {
                Postfix(
                    Value("__instance", typeof(MainEpisodeController)),
                    Value("episodeMaster", typeof(EpisodeMaster)),
                    Value("__result", typeof(MainChapterEpisodeCell.MainEpisodeCellArgs))),
            },
            "evidence/decomp/full-isil/IsilDump/GameUi/Assets/GameUi/Episode/MainChapter/MainEpisodeController.txt:1320-1971"),
        new HookCase(
            "EventEpisodeCellFactoryPatch",
            "Assets.GameUi.Episode.EventChapter.EventChapterController+__c__DisplayClass22_0",
            "_GenerateEpisodeCellArgs_b__0",
            new[] { "Assets.Api.Client.EpisodeMaster" },
            new[]
            {
                Postfix(
                    Value("__instance", typeof(EventChapterController.__c__DisplayClass22_0)),
                    Value("episodeMaster", typeof(EpisodeMaster)),
                    Value("__result", typeof(EventChapterEpisodeCell.EventEpisodeCellArgs))),
            },
            "evidence/decomp/full-isil/IsilDump/GameUi/Assets/GameUi/Episode/EventChapter/EventChapterController.txt:1343-1445"),
        new HookCase(
            "CharacterMoveToAdventureStateMachinePatch",
            "Assets.GameUi.Episode.EpisodeController+_MoveToAdventure_d__60",
            "MoveNext",
            Array.Empty<string>(),
            new[]
            {
                Prefix(
                    typeof(void),
                    Value("__instance", typeof(EpisodeController._MoveToAdventure_d__60))),
                Finalizer(
                    Value("__exception", typeof(Exception)),
                    Value("__instance", typeof(EpisodeController._MoveToAdventure_d__60))),
            },
            "evidence/decomp/full-isil/IsilDump/GameUi/Assets/GameUi/Episode/EpisodeComponent_NestedType__SelectCharacterCell_d__39.txt:505-510; evidence/decomp/full-isil/IsilDump/GameUi/Assets/GameUi/Episode/EpisodeController.txt:3526-3648 (state=-1 at 3562); evidence/decomp/interop-src/GameUi/Assets.GameUi.Episode/EpisodeController.cs:2429-2556"),
        new HookCase(
            "MemoryMoveToAdventureStateMachinePatch",
            "Assets.GameUi.Episode.EpisodeController+_MoveToAdventure_d__61",
            "MoveNext",
            Array.Empty<string>(),
            new[]
            {
                Prefix(
                    typeof(void),
                    Value("__instance", typeof(EpisodeController._MoveToAdventure_d__61))),
                Finalizer(
                    Value("__exception", typeof(Exception)),
                    Value("__instance", typeof(EpisodeController._MoveToAdventure_d__61))),
            },
            "evidence/decomp/full-isil/IsilDump/GameUi/Assets/GameUi/Episode/EpisodeComponent_NestedType___ProcessOnCreate_b__32_20_d.txt:351-356; evidence/decomp/full-isil/IsilDump/GameUi/Assets/GameUi/Episode/EpisodeController.txt:3664-3780 (state=-1 at 3697); evidence/decomp/interop-src/GameUi/Assets.GameUi.Episode/EpisodeController.cs:2581-2691"),
        new HookCase(
            "MainMoveToScenarioStateMachinePatch",
            "Assets.GameUi.Episode.MainChapter.MainEpisodeController+_MoveToScenario_d__14",
            "MoveNext",
            Array.Empty<string>(),
            new[]
            {
                Prefix(
                    typeof(void),
                    Value("__instance", typeof(MainEpisodeController._MoveToScenario_d__14))),
                Finalizer(
                    Value("__exception", typeof(Exception)),
                    Value("__instance", typeof(MainEpisodeController._MoveToScenario_d__14))),
            },
            "evidence/decomp/full-isil/IsilDump/GameUi/Assets/GameUi/Episode/MainChapter/MainEpisodeController_NestedType__MoveToScenario_d__14.txt:315-318; evidence/decomp/interop-src/GameUi/Assets.GameUi.Episode.MainChapter/MainEpisodeController.cs:216-314"),
        new HookCase(
            "EventMoveToScenarioStateMachinePatch",
            "Assets.GameUi.Episode.EventChapter.EventChapterController+_MoveToScenario_d__19",
            "MoveNext",
            Array.Empty<string>(),
            new[]
            {
                Prefix(
                    typeof(void),
                    Value("__instance", typeof(EventChapterController._MoveToScenario_d__19))),
                Finalizer(
                    Value("__exception", typeof(Exception)),
                    Value("__instance", typeof(EventChapterController._MoveToScenario_d__19))),
            },
            "evidence/decomp/full-isil/IsilDump/GameUi/Assets/GameUi/Episode/EventChapter/EventChapterController_NestedType__MoveToScenario_d__19.txt:293-296; evidence/decomp/interop-src/GameUi/Assets.GameUi.Episode.EventChapter/EventChapterController.cs:936-1035"),
        new HookCase(
            "ScenarioRefreshPatch",
            "Assets.GameUi.Scenario.ScenarioController",
            "Refresh",
            Array.Empty<string>(),
            new[]
            {
                Postfix(Value("__instance", typeof(ScenarioController))),
                Finalizer(
                    Value("__exception", typeof(Exception)),
                    Value("__instance", typeof(ScenarioController))),
            },
            "evidence/decomp/selected/GameUi__Assets__GameUi__Scenario__ScenarioController_NestedType__Refresh_d__76.txt:MoveNext"),
        new HookCase(
            "ScenarioPostReadProvenancePatch",
            "Assets.GameUi.Scenario.ScenarioController",
            "PostRead",
            Array.Empty<string>(),
            new[]
            {
                Prefix(typeof(void), Value("__instance", typeof(ScenarioController))),
                Finalizer(
                    Value("__exception", typeof(Exception)),
                    Value("__instance", typeof(ScenarioController))),
            },
            "evidence/decomp/interop-src/GameUi/Assets.GameUi.Scenario/ScenarioController.cs:PostRead()"),
        new HookCase(
            "ScenarioPostReadStateMachinePatch",
            "Assets.GameUi.Scenario.ScenarioController+_PostRead_d__104",
            "MoveNext",
            Array.Empty<string>(),
            new[]
            {
                Prefix(
                    typeof(void),
                    Value("__instance", typeof(ScenarioController._PostRead_d__104))),
                Finalizer(
                    Value("__exception", typeof(Exception)),
                    Value("__instance", typeof(ScenarioController._PostRead_d__104))),
            },
            "evidence/decomp/selected/GameUi__Assets__GameUi__Scenario__ScenarioController_NestedType__PostRead_d__104.txt:MoveNext"),
        new HookCase(
            "ScenarioPostBranchSelectionProvenancePatch",
            "Assets.GameUi.Scenario.ScenarioController",
            "PostBranchSelection",
            new[] { "System.Int64", "Il2CppSystem.Nullable`1[[System.Int64, System.Private.CoreLib, Version=8.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e]]" },
            new[]
            {
                Prefix(typeof(void), Value("__instance", typeof(ScenarioController))),
                Finalizer(
                    Value("__exception", typeof(Exception)),
                    Value("__instance", typeof(ScenarioController))),
            },
            "evidence/decomp/interop-src/GameUi/Assets.GameUi.Scenario/ScenarioController.cs:PostBranchSelection(long, Nullable<long>)"),
        new HookCase(
            "ScenarioPostBranchSelectionStateMachinePatch",
            "Assets.GameUi.Scenario.ScenarioController+_PostBranchSelection_d__103",
            "MoveNext",
            Array.Empty<string>(),
            new[]
            {
                Prefix(
                    typeof(void),
                    Value("__instance", typeof(ScenarioController._PostBranchSelection_d__103)),
                    Out("__state", typeof(long))),
                Finalizer(
                    Value("__exception", typeof(Exception)),
                    Value("__instance", typeof(ScenarioController._PostBranchSelection_d__103)),
                    Value("__state", typeof(long))),
            },
            "evidence/decomp/selected/GameUi__Assets__GameUi__Scenario__ScenarioController_NestedType__PostBranchSelection_d__103.txt:543-544,1108-1270"),
        new HookCase(
            "ScenarioLeavePatch",
            "Assets.GameUi.Scenario.ScenarioController",
            "Leave",
            Array.Empty<string>(),
            new[]
            {
                Prefix(typeof(void), Value("__instance", typeof(ScenarioController))),
                Finalizer(
                    Value("__exception", typeof(Exception)),
                    Value("__instance", typeof(ScenarioController))),
            },
            "evidence/decomp/selected/GameUi__Assets__GameUi__Scenario__ScenarioController_NestedType__Leave_d__85.txt:MoveNext"),
        new HookCase(
            "EpisodeServiceSceneFramesPatch",
            "Assets.GameUi.Service.EpisodeService",
            "DownloadSceneFrameMasters",
            new[] { "System.Int64" },
            new[]
            {
                Prefix(
                    typeof(bool),
                    Value("__instance", typeof(EpisodeService)),
                    Value("sceneMasterId", typeof(long)),
                    Ref("__result", typeof(UniTask<Il2CppReferenceArray<SceneFrameMaster>>))),
                Finalizer(
                    Value("__exception", typeof(Exception)),
                    Value("__instance", typeof(EpisodeService))),
            },
            "evidence/decomp/selected/GameUi__Assets__GameUi__Service__EpisodeService_NestedType__DownloadSceneFrameMasters_d__15.txt:cache-before-dataSource"),
        new HookCase(
            "EpisodeServicePostReadPatch",
            "Assets.GameUi.Service.EpisodeService",
            "PostRead",
            new[] { "System.Int64", "System.Boolean", "System.Boolean" },
            new[]
            {
                Prefix(
                    typeof(bool),
                    Value("__instance", typeof(EpisodeService)),
                    Value("episodeMasterId", typeof(long)),
                    Ref("__result", typeof(UniTask))),
                Finalizer(
                    Value("__exception", typeof(Exception)),
                    Value("__instance", typeof(EpisodeService))),
            },
            "evidence/decomp/selected/GameUi__Assets__GameUi__Service__EpisodeService_NestedType__PostRead_d__16.txt:MoveNext"),
    };

    private static CallbackParameterSignature Value(string name, Type parameterType)
        => new(name, parameterType, CallbackParameterModifier.Value);

    private static CallbackParameterSignature Ref(string name, Type parameterType)
        => new(name, parameterType, CallbackParameterModifier.Ref);

    private static CallbackParameterSignature Out(string name, Type parameterType)
        => new(name, parameterType, CallbackParameterModifier.Out);

    private static CallbackSignature Prefix(
        Type returnType,
        params CallbackParameterSignature[] parameters)
        => new("Prefix", typeof(HarmonyPrefix), true, returnType, parameters);

    private static CallbackSignature Postfix(params CallbackParameterSignature[] parameters)
        => new("Postfix", typeof(HarmonyPostfix), true, typeof(void), parameters);

    private static CallbackSignature Finalizer(params CallbackParameterSignature[] parameters)
        => new("Finalizer", typeof(HarmonyFinalizer), true, typeof(Exception), parameters);

    private static int Main()
    {
        var assembly = typeof(global::MuvluvUnlockCG.MuvluvUnlockCGPlugin).Assembly;
        var failures = new List<string>();
        var expectedPatchTypes = new HashSet<string>(
            Cases.Select(hook => hook.PatchType),
            StringComparer.Ordinal);
        var actualPatchTypes = assembly.GetTypes()
            .Where(type => type.GetCustomAttributes(typeof(HarmonyPatch), inherit: false).Length > 0)
            .ToArray();
        var actualTargets = new Dictionary<string, MethodBase>(StringComparer.Ordinal);
        var actualCallbacks = new Dictionary<string, CallbackSignature[]>(StringComparer.Ordinal);
        foreach (var patchType in actualPatchTypes)
        {
            actualCallbacks[patchType.Name] = ResolveCallbacks(patchType, failures);
            try
            {
                var targetMethodFactory = patchType.GetMethod(
                    "TargetMethod",
                    BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly);
                if (targetMethodFactory is null)
                {
                    throw new InvalidOperationException("TargetMethod factory is missing");
                }

                var target = targetMethodFactory.Invoke(null, null) as MethodBase;
                if (target is null)
                {
                    throw new InvalidOperationException("TargetMethod resolved null");
                }

                if (!actualTargets.TryAdd(patchType.Name, target))
                {
                    throw new InvalidOperationException("duplicate patch type name");
                }
            }
            catch (Exception exception)
            {
                failures.Add($"{patchType.Name}: {exception.GetBaseException().Message}");
            }
        }

        if (actualPatchTypes.Length != Cases.Length)
        {
            failures.Add($"Harmony patch count expected {Cases.Length}, actual {actualPatchTypes.Length}");
        }

        var actualPatchTypeNames = new HashSet<string>(
            actualPatchTypes.Select(type => type.Name),
            StringComparer.Ordinal);
        foreach (var expectedPatchType in expectedPatchTypes)
        {
            if (!actualPatchTypeNames.Contains(expectedPatchType))
            {
                failures.Add($"expected Harmony patch missing: {expectedPatchType}");
            }
        }

        foreach (var actualPatchType in actualPatchTypeNames)
        {
            if (!expectedPatchTypes.Contains(actualPatchType))
            {
                failures.Add($"unexpected Harmony patch present: {actualPatchType}");
            }
        }

        foreach (var hook in Cases)
        {
            if (!actualTargets.TryGetValue(hook.PatchType, out var target))
            {
                continue;
            }

            try
            {
                Equal(hook.DeclaringType, target.DeclaringType?.FullName, "declaring type");
                Equal(hook.MethodName, target.Name, "method name");
                var parameterTypes = target.GetParameters().Select(parameter => parameter.ParameterType.FullName).ToArray();
                if (!parameterTypes.SequenceEqual(hook.ParameterTypes))
                {
                    throw new InvalidOperationException(
                        $"parameter types expected [{string.Join(", ", hook.ParameterTypes)}], " +
                        $"actual [{string.Join(", ", parameterTypes)}]");
                }

                if (!actualCallbacks.TryGetValue(hook.PatchType, out var actualCallbacksForPatch)
                    || !CallbackSetsEqual(hook.Callbacks, actualCallbacksForPatch))
                {
                    var actual = actualCallbacksForPatch is null
                        ? "<missing>"
                        : string.Join(", ", actualCallbacksForPatch.Select(Describe));
                    throw new InvalidOperationException(
                        $"callback set expected [{string.Join(", ", hook.Callbacks.Select(Describe))}], actual [{actual}]");
                }

                Console.WriteLine($"PASS {hook.PatchType} <- {hook.Evidence}");
            }
            catch (Exception exception)
            {
                failures.Add($"{hook.PatchType}: {exception.GetBaseException().Message}");
            }
        }

        var forbiddenTargetNames = new[]
        {
            "GetApiEpisodeScenesDataSource",
            "GetBlob",
            "BestHTTP",
            "Addressables",
            "DirectCache",
            "PostApiEpisodeScenesBranchSelection",
        };
        foreach (var target in actualTargets.Values)
        {
            var declaringType = target.DeclaringType?.FullName ?? string.Empty;
            foreach (var forbidden in forbiddenTargetNames)
            {
                if (string.Equals(target.Name, forbidden, StringComparison.Ordinal)
                    || declaringType.Contains(forbidden, StringComparison.Ordinal))
                {
                    failures.Add($"forbidden content hook present: {declaringType}.{target.Name}");
                }
            }
        }

        VerifyNativeEntryRetainsCatalogDecision(assembly, failures);

        if (failures.Count > 0)
        {
            foreach (var failure in failures)
            {
                Console.Error.WriteLine($"FAIL {failure}");
            }

            return 1;
        }

        Console.WriteLine($"hook-contract: {actualPatchTypes.Length}/{Cases.Length} actual patches matched; lower-level content hooks absent");
        return 0;
    }

    private static void VerifyNativeEntryRetainsCatalogDecision(
        Assembly assembly,
        List<string> failures)
    {
        const string contract = "NativeEntryCatalogRetention";
        MethodInfo clearAll = null;
        try
        {
            var runtime = assembly.GetType("MuvluvUnlockCG.MuvluvUnlockRuntime", throwOnError: true);
            var catalogField = runtime.GetField(
                "CatalogDecisions",
                BindingFlags.NonPublic | BindingFlags.Static);
            var clearForEntry = runtime.GetMethod(
                "ClearForEntry",
                BindingFlags.Public | BindingFlags.Static);
            clearAll = runtime.GetMethod(
                "ClearAll",
                BindingFlags.Public | BindingFlags.Static);
            if (catalogField?.GetValue(null) is not IDictionary catalog
                || clearForEntry is null
                || clearAll is null)
            {
                throw new InvalidOperationException("runtime catalog cleanup seam is missing");
            }

            clearAll.Invoke(null, new object[] { "hook-contract-setup" });
            var keyType = catalog.GetType().GetGenericArguments()[0];
            var key = Activator.CreateInstance(
                keyType,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                args: new object[] { (IntPtr)73, 901L },
                culture: null);
            catalog.Add(
                key,
                new global::MuvluvUnlockCG.Core.EligibilityDecision(
                    global::MuvluvUnlockCG.Core.PlaybackMode.LocalBypass,
                    global::MuvluvUnlockCG.Core.BypassReason.UnownedMemory));

            clearForEntry.Invoke(null, new object[] { null, 902L, "memory-entry-normal-contract" });
            if (catalog.Count != 1)
            {
                throw new InvalidOperationException(
                    $"native entry erased sibling catalog decisions; expected 1, actual {catalog.Count}");
            }

            clearAll.Invoke(null, new object[] { "hook-contract-full-clear" });
            if (catalog.Count != 0)
            {
                throw new InvalidOperationException("full cleanup retained catalog decisions");
            }

            Console.WriteLine($"PASS {contract} <- native d60/d61 entry cleanup retains current catalog decisions");
        }
        catch (Exception exception)
        {
            failures.Add($"{contract}: {exception.GetBaseException().Message}");
        }
        finally
        {
            try
            {
                clearAll?.Invoke(null, new object[] { "hook-contract-cleanup" });
            }
            catch
            {
            }
        }
    }

    private static CallbackSignature[] ResolveCallbacks(Type patchType, List<string> failures)
    {
        var callbacks = new List<CallbackSignature>();
        var declaredMethods = patchType.GetMethods(
            BindingFlags.Public
            | BindingFlags.NonPublic
            | BindingFlags.Static
            | BindingFlags.Instance
            | BindingFlags.DeclaredOnly);
        foreach (var method in declaredMethods)
        {
            foreach (var annotationType in GetHarmonyAnnotations(method))
            {
                callbacks.Add(NormalizeCallback(method, annotationType));
            }
        }

        var inheritedCallbacks = patchType.GetMethods(
                BindingFlags.Public
                | BindingFlags.NonPublic
                | BindingFlags.Static
                | BindingFlags.Instance)
            .Where(method => method.DeclaringType != patchType)
            .Where(HasCallbackAnnotation)
            .Select(method => method.Name)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (inheritedCallbacks.Length > 0)
        {
            failures.Add($"{patchType.Name}: inherited Harmony callbacks are not allowed ({string.Join(", ", inheritedCallbacks)})");
        }

        return callbacks
            .OrderBy(Describe, StringComparer.Ordinal)
            .ToArray();
    }

    private static CallbackSignature NormalizeCallback(MethodInfo method, Type annotationType)
    {
        return new CallbackSignature(
            method.Name,
            annotationType,
            method.IsStatic,
            method.ReturnType,
            method.GetParameters().Select(NormalizeParameter).ToArray());
    }

    private static CallbackParameterSignature NormalizeParameter(ParameterInfo parameter)
    {
        var modifier = CallbackParameterModifier.Value;
        if (parameter.ParameterType.IsByRef)
        {
            modifier = parameter.IsOut
                ? CallbackParameterModifier.Out
                : parameter.IsIn
                    ? CallbackParameterModifier.In
                    : CallbackParameterModifier.Ref;
        }

        var parameterType = parameter.ParameterType.IsByRef
            ? parameter.ParameterType.GetElementType()
            : parameter.ParameterType;
        return new CallbackParameterSignature(
            parameter.Name ?? string.Empty,
            parameterType ?? typeof(void),
            modifier);
    }

    private static Type[] GetHarmonyAnnotations(MethodInfo method)
    {
        return method.GetCustomAttributes(inherit: false)
            .Select(attribute => attribute.GetType())
            .Where(attributeType => typeof(Attribute).IsAssignableFrom(attributeType)
                && attributeType.Name.StartsWith("Harmony", StringComparison.Ordinal))
            .OrderBy(attributeType => attributeType.FullName, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool HasCallbackAnnotation(MethodInfo method)
    {
        return method.GetCustomAttributes(inherit: true)
            .Select(attribute => attribute.GetType())
            .Any(attributeType => typeof(Attribute).IsAssignableFrom(attributeType)
                && attributeType.Name.StartsWith("Harmony", StringComparison.Ordinal));
    }

    private static bool CallbackSetsEqual(
        CallbackSignature[] expected,
        CallbackSignature[] actual)
    {
        var orderedExpected = expected.OrderBy(Describe, StringComparer.Ordinal).ToArray();
        var orderedActual = actual.OrderBy(Describe, StringComparer.Ordinal).ToArray();
        if (orderedExpected.Length != orderedActual.Length)
        {
            return false;
        }

        for (var index = 0; index < orderedExpected.Length; index++)
        {
            if (!CallbackSignaturesEqual(orderedExpected[index], orderedActual[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool CallbackSignaturesEqual(
        CallbackSignature expected,
        CallbackSignature actual)
    {
        if (expected.MethodName != actual.MethodName
            || expected.AnnotationType != actual.AnnotationType
            || expected.IsStatic != actual.IsStatic
            || expected.ReturnType != actual.ReturnType
            || expected.Parameters.Length != actual.Parameters.Length)
        {
            return false;
        }

        for (var index = 0; index < expected.Parameters.Length; index++)
        {
            var expectedParameter = expected.Parameters[index];
            var actualParameter = actual.Parameters[index];
            if (expectedParameter.Name != actualParameter.Name
                || expectedParameter.ParameterType != actualParameter.ParameterType
                || expectedParameter.Modifier != actualParameter.Modifier)
            {
                return false;
            }
        }

        return true;
    }

    private static string Describe(CallbackSignature callback)
    {
        var parameters = string.Join(", ", callback.Parameters.Select(parameter =>
            $"{parameter.Modifier}:{parameter.ParameterType.FullName}:{parameter.Name}"));
        return $"{callback.MethodName}:{callback.AnnotationType.FullName}:{(callback.IsStatic ? "static" : "instance")}:{callback.ReturnType.FullName}({parameters})";
    }

    private static void Equal(string expected, string actual, string label)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{label} expected {expected}, actual {actual}");
        }
    }
}
