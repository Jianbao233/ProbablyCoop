using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using ProbablyCoop.Sync;
using UnityEngine;

namespace ProbablyCoop;

internal sealed partial class NativeInventoryPilot
{
    private sealed class ActionCapture
    {
        internal readonly NativeInventoryPilot Pilot;
        internal readonly InventoryAction Action;
        internal readonly HashSet<GameItem> Touched = new HashSet<GameItem>();

        internal ActionCapture(NativeInventoryPilot pilot, InventoryAction action)
        {
            Pilot = pilot;
            Action = action;
        }
    }

    private ActionCapture capture;
    private bool inTargetGesture;
    private ActionCapture targetGesture;
    private Action deferredNativeTarget;
    private GameItem pendingScratchStart;
    private double nextScratchStart;
    private readonly HashSet<(string operation, string catalog)> uncapturedWrites = new HashSet<(string, string)>();

    private static NativeInventoryPilot EnterTargetGesture()
    {
        var pilot = Current;
        if (pilot == null || pilot.inTargetGesture) return null;
        pilot.inTargetGesture = true;
        return pilot;
    }

    private void ExitTargetGesture(bool completed)
    {
        var action = targetGesture;
        var deferred = deferredNativeTarget;
        targetGesture = null;
        deferredNativeTarget = null;
        inTargetGesture = false;
        if (Finish(action, completed)) deferred?.Invoke();
    }

    private void AfterTargetGesture(Action action)
    {
        // Original drag/equipped use dispatch both target callbacks. Send
        // one request after the native gesture has finished its own cleanup.
        if (inTargetGesture) deferredNativeTarget ??= action;
        else action();
    }

    private void Touch(GameItem item, bool contents = false)
    {
        if (item == null || capture == null) return;
        capture.Touched.Add(item);
        if (contents)
            foreach (var child in GraphUtils.FindAllChildrenType<GameItem>(item))
                if (world.Tracks(child)) capture.Touched.Add(child);
        for (var parent = item.parentInventory?.GetParentItem(); parent != null && capture.Touched.Add(parent);
             parent = parent.parentInventory?.GetParentItem()) { }
    }

    private bool BeginTransfer(GameItem item, GameItem target, GameInventory inventory, GridShape shape, int amount,
        out ActionCapture started)
    {
        started = null;
        if (!world.Ready || world.Applying || RunningNativeBoundary) return true;
        if (target == null && item != null && item.parentInventory == null && !world.Tracks(item) &&
            IsLocalResponsePlacement(item, inventory)) return true;
        if (item != null && target == null && inventory is GameGridScrollableInventory { alwaysStack: true } scroll &&
            item.parentInventory != scroll)
            target = scroll.childItems.FirstOrDefault(other => other != item && item.MayStack(other));
        if (target != null)
        {
            inventory = target.parentInventory;
            shape = target.modifiedShape;
        }
        if (!world.IsBound(inventory) || item == null || amount <= 0) return false;
        if (capture != null)
        {
            Touch(item);
            Touch(target);
            Touch(inventory.GetParentItem());
            return true;
        }
        if (!CanMove(item) || target != null && !CanMove(target) ||
            !world.TryPlacement(inventory, shape, out var placement) || !world.CanDestination(item, placement)) return false;
        capture = started = new ActionCapture(this,
            new InventoryAction(item.uniqueId, target?.uniqueId ?? 0, amount, placement));
        Touch(item);
        Touch(target);
        // Moving into a large cabinet can validate its entire subtree. Only
        // submit the slot owners and the items this action actually touched.
        // Nested inventory mutations add their own objects to the capture.
        Touch(inventory.GetParentItem());
        return true;
    }

    private static bool IsLocalResponsePlacement(GameItem item, GameInventory destination)
    {
        var shop = EmporiumEntry.Instance;
        // The original response window represents choices as temporary
        // items. Only its freshly built entries may bypass shared placement.
        return destination != null && shop != null &&
            (destination == shop.responseInventory || destination == shop.responseInventoryClosable) &&
            shop.choices?.Any(choice => ReferenceEquals(choice.Item1, item)) == true;
    }

    private bool BeginTarget(GameItem source, GameItem target, out ActionCapture started)
    {
        started = null;
        if (!world.Ready || world.Applying || RunningNativeBoundary) return true;
        if (capture != null)
        {
            Touch(source, contents: true);
            Touch(target, contents: true);
            return true;
        }
        var shop = EmporiumEntry.Instance;
        if (campaign != null && shop != null && target != null &&
            (ReferenceEquals(target, shop.faucet) || ReferenceEquals(target, shop.drain)))
        {
            if (CanMove(source) && source.IsTag("LIQUID_CONTAINER_TAG") && source.CanTarget(target))
                AfterTargetGesture(() => campaign.RequestItemUse(source,
                    ReferenceEquals(target, shop.faucet) ? ItemUse.FillAtTap : ItemUse.EmptyAtDrain));
            return false;
        }
        if (!CanMove(source) || !CanMove(target) || ReferenceEquals(source, target) ||
            !source.MayTarget(target) || !source.CanTarget(target)) return false;
        if (campaign != null && target.IsTag(MachineAutoButcher.AUTO_BUTCHER_TAG))
        {
            AfterTargetGesture(() => campaign.RequestProductionTarget(source, target));
            return false;
        }
        capture = started = new ActionCapture(this, new InventoryAction(source.uniqueId, target.uniqueId));
        Touch(source, contents: true);
        Touch(target, contents: true);
        // Native drag and equipped use each call both target callbacks before
        // finishing the same gesture. A rejection must undo their joint result.
        if (inTargetGesture)
        {
            targetGesture = started;
            started = null;
        }
        return true;
    }

    private bool Finish(ActionCapture action, bool completed)
    {
        if (action == null || capture != action) return completed;
        capture = null;
        bool enrolled = true;
        foreach (GameItem item in action.Touched)
        {
            if (world.Tracks(item) || !world.IsBound(item.parentInventory)) continue;
            if (world.TrackSpawn(item)) continue;
            // The object was inserted by this action into an enrolled inventory.
            // It has no portable binding, so remove the result before restoring.
            if (!NativeInventoryMembership.Remove(item.parentInventory, item)) RequestExit();
            item.Destroy();
            enrolled = false;
        }
        if (!completed || !enrolled || !world.TryObserve(action.Touched, action.Action, out var changes))
        {
            if (!world.RestoreVisible()) RequestExit();
            return false;
        }
        if (changes.Length == 0) return true;
        var ops = changes.Select(change => change.ToOp()).ToList();
        var description = localHost ? action.Action : world.DescribeMove(action.Action, changes);
        byte[] context = description.Encode();
        if (SyncCodec.Fits(ops, context))
        {
            if (localHost)
            {
                if (world.RecordLocal(changes))
                {
                    session.Host.Local(ops);
                    campaign?.CommerceChanged();
                    return true;
                }
            }
            else if (session.Guest.Local(ops, out ulong serial, context: context))
            {
                if (world.RecordLocal(changes, serial, description.UnchangedSource.HasValue ? description.Source : 0)) return true;
                RequestExit();
            }
        }
        if (!world.RestoreVisible()) RequestExit();
        return false;
    }

    private static Exception Abort(Exception exception, ActionCapture action)
    {
        if (exception != null && action != null && action.Pilot.capture == action) action.Pilot.Finish(action, false);
        return exception;
    }

    private static bool RouteScratch(GameItem ticket, ItemUse use, int slot = 0)
    {
        var pilot = Current;
        if (pilot?.campaign == null || pilot.RunningNativeBoundary || !pilot.world.Tracks(ticket)) return true;
        if (use == ItemUse.StartScratch)
        {
            if (!ticket.IsTag(ScratcherHelper.SCRATCHER_NEW_TAG)) return false;
            double now = Time.realtimeSinceStartupAsDouble;
            if (!pilot.localHost && ticket == pilot.pendingScratchStart && now < pilot.nextScratchStart) return false;
            pilot.pendingScratchStart = ticket;
            pilot.nextScratchStart = now + 0.75;
        }
        bool sent = pilot.CanMove(ticket) && pilot.campaign.RequestItemUse(ticket, use, (byte)slot);
        if (!sent && use == ItemUse.RevealSymbol && slot is >= 1 and <= 3 && !NativeEffectCampaign.IsRevealed(ticket, slot))
            ScratcherUIManager.Instance.scratchInputs[slot - 1].ResetScratch();
        return false;
    }

    [HarmonyPatch(typeof(ScratcherHelper), nameof(ScratcherHelper.TryRerollForBetterLuck))]
    private static class CampaignScratchOpenPatch
    {
        private static bool Prepare() => RoomRuntime.Selected;
        private static bool Prefix(GameItem gameItem) => RouteScratch(gameItem, ItemUse.OpenScratcher);
    }

    [HarmonyPatch(typeof(ScratchInput), nameof(ScratchInput.OnAnyScratch))]
    private static class CampaignScratchStartPatch
    {
        private static bool Prepare() => RoomRuntime.Selected;
        private static bool Prefix() => RouteScratch(ScratcherUIManager.Instance?.currentActiveScratcherItem, ItemUse.StartScratch);
    }

    [HarmonyPatch(typeof(ScratcherUIManager), nameof(ScratcherUIManager.OnScratchComplete))]
    private static class CampaignScratchRevealPatch
    {
        private static bool Prepare() => RoomRuntime.Selected;
        private static bool Prefix(ScratcherUIManager __instance, int slot) =>
            RouteScratch(__instance.currentActiveScratcherItem, ItemUse.RevealSymbol, slot);
    }

    [HarmonyPatch(typeof(PlayerStore), nameof(PlayerStore.ModCash))]
    private static class CampaignCashPatch
    {
        private static bool Prepare() => RoomRuntime.Selected;
        private static bool Prefix() => Current?.campaign == null || Current.localHost;
    }

    [HarmonyPatch(typeof(SlotMarker), nameof(SlotMarker.TryAcceptOnce))]
    private static class DropPatch
    {
        private static bool Prepare() => RoomRuntime.Selected;

        private static bool Prefix(SlotMarker __instance, int amount, ref int __result, out ActionCapture __state)
        {
            __state = null;
            var pilot = Current;
            if (pilot?.world.Ready != true || pilot.world.Applying) return true;
            int count = amount < 0 ? __instance.numTransfer : Math.Min(amount, __instance.numTransfer);
            count = Math.Min(count, __instance.item?.MaxNumRemove() ?? 0);
            if (pilot.BeginTransfer(__instance.item, __instance.targetItem, __instance.inventory,
                __instance.itemGridShape, count, out __state)) return true;
            __result = 0;
            return false;
        }

        private static void Postfix(SlotMarker __instance, ref int __result, ActionCapture __state)
        {
            if (__state == null) return;
            __state.Pilot.Touch(__instance.item);
            if (!__state.Pilot.Finish(__state, __result > 0)) __result = 0;
        }

        private static Exception Finalizer(Exception __exception, ActionCapture __state) => Abort(__exception, __state);
    }

    [HarmonyPatch(typeof(SlotMarker), nameof(SlotMarker.AcceptUnchecked))]
    private static class UncheckedDropPatch
    {
        private static bool Prepare() => RoomRuntime.Selected;

        private static bool Prefix(SlotMarker __instance, ref int __result, out ActionCapture __state)
        {
            __state = null;
            var pilot = Current;
            if (pilot?.world.Ready != true || pilot.world.Applying) return true;
            if (pilot.BeginTransfer(__instance.item, null, __instance.inventory, __instance.itemGridShape,
                __instance.item?.unitCount ?? 0, out __state)) return true;
            __result = 0;
            return false;
        }

        private static void Postfix(ref int __result, ActionCapture __state)
        {
            if (__state != null && !__state.Pilot.Finish(__state, true)) __result = 0;
        }

        private static Exception Finalizer(Exception __exception, ActionCapture __state) => Abort(__exception, __state);
    }

    [HarmonyPatch(typeof(SlotMarker), nameof(SlotMarker.StackItemUnchecked))]
    private static class StackPatch
    {
        private static bool Prepare() => RoomRuntime.Selected;

        private static bool Prefix(GameItem item, GameItem targetItem, int amount, out ActionCapture __state)
        {
            __state = null;
            var pilot = Current;
            if (pilot?.world.Ready != true || pilot.world.Applying) return true;
            int count = amount == -1 ? item?.unitCount ?? 0 : amount;
            return pilot.BeginTransfer(item, targetItem, targetItem?.parentInventory, targetItem?.modifiedShape, count, out __state);
        }

        private static void Postfix(ActionCapture __state) => __state?.Pilot.Finish(__state, true);
        private static Exception Finalizer(Exception __exception, ActionCapture __state) => Abort(__exception, __state);
    }

    [HarmonyPatch(typeof(ItemBehaviourManager), nameof(ItemBehaviourManager.Target))]
    private static class TargetManagerPatch
    {
        private static bool Prepare() => RoomRuntime.Selected;

        private static bool Prefix(GameItem source, GameItem target, out ActionCapture __state)
        {
            __state = null;
            return Current == null || Current.BeginTarget(source, target, out __state);
        }

        private static void Postfix(ActionCapture __state) => __state?.Pilot.Finish(__state, true);
        private static Exception Finalizer(Exception __exception, ActionCapture __state) => Abort(__exception, __state);
    }

    [HarmonyPatch(typeof(GameItem), nameof(GameItem.Target))]
    private static class TargetItemPatch
    {
        private static bool Prepare() => RoomRuntime.Selected;

        private static bool Prefix(GameItem __instance, GameItem targetItem, out ActionCapture __state)
        {
            __state = null;
            return Current == null || Current.BeginTarget(__instance, targetItem, out __state);
        }

        private static void Postfix(ActionCapture __state) => __state?.Pilot.Finish(__state, true);
        private static Exception Finalizer(Exception __exception, ActionCapture __state) => Abort(__exception, __state);
    }

    [HarmonyPatch(typeof(ItemSelectHandler), "OnEventPress")]
    private static class EquippedTargetGesturePatch
    {
        private static bool Prepare() => RoomRuntime.Selected;
        private static void Prefix(out NativeInventoryPilot __state) => __state = EnterTargetGesture();
        private static Exception Finalizer(Exception __exception, NativeInventoryPilot __state)
        {
            __state?.ExitTargetGesture(__exception == null);
            return __exception;
        }
    }

    [HarmonyPatch]
    private static class InventoryMutationPatch
    {
        private static bool Prepare() => RoomRuntime.Selected;
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.DeclaredMethod(typeof(GameGridInventory), nameof(GameGridInventory.UncheckedAccept));
            yield return AccessTools.DeclaredMethod(typeof(GameGridInventory), nameof(GameGridInventory.Expel));
            yield return AccessTools.DeclaredMethod(typeof(GameGridScrollableInventory), nameof(GameGridScrollableInventory.UncheckedAccept));
            yield return AccessTools.DeclaredMethod(typeof(GameGridScrollableInventory), nameof(GameGridScrollableInventory.Expel));
            yield return AccessTools.DeclaredMethod(typeof(GameSlotInventory), nameof(GameSlotInventory.UncheckedAccept));
            yield return AccessTools.DeclaredMethod(typeof(GameSlotInventory), nameof(GameSlotInventory.Expel));
        }

        private static bool Prefix(GameInventory __instance, GameItem item, MethodBase __originalMethod)
        {
            var pilot = Current;
            if (pilot == null || pilot.localHost || pilot.capture != null || !pilot.world.Ready ||
                pilot.world.Applying || !pilot.world.IsBound(__instance)) return true;
            pilot.ReportUncapturedWrite(__originalMethod.Name, item);
            return false;
        }

        private static void Postfix(GameInventory __instance, GameItem item, bool __result)
        {
            var pilot = Current;
            if (__result && pilot?.capture != null && !pilot.world.Applying)
            {
                pilot.Touch(item);
                pilot.Touch(__instance.GetParentItem());
            }
        }
    }

    [HarmonyPatch(typeof(GameItemElement), nameof(GameItemElement.Destroy))]
    private static class PreservePendingItemPatch
    {
        private static bool Prepare() => RoomRuntime.Selected;

        private static bool Prefix(GameItemElement __instance)
        {
            var pilot = Current;
            if (pilot == null || pilot.world.Applying || pilot.RunningNativeBoundary || !pilot.world.Retains(__instance))
                return true;
            if (pilot.capture != null)
            {
                pilot.Touch(__instance);
                if (__instance.parentInventory is GameInventory inventory && !inventory.Expel(__instance))
                    throw new InvalidOperationException("The native action could not release a consumed item.");
                return false;
            }
            // A replica may still need this item for an incoming effect or a
            // rollback. Uncaptured client callbacks cannot retire it locally.
            if (!pilot.localHost && __instance.parentInventory != null) pilot.ReportUncapturedWrite("Destroy", __instance);
            return pilot.localHost && __instance.parentInventory != null;
        }
    }

    private void ReportUncapturedWrite(string operation, GameItem item)
    {
        if (item != null && uncapturedWrites.Add((operation, item.identifier)))
            Debug.LogWarning($"[probably_coop] ignored uncaptured guest inventory write: {operation} {item.identifier}");
    }
}
