using System;
using System.IO;
using System.Linq;
using System.Reflection;
using DG.Tweening;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ProbablyCoop;

internal sealed partial class NativeWorld
{
    internal enum CustomerMotion { Hidden, Approaching, AtCounter, Leaving, LeavingRight }

    private CustomerMotion customerMotion;
    private ulong motionSequence;
    private double motionStarted, motionBaselineAge;
    private ulong motionBaselineSequence, motionBaselinePeer;
    private string motionSprite = "";
    private uint motionCustomer;
    private bool motionObserved;
    private ulong appliedMotion;
    private uint appliedMotionCustomer;
    private CustomerMotion appliedMotionPhase;

    // Called by the original host animation transitions, before the next capture.
    // Identity and start time stay stable even when two customers share a portrait.
    internal void RecordCustomerMotion(CustomerMotion phase)
    {
        if (closed || !host) return;
        customerMotion = phase;
        motionSequence++;
        motionStarted = Time.realtimeSinceStartupAsDouble;
        motionObserved = true;
        if (phase == CustomerMotion.Approaching || motionSequence == 1)
        {
            var blueprint = PlayerStore.Instance.currentClientInstance?.GetClientBlueprint();
            motionCustomer = model.Id(PlayerStore.Instance.currentClientInstance);
            var actual = StoreClientMono.Instance?.image.sprite;
            motionSprite = SpriteDict.Instance.spriteDictionary.FirstOrDefault(row => row.Value == actual).Key
                ?? blueprint?.spriteName ?? "";
        }
        nextCapture = 0;
    }

    private JObject CaptureCustomerMotion()
    {
        if (!motionObserved)
        {
            var store = PlayerStore.Instance;
            RecordCustomerMotion(store.isClientArrived && store.currentClientInstance != null
                ? CustomerMotion.AtCounter : CustomerMotion.Hidden);
        }
        // Seek only when a peer joins mid-path. Established peers play the
        // original animation from its cue, with no frame-by-frame age updates.
        if (motionBaselineSequence != motionSequence || motionBaselinePeer != peerGeneration)
        {
            motionBaselineSequence = motionSequence;
            motionBaselinePeer = peerGeneration;
            motionBaselineAge = customerMotion is CustomerMotion.Approaching or CustomerMotion.Leaving or CustomerMotion.LeavingRight
                ? Math.Max(0, Time.realtimeSinceStartupAsDouble - motionStarted) : 0;
        }
        return new JObject
        {
            ["sequence"] = motionSequence,
            ["customer"] = motionCustomer,
            ["phase"] = (int)customerMotion,
            ["sprite"] = motionSprite,
            ["age"] = motionBaselineAge
        };
    }

    private void ApplyCustomerMotion(JObject data)
    {
        if (data == null) throw new InvalidDataException("Missing customer presentation state");
        var sequence = (ulong)data["sequence"];
        var phase = (CustomerMotion)(int)data["phase"];
        double age = (double)data["age"];
        if (sequence == 0 || !Enum.IsDefined(typeof(CustomerMotion), phase) ||
            double.IsNaN(age) || double.IsInfinity(age) || age < 0)
            throw new InvalidDataException("Invalid customer presentation state");
        if (sequence <= appliedMotion) return;
        var actor = StoreClientMono.Instance;
        if (actor == null) return;
        bool restoring = appliedMotion == 0;
        uint customer = (uint)data["customer"];
        bool completingApproach = appliedMotionCustomer == customer && appliedMotionPhase == CustomerMotion.Approaching;
        bool completingDeparture = appliedMotionPhase is CustomerMotion.Leaving or CustomerMotion.LeavingRight;
        appliedMotion = sequence;
        appliedMotionCustomer = customer;
        appliedMotionPhase = phase;
        if (phase == CustomerMotion.Hidden)
        {
            // The local path owns its final visual frame and OnLeft callback.
            // A slightly earlier host snapshot must not truncate that animation.
            if (restoring || !completingDeparture || !actor.IsMoving()) StoreUIManager.Instance.HideClient();
            return;
        }
        string sprite = (string)data["sprite"];
        if (!SpriteDict.Instance.spriteDictionary.TryGetValue(sprite, out var portrait))
            throw new InvalidDataException("Missing customer portrait");
        actor.image.sprite = portrait;
        StoreUIManager.Instance.DisplayClient();
        switch (phase)
        {
            case CustomerMotion.Approaching:
                actor.StartApprochingStore();
                SeekCustomerPath(actor.GetComponent<DOTweenPath>(), age, false, restoring);
                break;
            case CustomerMotion.AtCounter:
                // A normal arrival completes its local walk. A reconnect has no
                // earlier animation to finish and can restore the current endpoint.
                if (restoring || !completingApproach) actor.SetImmediatlyArrive();
                break;
            case CustomerMotion.Leaving:
                actor.StartLeavingStore();
                SeekCustomerPath(actor.GetComponent<DOTweenPath>(), age, true, restoring);
                break;
            case CustomerMotion.LeavingRight:
                actor.StartLeavingStoreRight();
                SeekCustomerPath(actor.pathRight, age, false, restoring);
                break;
        }
        Debug.Log($"[probably_coop] customer motion sequence={sequence} phase={phase} restore={restoring}");
    }

    private static void SeekCustomerPath(DOTweenPath path, double age, bool backwards, bool restoring)
    {
        if (!restoring || path?.tween == null) return;
        float duration = path.tween.Duration(false);
        float elapsed = Mathf.Min((float)age, duration);
        path.tween.Goto(backwards ? duration - elapsed : elapsed, false);
        if (elapsed >= duration)
        {
            if (backwards || path == StoreClientMono.Instance.pathRight)
                StoreUIManager.Instance.HideClient();
            else StoreClientMono.Instance.SetImmediatlyArrive();
        }
        else if (backwards) path.DOPlayBackwards();
        else path.DOPlay();
    }
}

[HarmonyPatch]
internal static class NativeCustomerTransitions
{
    private static readonly FieldInfo Walking = AccessTools.Field(typeof(StoreClientMono), "walkingIn");
    private static readonly FieldInfo IgnoreLeft = AccessTools.Field(typeof(StoreClientMono), "ignoreOnLeftFlag");

    private static MethodBase[] TargetMethods() => new MethodBase[]
    {
        AccessTools.Method(typeof(StoreClientMono), nameof(StoreClientMono.StartApprochingStore)),
        AccessTools.Method(typeof(StoreClientMono), nameof(StoreClientMono.StartLeavingStore)),
        AccessTools.Method(typeof(StoreClientMono), nameof(StoreClientMono.StartLeavingStoreRight)),
        AccessTools.Method(typeof(StoreClientMono), nameof(StoreClientMono.OnArrived)),
        AccessTools.Method(typeof(StoreClientMono), nameof(StoreClientMono.OnLeft))
    };

    private static bool Prefix(StoreClientMono __instance, MethodBase __originalMethod)
    {
        string method = __originalMethod.Name;
        if (NativeGuest.Active)
        {
            if (method == nameof(StoreClientMono.OnArrived))
                Walking.SetValue(__instance, false);
            if (method == nameof(StoreClientMono.OnLeft))
            {
                if (!(bool)IgnoreLeft.GetValue(__instance)) StoreUIManager.Instance.HideClient();
                return false;
            }
            return true;
        }
        if (method == nameof(StoreClientMono.OnLeft) && (bool)IgnoreLeft.GetValue(__instance)) return true;
        if (method == nameof(StoreClientMono.OnArrived) && !(bool)Walking.GetValue(__instance)) return true;
        var phase = method switch
        {
            nameof(StoreClientMono.StartApprochingStore) => NativeWorld.CustomerMotion.Approaching,
            nameof(StoreClientMono.StartLeavingStore) => NativeWorld.CustomerMotion.Leaving,
            nameof(StoreClientMono.StartLeavingStoreRight) => NativeWorld.CustomerMotion.LeavingRight,
            nameof(StoreClientMono.OnArrived) => NativeWorld.CustomerMotion.AtCounter,
            _ => NativeWorld.CustomerMotion.Hidden
        };
        NativeWorld.Host?.RecordCustomerMotion(phase);
        return true;
    }
}
