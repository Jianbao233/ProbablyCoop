using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Core;

namespace ProbablyCoop;

internal sealed partial class NativeWorld
{
    private sealed class Gesture
    {
        internal ulong Serial, Pending, GrantedThrough, DropRequest, Context;
        internal double RenewAt;
        internal bool Group, BeginIssued, DropReceiptTaken;
        internal GameItemElement[] Items;
        internal JArray Sources;
        internal JObject Drop;
        internal PendingActions.Entry Action;
        internal HeldInteraction<GraphNodeStorage> SourceFootprint, TargetFootprint;
        internal bool Dragging => Group ? ItemMultiSelectHandler.current?.IsGroupDragging == true :
            ItemMouseDragHandler.current?.IsDraggingItem == true &&
            ReferenceEquals(ItemMouseDragHandler.current.GetCurrentItem(), Items[0]);
    }

    private readonly PendingActions handActions = new PendingActions();
    private readonly List<Gesture> releasedHands = new List<Gesture>();
    private readonly HashSet<ulong> orphanHandRequests = new HashSet<ulong>();
    private readonly Queue<ulong> cancelledHands = new Queue<ulong>();
    private Gesture gesture, releasing;
    private ulong nextHandSerial, handProjectionThrough, projectedHand;
    internal ulong LastGestureCompletion { get; private set; }
    internal ushort LastGestureResult { get; private set; }
    internal bool RemoteHostDragging { get; private set; }
    internal bool HasConfirmedGesture => gesture?.GrantedThrough > 0 && gesture.Serial == projectedHand && gesture.Dragging;
    internal bool IsHoldingSingleItem(GameItemElement item) =>
        gesture != null && releasing == null && !gesture.Group &&
        ReferenceEquals(gesture.Items[0], item) && gesture.Dragging;

    internal bool CanStartGesture()
    {
        TickGuestHand();
        if (gesture == null && handActions.Count < PendingActions.Capacity) return Ready;
        CommandNotice("待确认的移动过多，请稍后再试。");
        return false;
    }

    internal void StartGesture(IEnumerable<GameItemElement> selection, bool group)
    {
        var items = selection.ToArray();
        var rows = new JArray();
        foreach (var item in items)
        {
            if (!TryData(item, out var data) || data["moveToken"]?.Type != JTokenType.String) break;
            rows.Add(new JObject { ["id"]=(uint)data["entity"], ["token"]=ReplicaMoveToken(item) });
        }
        gesture = new Gesture { Serial=checked(++nextHandSerial), Items=items, Sources=rows, Group=group, Context=observedOperationContext };
        if (items.Length is < 1 or > PickupLease.MaxItems || rows.Count != items.Length)
        {
            CancelGesture();
            return;
        }
        gesture.Action = handActions.Reserve(gesture.Serial, rows.Select(row=>(uint)row["id"]).ToArray());
        if (gesture.Action == null) { CancelGesture(); return; }
        gesture.SourceFootprint = new HeldInteraction<GraphNodeStorage>(items, node=>node.children, node=>node.parents);
        AddHandDependencies(gesture);
    }

    private void AddHandDependencies(Gesture hand,GameInventory destination=null,GameItem target=null)
    {
        foreach(var earlier in releasedHands)
            if(!earlier.Action.Finished && earlier.Action.Id<hand.Action.Id &&
                (earlier.SourceFootprint.TouchesOperation(hand.Items,destination,target) ||
                 earlier.TargetFootprint?.TouchesOperation(hand.Items,destination,target)==true))
                hand.Action.AddDependency(earlier.Action);
    }

    private void RetireHandMessages(Gesture hand, bool release)
    {
        if (hand.Pending != 0) { orphanHandRequests.Add(hand.Pending); hand.Pending=0; }
        if (release && hand.BeginIssued) cancelledHands.Enqueue(hand.Serial);
    }

    private void CancelGesture()
    {
        var hand = gesture;
        gesture = null;
        if (hand == null) return;
        if (hand.Action != null) handActions.Cancel(hand.Action);
        RetireHandMessages(hand, true);
        if (ReferenceEquals(hand, releasing) || !hand.Dragging) return;
        if (hand.Group) ItemMultiSelectHandler.current.Cancel();
        else
        {
            AccessTools.Method(typeof(ItemMouseDragHandler), "AbortDrag").Invoke(ItemMouseDragHandler.current, null);
            var item = hand.Items[0];
            if (!item.IsDestroyed()) { item.ToggleFakeBackground(false); item.Validate(); }
        }
    }

    private void CancelAllHandActions()
    {
        CancelGesture();
        foreach (var hand in releasedHands)
        {
            RetireHandMessages(hand, true);
            if (hand.DropRequest != 0 && !hand.DropReceiptTaken) orphanHandRequests.Add(hand.DropRequest);
        }
        releasedHands.Clear();
        handActions.Clear();
    }

    private void ObserveHandReply()
    {
        var hand = gesture;
        if (hand == null || hand.Pending == 0 || !link.TryTakeIndependentResult(hand.Pending, out ushort result)) return;
        ulong completed = hand.Pending;
        hand.Pending = 0;
        if (hand.GrantedThrough == 0 || result != (ushort)ShopResult.Applied) RecordGestureResult(completed, result);
        if (result != (ushort)ShopResult.Applied) { CancelGesture(); return; }
        hand.GrantedThrough = completed;
        hand.RenewAt = HandClock + PickupLease.LifetimeSeconds / 3;
    }

    private void RecordGestureResult(ulong serial, ushort result)
    {
        if (serial <= LastGestureCompletion) return;
        LastGestureCompletion = serial;
        LastGestureResult = result;
    }

    private void ObserveHandProjection(ulong active, ulong through)
    {
        handProjectionThrough = Math.Max(handProjectionThrough, through);
        handActions.Projected(through);
        projectedHand = active;
        ObserveHandReply();
        CollectReleasedHands(true);
        if (gesture != null && gesture.GrantedThrough != 0 && through >= gesture.GrantedThrough && active != gesture.Serial)
            CancelGesture();
    }

    private bool SourcesStillMatch(Gesture hand) => hand.Sources.All(row =>
        model.Data.TryGetValue((uint)row["id"], out var data) &&
        (string)data["moveToken"] == (string)row["token"]);

    private void CollectReleasedHands(bool baseRestored = false)
    {
        bool failed = false, retired = false;
        ShopResult refusal=ShopResult.None;
        foreach (var hand in releasedHands.ToArray())
        {
            if (hand.DropRequest != 0 && link.TryTakeIndependentResult(hand.DropRequest, out ushort result))
            {
                hand.DropReceiptTaken = true;
                RecordGestureResult(hand.DropRequest, result);
                handActions.Result(hand.Action, result == (ushort)ShopResult.Applied);
                if(result!=(ushort)ShopResult.Applied)
                {
                    failed=true;
                    refusal=(ShopResult)result;
                }
            }
        }
        foreach (var hand in releasedHands.ToArray())
        {
            if (!hand.Action.Finished) continue;
            bool rejected = hand.Action.State != PendingActions.Status.Committed;
            RetireHandMessages(hand, rejected);
            if (rejected && hand.DropRequest != 0 && !hand.DropReceiptTaken) orphanHandRequests.Add(hand.DropRequest);
            releasedHands.Remove(hand);
            if(!rejected) OpenAcknowledgedDragEditor(hand);
            retired = true;
            failed |= rejected;
        }
        if (retired && !baseRestored)
        {
            RestorePlacementPreviews();
            ReapplyPlacementPreviews(handProjectionThrough);
        }
        if (failed) CommandNotice(ModHelper.GetLocalized("probably_coop",refusal switch {
            ShopResult.NoSpace=>"drop_full",
            ShopResult.Busy=>"drop_busy",
            ShopResult.Unavailable=>"drop_unavailable",
            _=>"drop_stale"
        }));
    }

    private void TickGuestHand()
    {
        if (link == null) return;
        foreach (ulong request in orphanHandRequests.ToArray())
            if (link.TryTakeIndependentResult(request, out _)) orphanHandRequests.Remove(request);
        ObserveHandReply();
        CollectReleasedHands();
        if (gesture?.Action?.Finished == true) CancelGesture();
        while (cancelledHands.Count > 0)
        {
            if (!SubmitHand(new JObject { ["kind"]="handRelease", ["hand"]=cancelledHands.Peek() }, out ulong serial)) break;
            orphanHandRequests.Add(serial);
            cancelledHands.Dequeue();
        }
        // Preserve the order of the actor's hands. The host maintains one
        // renewable pickup lease, while any number of completed local gestures
        // can await their own business result inside the bounded action window.
        foreach (var hand in releasedHands)
        {
            if (hand.DropRequest != 0) continue;
            if (!hand.Action.CanSubmit) break;
            if (!SourcesStillMatch(hand)) { handActions.Reject(hand.Action); break; }
            if (!SubmitHand(hand.Drop, out ulong serial)) break;
            handActions.Sent(hand.Action, serial);
            hand.DropRequest = serial;
            RetireHandMessages(hand, false);
        }
        var current = gesture;
        if (current == null) return;
        if (!current.Dragging) { CancelGesture(); return; }
        if (releasedHands.Any(hand => hand.DropRequest == 0) || !current.Action.CanSubmit) return;
        if (current.Pending != 0 || current.GrantedThrough != 0 && HandClock < current.RenewAt) return;
        if (!SourcesStillMatch(current)) { CancelGesture(); return; }
        var message = new JObject {
            ["kind"]=current.GrantedThrough == 0 ? "handBegin" : "handRenew",
            ["hand"]=current.Serial, ["context"]=current.Context
        };
        if (current.GrantedThrough == 0) message["items"] = current.Sources;
        if (SubmitHand(message, out ulong pending))
        {
            current.Pending = pending;
            current.BeginIssued = true;
        }
    }

    private bool SubmitHand(JObject command, out ulong serial)
    {
        SealOperation(command);
        return link.SubmitIndependent(System.Text.Encoding.UTF8.GetBytes(command.ToString(Newtonsoft.Json.Formatting.None)), HandClock, out serial);
    }

    private bool Submit(JObject command)
    {
        if (releasing != null && !ReferenceEquals(releasing, gesture) && DropSources(command) != null) return false;
        if (gesture == null) return SubmitDirect(command);
        var hand = gesture;
        var rows = DropSources(command);
        bool matches = rows != null && rows.Count == hand.Sources.Count &&
            rows.All(row => hand.Sources.Any(source => (uint)source["id"] == (uint)row["id"]));
        ObserveHandReply();
        if (!matches) return SubmitDirect(command);
        if (gesture != hand || hand.Drop != null) return false;
        if (hand.Group) command["items"] = hand.Sources;
        else command["token"] = (string)hand.Sources[0]["token"];
        command["hand"] = hand.Serial;
        command["context"] = hand.Context;
        hand.Drop = command;
        model.Objects.TryGetValue((uint?)command["destination"] ?? 0, out var destination);
        model.Objects.TryGetValue((uint?)command["target"] ?? 0, out var target);
        AddHandDependencies(hand,destination as GameInventory,target as GameItem);
        if(target is GameItem targetItem)
            hand.TargetFootprint=new HeldInteraction<GraphNodeStorage>(new[]{targetItem},node=>node.children,node=>node.parents);
        PreviewHandPlacement(command, hand.Action);
        gesture = null;
        releasedHands.Add(hand);
        TickGuestHand();
        return true;
    }

    internal object BeginNativeRelease()
    {
        var previous = releasing;
        releasing = gesture;
        return previous;
    }
    internal void EndNativeRelease(object previous) => releasing = (Gesture)previous;
}

[HarmonyPatch(typeof(ItemMouseDragHandler),"EndDrag")]
internal static class NativeSingleReleaseScope
{
    private static void Prefix(ref bool cancelled, GameItemElement ___currentItem, ref GameItem ___lastItem,
        ref GameInventory ___lastInventory, int ___currentRemove, out object __state)
    {
        var guest=NativeWorld.Guest;
        __state=guest?.BeginNativeRelease();
        if(cancelled || ___lastInventory!=null || ___currentItem==null || ___lastItem==null ||
            guest?.RequestItemDrop(___currentItem,___lastItem,___currentRemove)!=true) return;
        // Keep original drag cleanup and sounds; skip its local mutations for
        // the item hit already handled by this release.
        cancelled=true;
        ___lastItem=null;
    }
    private static void Finalizer(object __state) => NativeWorld.Guest?.EndNativeRelease(__state);
}

[HarmonyPatch(typeof(ItemMouseDragHandler),"StartDrag")]
internal static class NativeSinglePickup
{
    private static bool Prefix(ItemMouseDragHandler __instance)
    {
        if(NativeGuest.Active) return NativeWorld.Guest?.CanStartGesture()==true;
        return NativeWorld.Host?.RejectHeldInput(__instance.GetCurrentItem())!=true;
    }
    private static void Postfix(ItemMouseDragHandler __instance,bool __runOriginal)
    {
        if(__runOriginal && NativeGuest.Active && __instance.IsDraggingItem && __instance.GetCurrentItem() is GameItemElement item)
        {
            NativeGuestDragVisual.MakeTransparentToInput(item);
            NativeWorld.Guest?.StartGesture(new[]{item},false);
        }
    }
}
[HarmonyPatch(typeof(ItemMultiSelectHandler),"StartGroupDrag")]
internal static class NativeGroupPickup
{
    private static bool Prefix(ItemMultiSelectHandler __instance)
    {
        if(NativeGuest.Active) return NativeWorld.Guest?.CanStartGesture()==true;
        var host=NativeWorld.Host;
        return host==null || !NativeGroupTransfer.Selected(__instance).Any(host.RejectHeldInput);
    }
    private static void Postfix(ItemMultiSelectHandler __instance,bool __runOriginal)
    {
        if(__runOriginal && NativeGuest.Active && __instance.IsGroupDragging)
            NativeWorld.Guest?.StartGesture(NativeGroupTransfer.Selected(__instance),true);
    }
}
