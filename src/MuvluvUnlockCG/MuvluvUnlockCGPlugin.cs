using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using MuvluvUnlockCG.Core;

namespace MuvluvUnlockCG;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class MuvluvUnlockCGPlugin : BasePlugin
{
    public const string PluginGuid = "com.pixelabyss.muvluv.unlockcg";
    public const string PluginName = "Muv-Luv Local Episode Playback";
    public const string PluginVersion = "1.2.0";

    private Harmony _harmony;
    private ConfigEntry<bool> _enabled;
    private ConfigEntry<string> _remoteBaseUrl;

    public override void Load()
    {
        // Gate loading on target resolution: a game update that moved a generated seam must abort
        // before configuration side effects or runtime initialization, so a partially patched
        // plugin cannot reach the runtime.
        var preflightFailures = HarmonyPatchDiagnostics.Precheck(Log);
        if (preflightFailures.Count > 0)
        {
            throw new PatchPreflightPolicy.PatchPreflightException(preflightFailures);
        }

        _enabled = Config.Bind(
            "General",
            "Enabled",
            true,
            "Enable Master-driven local story, Character, Memory, and event Episode playback. Changes apply without restarting the game.");
        _remoteBaseUrl = Config.Bind(
            "SceneFrames",
            "RemoteBaseUrl",
            SceneFrameSource.DefaultRemoteBaseUrl,
            "Absolute HTTPS base URL for the static G4 SceneFrame corpus. Clear it for local-only behavior.");
        var sceneDataRoot = Path.Combine(Paths.PluginPath, "MuvluvUnlockCG.SceneFrames");
        Log.LogInfo($"{MuvluvUnlockRuntime.DiagnosticPrefix} init enabled={_enabled.Value} sceneRootExists={Directory.Exists(sceneDataRoot)} remoteConfigured={SceneFrameSource.TryNormalizeRemoteBaseUrl(_remoteBaseUrl.Value, out _)}");
        MuvluvUnlockRuntime.Initialize(() => _enabled.Value, Log, sceneDataRoot, _remoteBaseUrl.Value);
        _harmony = new Harmony(PluginGuid);

        try
        {
            _harmony.PatchAll(typeof(MuvluvUnlockCGPlugin).Assembly);
            var allPatchesInstalled = HarmonyPatchDiagnostics.LogInstall(_harmony, Log);
            Log.LogInfo($"{MuvluvUnlockRuntime.DiagnosticPrefix} patch-all completed installed={allPatchesInstalled}");
            Log.LogInfo($"{MuvluvUnlockRuntime.DiagnosticPrefix} local SceneFrame directory exists={Directory.Exists(sceneDataRoot)}");
            if (!allPatchesInstalled)
            {
                throw new InvalidOperationException(
                    "one or more Harmony targets are not owned by this plugin after install");
            }
        }
        catch (Exception exception)
        {
            MuvluvUnlockRuntime.ClearAll("patch-install-failure");
            try
            {
                _harmony.UnpatchSelf();
            }
            catch (Exception unpatchException)
            {
                Log.LogError($"{MuvluvUnlockRuntime.DiagnosticPrefix} patch-install cleanup failed; exception={unpatchException.GetType().FullName}; stack={unpatchException.StackTrace}");
            }

            Log.LogError($"{MuvluvUnlockRuntime.DiagnosticPrefix} local episode hooks failed to load; loading aborted before any patch was installed. exception={exception.GetType().FullName}; stack={exception.StackTrace}");
        }
    }

    public override bool Unload()
    {
        Log.LogInfo($"{MuvluvUnlockRuntime.DiagnosticPrefix} unload requested enabled={_enabled?.Value ?? false}");
        // Keep the Harmony hooks installed when a native Viewable setter could
        // not be restored. CellVisibilityRegistry retains the strong wrapper
        // lease for a retry; d60:418-421/d61:268-271 consume that live native
        // bit, so unpatching here would strand it.
        var unloaded = UnloadLifecyclePolicy.TryUnload(
            () => MuvluvUnlockRuntime.ClearAll("unload"),
            () => _harmony?.UnpatchSelf());
        if (!unloaded)
        {
            Log.LogWarning($"{MuvluvUnlockRuntime.DiagnosticPrefix} unload deferred; a cell visibility lease is still active.");
        }
        else
        {
            Log.LogInfo($"{MuvluvUnlockRuntime.DiagnosticPrefix} unload completed");
        }

        return unloaded;
    }
}
