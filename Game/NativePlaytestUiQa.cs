using System.Linq;
using HarmonyLib;
using ProbablyCoop.Core;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ProbablyCoop;

internal sealed partial class NativeBackgroundQa
{
    private double leaveRoomDeadline;

    private void BeginGuestRoomExit(RoomSelector room,double now)
    {
        leaveRoomDeadline=now+20;
        room.Leave();
    }

    private bool VerifyGuestRoomExit(bool inMenu,double now)
    {
        if(leaveRoomDeadline==0) return false;
        Require(now<leaveRoomDeadline,"guest returned without a usable cooperative menu entry");
        if(!inMenu || NativeGuest.Active || NativePointer.Current==null) return true;
        var entry=GameObject.Find("ProbablyCoopMenuButton");
        if(entry==null || !entry.TryGetComponent<Button>(out var button) || !button.IsInteractable()) return true;
        ushort surface=NativePointer.Current.FindSurface(Vector2.zero,out _,out uint entity);
        Require(surface==32 && entity==0,"main-menu pointer still resolves a destroyed shop surface");
        button.onClick.Invoke();
        Require(CustomUIManager.Instance.IsOpen("probably_coop_panel"),"main-menu button failed after leaving the guest room");
        Record("PASS bug-03-return-to-menu","left room in the native guest, shop surface retired, original main-menu coop entry opened room panel");
        Finish("PASS guest runtime");
        return true;
    }

    private void VerifyDragHighlight()
    {
        var nativeDrag=AccessTools.Method(typeof(ItemMouseDragHandler),"Drag");
        Require(Harmony.GetPatchInfo(nativeDrag)?.Transpilers.Any(patch=>patch.PatchMethod.DeclaringType==typeof(NativeDragTargetHighlight))==true,
            "native drag highlight transpiler missing");
        var drag=ItemMouseDragHandler.current;
        var manager=(HighlightManager)AccessTools.Field(typeof(ItemMouseDragHandler),"highlightManager").GetValue(drag);
        var grid=EmporiumEntry.Instance.invElement;
        var slot=new SlotMarker { inventory=grid, itemGridShape=new GridShapeBuilder(1,1).SetDataFill(1).Build() };
        var highlight=AccessTools.MethodDelegate<System.Func<GameInventory,SlotMarker,RenderHandler.BackgroundColor,bool,System.Collections.Generic.List<TreeNodeRender>>>(
            AccessTools.Method(typeof(NativeDragTargetHighlight),"Highlight"));
        var nodes=highlight(grid,slot,RenderHandler.BackgroundColor.Green,true);
        Require(nodes?.Count==1 && nodes[0].image.enabled,"native hover did not paint the fixture grid");
        manager.Clear();
        Require(!nodes[0].image.enabled,"native hover clear left green in a prior cell");
        Record("PASS bug-02-drag-highlight-cleanup","native inventory render and native hover clear; direct target path registered its nodes");
    }

    private void VerifyCarryWindowDrag(ReplicaLink link)
    {
        var entry=EmporiumEntry.Instance;
        var window=entry.afterhourWindow;
        var header=(TreeNodeRender)AccessTools.Field(typeof(PixelWindow),"machineryHeader").GetValue(window);
        Require(window.IsVisible() && header.image.raycastTarget && header.eventHandler!=null,
            "outdoor carry header is not interactive");
        var canvas=header.GetComponentInParent<Canvas>().rootCanvas;
        var camera=canvas.renderMode==RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
        var start=RectTransformUtility.WorldToScreenPoint(camera,header.rectTransform.TransformPoint(header.rectTransform.rect.center));
        var pointer=new PointerEventData(EventSystem.current) { button=PointerEventData.InputButton.Left,position=start };
        var before=window.rectTransform.anchoredPosition;
        ulong request=QaIssued(link);
        var pocket=entry.afterhourPocketSlotInvLeft;
        var backpack=entry.afterhourPocketSlotInvBackpack.childItem;
        header.OnBeginDrag(pointer);
        pointer.position+=new Vector2(40,120);
        header.OnDrag(pointer);
        header.OnEndDrag(pointer);
        var moved=window.rectTransform.anchoredPosition;
        Require((moved-before).sqrMagnitude>4 && QaIssued(link)==request &&
            ReferenceEquals(pocket,entry.afterhourPocketSlotInvLeft) &&
            ReferenceEquals(backpack,entry.afterhourPocketSlotInvBackpack.childItem),
            "carry window movement submitted gameplay or changed the native slots");
        entry.Validate();
        Require((window.rectTransform.anchoredPosition-moved).sqrMagnitude<2,"original layout reset the player's carry window");
        Record("PASS bug-08-native-carry-drag","original carry header pointer callbacks moved the window, retained placement after layout, and issued no network command");
    }
}
