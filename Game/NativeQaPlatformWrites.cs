using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using ProbablyCoop.Core;
using UnityEngine;

namespace ProbablyCoop;

/// <summary>Disposable save profiles must not modify the real Steam account.</summary>
[HarmonyPatch]
internal static class NativeQaPlatformWrites
{
    internal static int Blocked { get; private set; }
    private static readonly MethodInfo Unlock = AccessTools.Method(typeof(AchievementTracker), "Unlock");
    private static readonly MethodInfo Increment = AccessTools.Method(typeof(AchievementTracker), "IncrementCountAndUnlock");

    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return Unlock;
        yield return Increment;
    }

    private static bool Prefix()
    {
        if (!DisposableProduct.BlocksAccountWrites(Application.productName)) return true;
        Blocked++;
        return false;
    }

    internal static void Verify()
    {
        if (!DisposableProduct.BlocksAccountWrites(Application.productName) || AchievementTracker.Instance == null)
            throw new InvalidOperationException("Account-write isolation probe requires a disposable player");
        var ready = AccessTools.Field(typeof(AchievementTracker), "statsReady");
        bool previous = (bool)ready.GetValue(AchievementTracker.Instance);
        int before = Blocked;
        try
        {
            // Independent fail-safe: even if the prefixes were not installed,
            // both original methods exit before any Steam APIs with statsReady=false.
            ready.SetValue(AchievementTracker.Instance, false);
            Unlock.Invoke(AchievementTracker.Instance, new object[] { "PROBABLY_COOP_QA_NO_EFFECT" });
            Increment.Invoke(AchievementTracker.Instance, new object[] { "PROBABLY_COOP_QA_NO_EFFECT", 1, "PROBABLY_COOP_QA_NO_EFFECT" });
            if (Blocked != before + 2) throw new InvalidOperationException("QA account-write prefixes are not installed");
        }
        finally { ready.SetValue(AchievementTracker.Instance, previous); }
    }
}
