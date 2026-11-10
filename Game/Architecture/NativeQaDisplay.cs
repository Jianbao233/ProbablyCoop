using System;
using System.Collections;
using HarmonyLib;
using UnityEngine;

namespace ProbablyCoop;

/// <summary>Only disposable batch QA players. Their command-line render size is
/// fixed; never let saved fullscreen preferences or focus recovery create a window.
/// Production/local multiplayer settings and ordinary single-player are untouched.</summary>
internal static class NativeQaDisplay
{
    internal static bool IsIsolatedQa
    {
        get
        {
            if (!NativeBackgroundQa.IsHostProduct &&
                !NativeBackgroundQa.IsGuestProduct) return false;
            var args = Environment.GetCommandLineArgs();
            return (Application.isBatchMode || Array.IndexOf(args,"--probably-coop-qa-windowed")>=0) &&
                Array.IndexOf(args, "--probably-coop-local") >= 0 &&
                (Array.IndexOf(args, "--probably-coop-qa") >= 0 || Array.IndexOf(args, "--probably-coop-architecture-qa") >= 0);
        }
    }
    internal static IEnumerator Empty() { yield break; }
}

[HarmonyPatch(typeof(EmporiumOption), nameof(EmporiumOption.ApplySetting))]
internal static class QaKeepCommandLineResolution
{
    private static void Prefix(ref bool applyResolution)
    {
        if (NativeQaDisplay.IsIsolatedQa) applyResolution = false;
    }
}
[HarmonyPatch(typeof(EmporiumOption), "RecoverDisplayAfterFocus")]
internal static class QaNoDisplayRecovery
{
    private static bool Prefix(ref IEnumerator __result)
    {
        if (!NativeQaDisplay.IsIsolatedQa) return true;
        __result = NativeQaDisplay.Empty(); return false;
    }
}
[HarmonyPatch(typeof(EmporiumOption), "WaitForResolutionThenAdjust")]
internal static class QaNoDisplayResolutionRetry
{
    private static bool Prefix(ref IEnumerator __result)
    {
        if (!NativeQaDisplay.IsIsolatedQa) return true;
        __result = NativeQaDisplay.Empty(); return false;
    }
}
