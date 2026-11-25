#if PROBABLY_COOP_ANDROID_HOST_PREVIEW
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
#endif
using UnityEngine;

namespace ProbablyCoop;

internal static class NativePlatform
{
    internal static bool IsAndroid => Application.platform == RuntimePlatform.Android;

    internal static bool SupportsHosting => !IsAndroid || AndroidActorRoutingReady;

    internal static bool SupportsAuthorityContract => !IsAndroid || IsAuditedAndroidRuntime;

    internal static bool IsAuditedAndroidRuntime
    {
        get
        {
#if PROBABLY_COOP_ANDROID_HOST_PREVIEW
            return IsAndroid && typeof(PlayerStore).Module.ModuleVersionId == AndroidRev13Mvid;
#else
            return false;
#endif
        }
    }

    internal static bool MatchesActorModule(System.Reflection.Module module, string expectedMvid) =>
        module.ModuleVersionId == new System.Guid(expectedMvid) ||
        (IsAuditedAndroidRuntime && module == typeof(PlayerStore).Module);

    internal static bool AndroidActorRoutingReady
    {
        get
        {
#if PROBABLY_COOP_ANDROID_HOST_PREVIEW
            return actorHooksReady;
#else
            return false;
#endif
        }
    }
    internal static bool AndroidCaptureReady
    {
        get
        {
#if PROBABLY_COOP_ANDROID_HOST_PREVIEW
            return NativeAndroidCaptureHooks.Ready;
#else
            return false;
#endif
        }
    }

#if PROBABLY_COOP_ANDROID_HOST_PREVIEW
    private static readonly Guid AndroidRev13Mvid = new Guid("c527db7b-7747-9024-2c5d-bd301285a1ae");
    private static readonly MethodInfo Sentinel = AccessTools.Method(typeof(PlayerStore), nameof(PlayerStore.IsInstanceExist));
    private static readonly MethodInfo AfterSentinel = AccessTools.Method(typeof(NativePlatform), nameof(ObserveSentinel));
    private static Harmony authority;
    private static int requestedFrame;
    private static bool actorHooksReady, activationFailed, sentinelObserved;
    private static bool fieldsPrepared, carryPrepared, nightPrepared, sentinelPrepared;

    internal static void VerifyAuditedAndroidOperand(byte[] body, int offset, int token, bool write, string signature)
    {
        byte opcode = write ? (byte)0x7d : (byte)0x7b;
        if (body == null || offset < 0 || offset > body.Length - 5 || body[offset] != opcode ||
            BitConverter.ToInt32(body, offset + 1) != token)
            throw new InvalidDataException("Android actor operand changed: " + signature + " IL_" + offset.ToString("X4"));
    }

    // The Android port installs generated Harmony wrappers at its end-of-frame
    // batch boundary. Prepare them in the menu, before any cooperative store
    // can read or write a player's state.
    internal static bool PrepareAndroidHosting()
    {
        if (!IsAuditedAndroidRuntime || actorHooksReady || activationFailed) return false;
        try
        {
            if (authority == null)
            {
                authority = ModLoader.Mods.Single(mod => mod.Assembly == typeof(NativePlatform).Assembly &&
                    mod.Manifest?.ID == "probably_coop").HarmonyInstance
                    ?? throw new InvalidOperationException("Missing framework Harmony.");
                fieldsPrepared = true;
                NativeActorFieldAccess.Install(authority, store => NativeActorSession.Current?.Run.Resolve(store));
                carryPrepared = true;
                NativeActorCarryAccess.Install(authority, entry =>
                {
                    var carry = NativeActorSession.Current?.Carry;
                    return ReferenceEquals(entry, carry?.Source) ? carry : null;
                });
                nightPrepared = true;
                NativeActorNight.Install(authority);
                if (!NativeActorCarryQueries.HasPatchedContract())
                    throw new InvalidOperationException("Android actor backpack query patches did not install.");
                if (!NativeWorld.ForceFullCaptureCadence && !NativeWorld.HasEnabledCaptureWriters())
                    NativeAndroidCaptureHooks.Install(authority);
                sentinelPrepared = true;
                authority.Patch(Sentinel, postfix: new HarmonyMethod(AfterSentinel));
                requestedFrame = Time.frameCount;
            }
            if (Time.frameCount == requestedFrame) return true;
            sentinelObserved = false;
            Sentinel.Invoke(null, null);
            if (!sentinelObserved)
            {
                if (Time.frameCount - requestedFrame < 10) return true;
                throw new InvalidOperationException("The Android patch batch did not activate.");
            }
            authority.Unpatch(Sentinel, AfterSentinel);
            sentinelPrepared = false;
            NativeAndroidCaptureHooks.Verify(authority);
            actorHooksReady = true;
            Debug.Log("[probably_coop] Android actor hooks preloaded; IPv4 hosting available");
        }
        catch (Exception error)
        {
            activationFailed = true;
            ReleaseAndroidHosting();
            Debug.LogWarning("[probably_coop] Android actor hooks unavailable: " + error);
        }
        return false;
    }

    private static void ObserveSentinel() => sentinelObserved = true;

    internal static void ResetAndroidHosting()
    {
        ReleaseAndroidHosting();
        activationFailed = false;
    }

    private static void ReleaseAndroidHosting()
    {
        actorHooksReady = false;
        if (authority == null) return;
        NativeAndroidCaptureHooks.Uninstall(authority);
        if (sentinelPrepared) ReleaseHook(() => authority.Unpatch(Sentinel, AfterSentinel));
        if (nightPrepared) ReleaseHook(() => NativeActorNight.Uninstall(authority));
        if (carryPrepared) ReleaseHook(() => NativeActorCarryAccess.Uninstall(authority));
        if (fieldsPrepared) ReleaseHook(() => NativeActorFieldAccess.Uninstall(authority));
        sentinelPrepared = nightPrepared = carryPrepared = fieldsPrepared = false;
        authority = null;
    }

    private static void ReleaseHook(Action release)
    {
        try { release(); }
        catch (Exception error)
        {
            Debug.LogWarning("[probably_coop] Android hook cleanup failed: " + error);
        }
    }
#endif
}
