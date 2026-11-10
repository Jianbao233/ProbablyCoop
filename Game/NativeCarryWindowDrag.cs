using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;

namespace ProbablyCoop;

// The carry panel keeps its original slots, layout and visibility rules.
// Only its own header is draggable and the position belongs to this client.
internal static class NativeCarryWindowDrag
{
    private sealed class Placement
    {
        internal bool Dragging;
        internal Vector2? OutsidePosition;
    }

    private static readonly ConditionalWeakTable<PixelWindow, Placement> Placements = new();
    private static readonly AccessTools.FieldRef<PixelWindow, TreeNodeRender> Header =
        AccessTools.FieldRefAccess<PixelWindow, TreeNodeRender>("machineryHeader");
    private static readonly AccessTools.FieldRef<PixelWindow, TreeNodeRender> Background =
        AccessTools.FieldRefAccess<PixelWindow, TreeNodeRender>("background");

    private static bool Cooperative => NativeGuest.Active || NativeActorSession.Current!=null;
    private static bool Outside => MapUIManager.Instance!=null && MapUIManager.Instance.IsOutside;

    internal static void OnShown(PixelWindow window)
    {
        var entry=EmporiumEntry.Instance;
        if(!Cooperative || entry==null || !ReferenceEquals(entry.afterhourWindow,window) || !window.IsVisible()) return;
        Placements.GetValue(window,Attach);
        RestoreOutsidePosition(window);
    }

    private static Placement Attach(PixelWindow window)
    {
        var placement=new Placement();
        var header=Header(window);
        var background=Background(window);
        header.InitEventHandler();
        header.eventHandler.mouseBeginDragFunc.Add(pointer =>
        {
            placement.Dragging=Cooperative && pointer.button==PointerEventData.InputButton.Left &&
                ItemMouseDragHandler.current?.IsDraggingItem!=true && ItemMultiSelectHandler.current?.IsGroupDragging!=true;
            if(!placement.Dragging) return false;
            background.OnBeginDrag(pointer);
            // The original drag handler respects canMove=false for stacking.
            // This is a local raise, leaving native dock/close policy intact.
            window.ToFront(force:true);
            return true;
        });
        header.eventHandler.mouseDragFunc.Add(pointer =>
        {
            if(!placement.Dragging) return false;
            background.OnDrag(pointer);
            if(Outside) placement.OutsidePosition=window.rectTransform.anchoredPosition;
            return true;
        });
        header.eventHandler.mouseEndDragFunc.Add(pointer =>
        {
            if(!placement.Dragging) return false;
            placement.Dragging=false;
            background.OnEndDrag(pointer);
            return true;
        });
        return placement;
    }

    internal static void RestoreOutsidePosition(PixelWindow window)
    {
        if(!Cooperative || !Outside || window?.handler==null || !window.IsVisible() ||
            !Placements.TryGetValue(window,out var placement) || !placement.OutsidePosition.HasValue) return;
        window.rectTransform.anchoredPosition=placement.OutsidePosition.Value;
        window.FixBounds();
    }
}

[HarmonyPatch(typeof(PixelWindow), nameof(PixelWindow.Show), new System.Type[] { })]
internal static class NativeCarryWindowShow
{
    private static void Postfix(PixelWindow __instance) => NativeCarryWindowDrag.OnShown(__instance);
}

[HarmonyPatch(typeof(EmporiumEntry), nameof(EmporiumEntry.Validate))]
internal static class NativeCarryWindowLayout
{
    private static void Postfix(EmporiumEntry __instance) =>
        NativeCarryWindowDrag.RestoreOutsidePosition(__instance.afterhourWindow);
}
