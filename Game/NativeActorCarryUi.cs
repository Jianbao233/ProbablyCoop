using System;
using HarmonyLib;
using UnityEngine;

namespace ProbablyCoop;

// Reuse the local player's original afterhour window and all three native
// slots. Only the ground *panel* is hidden indoors: the inventory graph and
// original slot identities remain intact for native ownership/enumeration.
internal static class NativeActorCarryUi
{
    private sealed class Binding
    {
        internal readonly NativeActorCarry Carry;
        internal readonly EmporiumEntry Entry;
        internal readonly PixelWindow Window;
        internal readonly MachineryPanel Ground;
        internal readonly string Title;
        internal readonly bool GroundHidden;
        private Vector2 originalPosition;
        internal bool Indoors, SawContents, Initial = true;
        internal Binding(NativeActorCarry carry)
        {
            Carry = carry; Entry = carry.Source; Window = Entry.afterhourWindow;
            if (Window?.childElement is not MachineryPanelRow row || row.Panels.Count != 4)
                throw new InvalidOperationException("Native carry panel layout changed.");
            Ground = row.GetPanel("ground");
            // SetupAfterhourInv uses MachineHelper.Panel, whose native
            // WrapInIntake inserts exactly one MachineryIntake around each
            // inventory. Check that wrapper and the exact original slot;
            // arbitrary descendant searches would permit a wrong nested root.
            if (!IsNativePanel(Ground, Entry.afterhourInventory) ||
                !IsNativePanel(row.GetPanel("pocket1"), Entry.afterhourPocketSlotInvLeft) ||
                !IsNativePanel(row.GetPanel("backpack"), Entry.afterhourPocketSlotInvBackpack) ||
                !IsNativePanel(row.GetPanel("pocket2"), Entry.afterhourPocketSlotInvRight))
                throw new InvalidOperationException("Native carry panel identities changed.");
            Title = Window.titleString; GroundHidden = Ground.isHidden;
        }

        internal bool HasContents => Entry.afterhourPocketSlotInvLeft.childItem != null ||
            Entry.afterhourPocketSlotInvRight.childItem != null || Entry.afterhourPocketSlotInvBackpack.childItem != null;

        internal bool Alive => Window?.handler != null && Window.handler.gameObject != null && Ground?.handler != null;

        internal void Update(bool outside)
        {
            bool contents = HasContents;
            if (outside)
            {
                RestorePresentation(false);
                Initial = false; SawContents = contents;
                return;
            }
            bool arrived = !Initial && !Indoors;
            if (!Indoors)
            {
                originalPosition = Window.rectTransform.anchoredPosition;
                Ground.SetHidden(true);
                Window.SetTitle("随身物品");
                Indoors = true;
            }
            // A restored/nonempty carry and a failed return must remain
            // reachable. A manual close is respected until another arrival
            // or an empty -> occupied transition; no per-frame reopen/layout.
            if (contents && (Initial || arrived || !SawContents)) Show();
            Initial = false; SawContents = contents;
        }

        internal void Show()
        {
            Window.Show();
            // Use the original inventory window's position/scale. FixBounds
            // clamps this native window on small screens without a new canvas.
            if (Entry.invWindow?.rectTransform != null)
                Window.rectTransform.anchoredPosition = Entry.invWindow.rectTransform.anchoredPosition +
                    new Vector2(Entry.invWindow.widthPixels + 12, 0);
            Window.FixBounds();
        }

        internal void RestorePresentation(bool hide)
        {
            if (!Alive) return;
            if (Indoors)
            {
                if (hide) Window.Hide();
                Ground.SetHidden(GroundHidden);
                Window.SetTitle(Title);
                Window.rectTransform.anchoredPosition = originalPosition;
                Window.FixBounds();
                Indoors = false;
            }
        }
    }

    private static Binding binding;

    internal static bool Available => LayoutReady(Resolve()) && MapUIManager.Instance != null && !MapUIManager.Instance.IsOutside;
    internal static bool IsOpen => binding?.Indoors == true && binding.Window.IsVisible();

    internal static void Tick()
    {
        var carry = Resolve();
        if (carry == null || MapUIManager.Instance == null) { Reset(); return; }
        // Native Awake publishes the entry before Start builds its windows.
        // An absent attachment is not a corrupt completed layout. Conversely,
        // once attached, wrong roles/wrappers/slots still fail strict binding.
        if (!LayoutReady(carry)) { Reset(); return; }
        if (binding == null || !ReferenceEquals(binding.Carry, carry) || !ReferenceEquals(binding.Window, carry.Source.afterhourWindow))
        {
            Reset();
            binding = new Binding(carry);
        }
        binding.Update(MapUIManager.Instance.IsOutside);
    }

    internal static bool Toggle()
    {
        if (!Available || ItemMouseDragHandler.current?.IsDraggingItem == true ||
            ItemMultiSelectHandler.current?.IsGroupDragging == true) return false;
        // Outside uses the existing complete native afterhour interface.
        // Its scene owner decides when the ground window may be shown/hidden.
        if (MapUIManager.Instance.IsOutside) return false;
        bool wasOpen = IsOpen;
        Tick();
        if (wasOpen) binding.Window.Hide();
        else binding.Show();
        return true;
    }

    internal static void Reset()
    {
        var previous = binding;
        binding = null;
        previous?.RestorePresentation(true);
    }

    private static NativeActorCarry Resolve()
    {
        NativeActorCarry carry;
        if (NativeGuest.Active)
        {
            var world = NativeWorld.Guest;
            if (world?.Ready != true || !world.HasActorProjection) return null;
            carry = world.ActorCarry;
        }
        else carry = NativeActorSession.Current?.Carry;
        if (carry == null || carry.IsDisposed || !ReferenceEquals(carry.Source, EmporiumEntry.Instance)) return null;
        var actor = NativeGuest.Active ? NativeActorRun.Slot.Partner : NativeActorRun.Slot.Host;
        return ReferenceEquals(carry.Get(actor, NativeActorInventory.CarrySlot.LeftPocket), carry.Source.afterhourPocketSlotInvLeft) &&
            ReferenceEquals(carry.Get(actor, NativeActorInventory.CarrySlot.RightPocket), carry.Source.afterhourPocketSlotInvRight) &&
            ReferenceEquals(carry.Get(actor, NativeActorInventory.CarrySlot.Backpack), carry.Source.afterhourPocketSlotInvBackpack) ? carry : null;
    }

    private static bool LayoutReady(NativeActorCarry carry) => carry?.Source.afterhourWindow?.childElement != null;

    private static bool IsNativePanel(MachineryPanel panel, GameInventory inventory) =>
        inventory != null && panel?.content is MachineryIntake intake && ReferenceEquals(intake.content, inventory);

    // A hidden ground panel is still in the original graph. Block only that
    // exact cooperative native ground indoors, including native quick-transfer
    // queries and stale SlotMarkers. No inventory lock/tag/delegate is changed.
    internal static bool BlocksGround(GameInventory inventory)
    {
        if (inventory == null || MapUIManager.Instance == null || MapUIManager.Instance.IsOutside) return false;
        var carry = Resolve();
        return carry != null && ReferenceEquals(inventory, carry.Source.afterhourInventory);
    }
}

[HarmonyPatch(typeof(GameGridInventory), nameof(GameGridInventory.MayHaveValidInventorySlot))]
internal static class NativeActorCarryGroundQuery
{
    [HarmonyPriority(Priority.First)]
    private static bool Prefix(GameGridInventory __instance, ref bool __result)
    { if (!NativeActorCarryUi.BlocksGround(__instance)) return true; __result = false; return false; }
}

[HarmonyPatch(typeof(GeneralHelper), nameof(GeneralHelper.MayPlayerInsertInto), new[] { typeof(GameInventory) })]
internal static class NativeActorCarryGroundPlayerInsert
{
    [HarmonyPriority(Priority.First)]
    private static bool Prefix(GameInventory targetInventory, ref bool __result)
    { if (!NativeActorCarryUi.BlocksGround(targetInventory)) return true; __result = false; return false; }
}

[HarmonyPatch(typeof(GameGridInventory), nameof(GameGridInventory.TryFindOneValidInventorySlot), new[] { typeof(GameItem), typeof(bool) })]
internal static class NativeActorCarryGroundSearch
{
    [HarmonyPriority(Priority.First)]
    private static bool Prefix(GameGridInventory __instance, ref SlotMarker __result)
    { if (!NativeActorCarryUi.BlocksGround(__instance)) return true; __result = null; return false; }
}

[HarmonyPatch(typeof(GameGridInventory), nameof(GameGridInventory.TryFindOneValidInventorySlot_TopMiddle))]
internal static class NativeActorCarryGroundMiddleSearch
{
    [HarmonyPriority(Priority.First)]
    private static bool Prefix(GameGridInventory __instance, ref SlotMarker __result)
    { if (!NativeActorCarryUi.BlocksGround(__instance)) return true; __result = null; return false; }
}

[HarmonyPatch(typeof(SlotMarker), nameof(SlotMarker.TryAcceptOnce))]
internal static class NativeActorCarryGroundDrop
{
    [HarmonyPriority(Priority.First)]
    private static bool Prefix(SlotMarker __instance, ref int __result)
    { if (!NativeActorCarryUi.BlocksGround(__instance.inventory)) return true; __result = 0; return false; }
}

[HarmonyPatch(typeof(SlotMarker), nameof(SlotMarker.AcceptUnchecked))]
internal static class NativeActorCarryGroundUncheckedDrop
{
    [HarmonyPriority(Priority.First)]
    private static bool Prefix(SlotMarker __instance, ref int __result)
    { if (!NativeActorCarryUi.BlocksGround(__instance.inventory)) return true; __result = 0; return false; }
}
