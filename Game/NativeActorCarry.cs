using System;
using System.Collections.Generic;
using System.Reflection;

namespace ProbablyCoop;

// The local player's three native slots keep their identity and their original
// window. The other player has three real, hidden native slots, not cloned bags
// or UI aliases. The session owns actor scopes; this bank never swaps fields.
internal sealed class NativeActorCarry : IDisposable
{
    internal readonly struct TransferResult
    {
        internal readonly int MovedUnits, RemainingUnits;
        internal readonly string Reason;
        internal bool Complete => RemainingUnits == 0 && Reason == null;
        internal TransferResult(int moved, int remaining, string reason = null)
        { MovedUnits = moved; RemainingUnits = remaining; Reason = reason; }
    }

    private static readonly FieldInfo SelectedField = typeof(EmporiumEntry).GetField(
        "<selectedBackpack>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new MissingFieldException(typeof(EmporiumEntry).FullName, "selectedBackpack");
    private readonly EmporiumEntry source;
    private readonly Func<NativeActorRun.Slot> currentActor;
    private readonly NativeActorRun.Slot localActor;
    private readonly GameSlotInventory[,] slots = new GameSlotInventory[2, 3];
    private readonly GameItem[] selected = new GameItem[2];
    private TreeNodeRender remoteRenderer;
    private readonly int thread = Environment.CurrentManagedThreadId;
    private bool disposed;

    internal EmporiumEntry Source => source;
    internal bool IsDisposed => disposed;
    internal NativeActorRun.Slot CurrentActor
    { get { CheckActive(); var actor = currentActor(); RequireActor(actor); return actor; } }

    internal NativeActorCarry(EmporiumEntry existing, Func<NativeActorRun.Slot> currentActor,
        NativeActorRun.Slot localActor)
    {
        source = existing ?? throw new ArgumentNullException(nameof(existing));
        this.currentActor = currentActor ?? throw new ArgumentNullException(nameof(currentActor));
        RequireActor(localActor);
        this.localActor = localActor;
        if (existing.afterhourPocketSlotInvLeft == null || existing.afterhourPocketSlotInvRight == null ||
            existing.afterhourPocketSlotInvBackpack == null)
            throw new InvalidOperationException("Create actor carry after native SetupAfterhourInv.");
        if (ReferenceEquals(existing.afterhourPocketSlotInvLeft, existing.afterhourPocketSlotInvRight) ||
            ReferenceEquals(existing.afterhourPocketSlotInvLeft, existing.afterhourPocketSlotInvBackpack) ||
            ReferenceEquals(existing.afterhourPocketSlotInvRight, existing.afterhourPocketSlotInvBackpack))
            throw new InvalidOperationException("Native carry slots must be distinct.");
        slots[(int)localActor, 0] = existing.afterhourPocketSlotInvLeft;
        slots[(int)localActor, 1] = existing.afterhourPocketSlotInvRight;
        slots[(int)localActor, 2] = existing.afterhourPocketSlotInvBackpack;
        int other = 1 - (int)localActor;
        try
        {
            // Visibility belongs to this frontend, not to replicated slot
            // state. Hide a rendering parent instead of slot.Hide(): syncing
            // isHidden would hide the partner's original visible slots too.
            remoteRenderer = TreeNodeRender.Instantiate();
            remoteRenderer.gameObject.SetActive(false);
            // Register each allocation before configuring it, so a failed atlas
            // or localization lookup can still clean up the hidden renderer.
            var left = slots[other, 0] = new GameSlotInventory();
            left.handler.SetParent(remoteRenderer);
            left.SetTooltipName("[" + LocHelper.GetLocalizedItem("item_expedition_box_label_pocket1") + "]");
            left.SetBackgroundFadeSprite("Items/items_container", "pouch");
            ContainerHelper.AllowOnlyOwnedItems(left);
            var right = slots[other, 1] = new GameSlotInventory();
            right.handler.SetParent(remoteRenderer);
            right.SetTooltipName("[" + LocHelper.GetLocalizedItem("item_expedition_box_label_pocket2") + "]");
            right.SetBackgroundFadeSprite("Items/items_container", "pouch");
            ContainerHelper.AllowOnlyOwnedItems(right);
            var backpack = slots[other, 2] = new GameSlotInventory(3, 3, stealUI: true);
            backpack.handler.SetParent(remoteRenderer);
            backpack.SetBackgroundFadeSprite("Items/items_backpack2", "simple_backpack_medium");
            ContainerHelper.AllowOnlyTaggedItem(backpack, "BACKPACK_TAG", ownedOnly: true);
        }
        catch { ReleaseRemoteRenderers(); disposed = true; throw; }
    }

    internal GameSlotInventory Get(NativeActorRun.Slot actor, NativeActorInventory.CarrySlot place)
    { CheckActive(); RequireActor(actor); return slots[(int)actor, Index(place)]; }

    internal GameItem Selected(NativeActorRun.Slot actor)
    {
        CheckActive(); RequireActor(actor);
        // Read the backing field directly: the native property's IL is routed
        // by CarryAccess and would otherwise recursively call this method.
        return actor == localActor ? ReadNativeSelected(source) : selected[(int)actor];
    }

    internal void SetSelected(NativeActorRun.Slot actor, GameItem item)
    {
        CheckActive(); RequireActor(actor);
        if (actor == localActor) WriteNativeSelected(source, item);
        else selected[(int)actor] = item;
    }

    internal static GameItem ReadNativeSelected(EmporiumEntry entry)
    { if (ReferenceEquals(entry, null)) throw new NullReferenceException(); return (GameItem)SelectedField.GetValue(entry); }
    internal static void WriteNativeSelected(EmporiumEntry entry, GameItem item)
    { if (ReferenceEquals(entry, null)) throw new NullReferenceException(); SelectedField.SetValue(entry, item); }

    // For feature/save traversal, not for deciding physical ownership. Native
    // windows may alias subtrees; reference dedup keeps each feature once. The
    // ownership graph adapter separately validates actual item.parentInventory.
    internal List<GameItem> GetAllCarriedItems(NativeActorRun.Slot actor)
    {
        CheckActive(); RequireActor(actor);
        var result = new List<GameItem>();
        var seen = new HashSet<GameItem>();
        for (int i = 0; i < 3; i++)
            foreach (var item in GraphUtils.FindAllChildrenType<GameItem>(slots[(int)actor, i]))
                if (item != null && seen.Add(item)) result.Add(item);
        return result;
    }

    internal List<GameItem> GetAllCarriedItems()
    {
        var result = GetAllCarriedItems(NativeActorRun.Slot.Host);
        var seen = new HashSet<GameItem>(result);
        foreach (var item in GetAllCarriedItems(NativeActorRun.Slot.Partner))
            if (seen.Add(item)) result.Add(item);
        return result;
    }

    // Only actor carry is returned here. Shared ground/waste disposal remains
    // the single native scene transition's responsibility.
    internal TransferResult ReturnCarryToShop(NativeActorRun.Slot actor)
    {
        RequireActionScope(actor);
        int moved = 0, remaining = 0;
        foreach (int index in new[] { 2, 1, 0 })
        {
            var slot = slots[(int)actor, index];
            var item = slot.childItem;
            if (item == null) continue;
            if (!ReferenceEquals(item.parentInventory, slot))
                throw new InvalidOperationException("Carry parent changed before return.");
            int original = item.unitCount;
            int left = original;
            foreach (GraphNodeStorage destination in new GraphNodeStorage[]
                { source.backInvinvElement, source.invElement, source.showcaseElement })
            {
                if (left <= 0 || destination == null) continue;
                // Native helper returns remaining units, not transferred units.
                left = GraphUtils.TryAcceptAll(destination, item, left);
            }
            moved = checked(moved + original - left);
            remaining = checked(remaining + left);
            if (left > 0 && (!ReferenceEquals(slot.childItem, item) || !ReferenceEquals(item.parentInventory, slot)))
                throw new InvalidOperationException("A partial carry return lost its source ownership.");
            if (left == 0 && (ReferenceEquals(slot.childItem, item) || ReferenceEquals(item.parentInventory, slot)))
                throw new InvalidOperationException("Native carry return reported completion without moving its source.");
        }
        return new TransferResult(moved, remaining, remaining == 0 ? null : "capacity-or-native-rule");
    }

    internal TransferResult TransferSelectedBackpack(NativeActorRun.Slot actor)
    {
        RequireActionScope(actor);
        var item = Selected(actor);
        if (item == null) return new TransferResult(0, 0);
        var slot = Get(actor, NativeActorInventory.CarrySlot.Backpack);
        if (ReferenceEquals(slot.childItem, item) && ReferenceEquals(item.parentInventory, slot))
        { SetSelected(actor, null); return new TransferResult(0, 0); }
        int amount = item.unitCount;
        if (item.parentInventory == null || amount <= 0)
            return new TransferResult(0, Math.Max(0, amount), "selected-item-unavailable");
        if (slot.childItem != null)
            return new TransferResult(0, amount, "backpack-slot-occupied");
        var other = actor == NativeActorRun.Slot.Host ? NativeActorRun.Slot.Partner : NativeActorRun.Slot.Host;
        if (GetAllCarriedItems(other).Contains(item))
            return new TransferResult(0, amount, "other-actor-carry");
        var parent = item.parentInventory;
        int left = GraphUtils.TryAcceptAll(slot, item, amount);
        if (left == 0 && ReferenceEquals(slot.childItem, item) && ReferenceEquals(item.parentInventory, slot))
        { SetSelected(actor, null); return new TransferResult(amount, 0); }
        if (left > 0 && !ReferenceEquals(item.parentInventory, parent))
            throw new InvalidOperationException("A rejected backpack transfer changed source ownership.");
        // Unlike the native method, failed insertion retains the selected draft
        // and its physical item, so capacity failure cannot masquerade as success.
        return new TransferResult(amount - left, left, "capacity-or-native-rule");
    }

    public void Dispose()
    {
        CheckThread();
        if (disposed) return;
        int other = 1 - (int)localActor;
        for (int i = 0; i < 3; i++)
            if (slots[other, i]?.childItem != null)
                throw new InvalidOperationException("Return or checkpoint remote carry before disposing a live scene.");
        DisposeForSceneUnload();
    }

    // Explicit lifecycle boundary: scene unload owns destruction of native
    // items. We release only renderers created by this bank, never Destroy any
    // GameItem or detach/replace the local player's original three slots.
    internal void DisposeForSceneUnload()
    {
        CheckThread();
        if (disposed) return;
        ReleaseRemoteRenderers();
        Array.Clear(selected, 0, selected.Length);
        disposed = true;
    }

    private void ReleaseRemoteRenderers()
    {
        int other = 1 - (int)localActor;
        for (int i = 0; i < 3; i++)
        {
            // Also catches a slot created just before attaching failed.
            var renderer = slots[other, i]?.handler;
            if (renderer != null && renderer.gameObject != null) UnityEngine.Object.Destroy(renderer.gameObject);
            slots[other, i] = null;
        }
        if (remoteRenderer != null && remoteRenderer.gameObject != null) UnityEngine.Object.Destroy(remoteRenderer.gameObject);
        remoteRenderer = null;
    }
    private void RequireActionScope(NativeActorRun.Slot actor)
    { RequireActor(actor); if (CurrentActor != actor) throw new InvalidOperationException("Carry actions require the matching actor scope."); }
    private void CheckActive()
    {
        CheckThread();
        if (disposed) throw new ObjectDisposedException(nameof(NativeActorCarry));
        if (!ReferenceEquals(slots[(int)localActor, 0], source.afterhourPocketSlotInvLeft) ||
            !ReferenceEquals(slots[(int)localActor, 1], source.afterhourPocketSlotInvRight) ||
            !ReferenceEquals(slots[(int)localActor, 2], source.afterhourPocketSlotInvBackpack))
            throw new InvalidOperationException("Native carry was rebuilt; retire its previous actor bank.");
    }
    private void CheckThread()
    { if (Environment.CurrentManagedThreadId != thread) throw new InvalidOperationException("Actor carry belongs to the owning game thread."); }
    private static void RequireActor(NativeActorRun.Slot actor)
    { if (actor is not (NativeActorRun.Slot.Host or NativeActorRun.Slot.Partner)) throw new ArgumentOutOfRangeException(nameof(actor)); }
    private static int Index(NativeActorInventory.CarrySlot slot) => slot switch
    { NativeActorInventory.CarrySlot.LeftPocket => 0, NativeActorInventory.CarrySlot.RightPocket => 1,
      NativeActorInventory.CarrySlot.Backpack => 2, _ => throw new ArgumentOutOfRangeException(nameof(slot)) };
}
