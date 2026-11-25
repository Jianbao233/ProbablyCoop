#if PROBABLY_COOP_ANDROID_HOST_PREVIEW
using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ProbablyCoop;

internal static class NativeAndroidCaptureHooks
{
    private static readonly MethodInfo Mutation = AccessTools.Method(typeof(NativeAndroidCaptureHooks), nameof(AfterMutation));
    private static MethodBase[] targets;
    private static int installed;
    private static GridShapeBuilder probe;
    private static bool positionObserved, fillObserved;
    internal static bool Ready { get; private set; }

    internal static void Install(Harmony harmony)
    {
        try
        {
            targets = NativeInventoryCaptureContract.ResolveMethods();
            var postfix = new HarmonyMethod(Mutation);
            foreach (var target in targets)
            {
                ++installed;
                harmony.Patch(target, postfix: postfix);
            }
        }
        catch (Exception error)
        {
            UseFullCapture(harmony, error);
        }
    }

    // Called once the port has activated the same patch batch as the actor
    // hooks. Harmony metadata alone does not prove that a wrapper executes.
    internal static void Verify(Harmony harmony)
    {
        if (targets == null) return;
        try
        {
            if (installed != NativeInventoryCaptureContract.Count || !targets.All(target => Harmony.GetPatchInfo(target)?.Postfixes.Any(
                patch => patch.owner == harmony.Id && patch.PatchMethod == Mutation) == true))
                throw new InvalidOperationException("Android inventory patch registration incomplete.");
            positionObserved = fillObserved = false;
            probe = new GridShapeBuilder(2, 2);
            probe.SetPosition(4, 7);
            probe.SetDataFill(3, 2, 1);
            if (!positionObserved || !fillObserved || probe.minX != 4 || probe.minY != 7 || probe.width != 3 || probe.height != 2)
                throw new InvalidOperationException("Android inventory patches failed the native shape probe.");
            Ready = true;
            Debug.Log($"[probably_coop] Android incremental inventory capture active ({installed} hooks)");
        }
        catch (Exception error)
        {
            UseFullCapture(harmony, error);
        }
        finally { probe = null; }
    }

    private static void AfterMutation(object __instance, MethodBase __originalMethod)
    {
        if (Ready)
        {
            NativeCaptureMutationHooks.Postfix(__instance, __originalMethod);
            return;
        }
        if (probe == null || !ReferenceEquals(probe, __instance)) return;
        if (__originalMethod.Name == "SetPosition") positionObserved = true;
        if (__originalMethod.Name == "SetDataFill") fillObserved = true;
    }

    private static void UseFullCapture(Harmony harmony, Exception error)
    {
        Uninstall(harmony);
        Debug.LogWarning("[probably_coop] Android inventory hooks unavailable; using full capture: " + error.Message);
    }

    internal static void Uninstall(Harmony harmony)
    {
        Ready = false;
        Exception failure = null;
        for (int i = installed - 1; i >= 0; --i)
        {
            try { harmony.Unpatch(targets[i], Mutation); }
            catch (Exception error) { failure ??= error; }
        }
        installed = 0;
        targets = null;
        if (failure != null) Debug.LogWarning("[probably_coop] Android inventory hook cleanup failed: " + failure.Message);
    }

}
#endif
