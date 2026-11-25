using System;
using System.IO;
using System.Linq;
using System.Reflection;
using DG.Tweening;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Api;
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
    private string appliedMotionSprite;
    private CustomerMotion appliedMotionPhase;

#if PROBABLY_COOP_GRID_QA
    internal void QaPublishPortrait(string key)
    {
        RecordCustomerMotion(CustomerMotion.AtCounter);
        motionSprite = key;
    }
#endif

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

    internal void RecordDialogueCustomerHidden()
    {
        if (customerMotion == CustomerMotion.AtCounter) RecordCustomerMotion(CustomerMotion.Hidden);
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
        bool customerChanged = appliedMotionCustomer != customer;
        bool completingApproach = !customerChanged && appliedMotionPhase == CustomerMotion.Approaching;
        bool recoveringApproach = !restoring && customer != 0 &&
            (customerChanged || appliedMotionPhase is CustomerMotion.Hidden or CustomerMotion.Leaving or CustomerMotion.LeavingRight) &&
            MapUIManager.Instance?.IsOutside == false;
        bool completingDeparture = appliedMotionPhase is CustomerMotion.Leaving or CustomerMotion.LeavingRight;
        appliedMotion = sequence;
        appliedMotionCustomer = customer;
        appliedMotionPhase = phase;
        if (phase == CustomerMotion.Hidden)
        {
            appliedMotionSprite = null;
            // The local path owns its final visual frame and OnLeft callback.
            // A slightly earlier host snapshot must not truncate that animation.
            if (restoring || !completingDeparture || !actor.IsMoving()) StoreUIManager.Instance.HideClient();
            return;
        }
        string sprite = (string)data["sprite"] ?? "";
        if (sprite.Length == 0) sprite = "default";
        bool newAppearance = restoring || customerChanged || appliedMotionSprite != sprite || actor.image.sprite == null;
        // Native lookup also lets local mods provide portraits without adding an
        // entry to the dictionary. Missing keys use the game's stock fallback.
        if (newAppearance)
        {
            actor.image.sprite = SpriteDict.Instance.ShowClientSprite(sprite);
            appliedMotionSprite = sprite;
        }
        StoreUIManager.Instance.DisplayClient();
        switch (phase)
        {
            case CustomerMotion.Approaching:
                actor.StartApprochingStore();
                SeekCustomerPath(actor.GetComponent<DOTweenPath>(), age, false, restoring);
                break;
            case CustomerMotion.AtCounter:
                // Intermediate snapshots may be coalesced while the guest is in
                // the shop. Start the local walk once even if the at-counter
                // state is the first cue. A join restores an established pose.
                if (completingApproach) break;
                if (recoveringApproach) actor.StartApprochingStore();
                else actor.SetImmediatlyArrive();
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
        if (newAppearance)
            ModHelper.SendMessage("probably_coop", "customer-appearance-v1", new CoopCustomerAppearance(sprite, restoring));
        Debug.Log($"[probably_coop] customer motion sequence={sequence} phase={phase} restore={restoring}");
    }

    private bool CustomerSpeakerApproaching() =>
        appliedMotionPhase is CustomerMotion.Approaching or CustomerMotion.AtCounter &&
        StoreClientMono.Instance is { } actor && actor != null &&
        actor.gameObject.activeInHierarchy && actor.IsMoving();

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
        if (NativeGuest.Active || NativeInventoryPilot.CampaignReplica)
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
        if (method is (nameof(StoreClientMono.StartLeavingStore) or nameof(StoreClientMono.StartLeavingStoreRight)) &&
            !__instance.gameObject.activeSelf)
            return true;
        var phase = method switch
        {
            nameof(StoreClientMono.StartApprochingStore) => NativeWorld.CustomerMotion.Approaching,
            nameof(StoreClientMono.StartLeavingStore) => NativeWorld.CustomerMotion.Leaving,
            nameof(StoreClientMono.StartLeavingStoreRight) => NativeWorld.CustomerMotion.LeavingRight,
            nameof(StoreClientMono.OnArrived) => NativeWorld.CustomerMotion.AtCounter,
            _ => NativeWorld.CustomerMotion.Hidden
        };
        NativeWorld.Host?.RecordCustomerMotion(phase);
        NativeInventoryPilot.RecordCustomerMotion(phase);
        return true;
    }
}

[HarmonyPatch(typeof(Dialogue), nameof(Dialogue.ExecuteEndAction))]
internal static class NativeDialogueCustomerVisibility
{
    private static void Prefix(Dialogue __instance, out NativeWorld __state)
    {
        __state = NativeWorld.Host;
        if (__state == null || __instance.isEndActionExecuted || __instance.endAction == null ||
            StoreClientMono.Instance is not { } actor || actor == null || !actor.gameObject.activeSelf) __state = null;
    }

    private static void Postfix(NativeWorld __state)
    {
        // Some native dialogue callbacks hide the visitor without a walk.
        if (__state != null && StoreClientMono.Instance is { } actor && actor != null && !actor.gameObject.activeSelf)
            __state.RecordDialogueCustomerHidden();
    }
}
