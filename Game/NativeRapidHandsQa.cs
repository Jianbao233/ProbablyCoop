using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using ProbablyCoop.Core;
using UnityEngine;

namespace ProbablyCoop;

internal sealed partial class NativeBackgroundQa
{
    private GameItemElement[] rapidItems;
    private GridShape[] rapidOrigins;
    private int rapidStage;
    private double rapidDeadline;
    private bool rapidBellSent;
    private GameItem rapidMachine;
    private int rapidMachineMode, rapidMachineChoice;
    private bool rapidMachineRestoring;

    // Handler-level input on the private desktop. It exercises original pickup,
    // slot geometry and release, without sending physical input to the user's desktop.
    private bool RunRapidHandsQa(ReplicaLink link, double now)
    {
        if (rapidStage == 3) return !InputConflictRequested || RunRapidInputConflict(link, now);
        if (rapidStage == 0 && Array.IndexOf(Environment.GetCommandLineArgs(), "--probably-coop-qa-input-only") >= 0)
        {
            if (!rapidBellSent)
            {
                if (QaWaiting(link)) return false;
                NotebookUIManager.Instance.CloseUI();
                AdvCalendarUIManager.Instance.CloseUI();
                UnityEngine.Object.FindObjectOfType<StoreBell>().RingBell();
                rapidBellSent = true;
                rapidDeadline = now + 20;
                return false;
            }
            Require(now < rapidDeadline, "guest bell or native customer approach timed out");
            if (QaWaiting(link) || !ObserveNativeApproach(now)) return false;
        }
        if (rapidStage == 0)
        {
            if(rapidItems==null)
            {
                // Freeze identities before the preliminary re-pick/container
                // cases change native inventory traversal order by restoring items.
                rapidItems = EmporiumEntry.Instance.invElement.childItems.OfType<GameItemElement>()
                    .Where(item => GeneralHelper.IsItemOwned(item) && item.unitCount == 1 &&
                        item.modifiedShape.width <= 2 && item.modifiedShape.height <= 2).Take(3).ToArray();
                Require(rapidItems.Length == 3, "three independent whole-item fixtures missing");
                rapidOrigins = rapidItems.Select(item => item.modifiedShape.Clone()).ToArray();
            }
            if (!RunRepeatedHandsQa(link, now)) return false;
            if (!RunContainerHandsQa(link, now)) return false;
            foreach (var item in rapidItems)
            {
                var slot = EmporiumEntry.Instance.backInvinvElement.TryFindOneValidInventorySlot(item);
                Require(slot?.IsValid() == true, "rapid placement has no destination space");
                ReleaseAtNativeSlot(item, slot);
                Require(item.parentInventory == slot.inventory, "release feedback waited for authority");
            }
            Require(link.IndependentInFlight >= 3, "rapid input did not issue independent requests");
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--probably-coop-qa-input-only") >= 0)
            {
                rapidMachine = MachineReplica();
                ItemMouseDoubleClickHandler.current.OpenContentAction(rapidMachine, new Vector2(200,250));
                rapidMachineMode = rapidMachine.GetTagReadonly("HEATING_PLATE_STATE").GetInt();
                rapidMachineChoice = rapidMachineMode == 1 ? 0 : 1;
                Modes(rapidMachine).Get(rapidMachineChoice).SetOn(true,true);
                Require(QaWaiting(link) && Modes(rapidMachine).selected == rapidMachineChoice &&
                    rapidMachine.GetTagReadonly("HEATING_PLATE_STATE").GetInt() == rapidMachineMode,
                    "pending inventory transfers blocked local machine choice or ran its business locally");
                Record("TRACE rapid-machine-input", "original machine opened and radio lit immediately while three inventory commands still awaited host results");
                NativeOffscreenCapture.Capture("qa-couple-machine-pending");
            }
            rapidStage = 1; rapidDeadline = now + 20;
            Record("TRACE rapid-native-releases", "three original StartDrag/OnEventRelease calls in one frame; local positions visible before any reply");
            return false;
        }
        Require(now < rapidDeadline, "rapid placement results timed out");
        if (link.HasUnacknowledgedRequests || NativeWorld.Guest.HasPendingPlacements) return false;
        if (rapidStage == 1)
        {
            if (rapidMachine != null && rapidMachine.GetTagReadonly("HEATING_PLATE_STATE").GetInt() != rapidMachineChoice) return false;
            Require(rapidItems.All(item => item.parentInventory == EmporiumEntry.Instance.backInvinvElement),
                "rapid placements differed after host projection");
            for (int i = 0; i < rapidItems.Length; i++)
            {
                var item = rapidItems[i];
                var slot = EmporiumEntry.Instance.invElement.TryInventorySlot(item, 1, rapidOrigins[i], item.modifiedState);
                Require(slot?.IsValid() == true, "rapid restore slot changed");
                ReleaseAtNativeSlot(item, slot);
            }
            rapidStage = 2;
            return false;
        }
        Require(rapidItems.All(item => item.parentInventory == EmporiumEntry.Instance.invElement && item.unitCount == 1),
            "rapid restoration lost an item");
        for (int i = 0; i < rapidItems.Length; i++)
            Require(rapidItems[i].modifiedShape.minX == rapidOrigins[i].minX && rapidItems[i].modifiedShape.minY == rapidOrigins[i].minY,
                "rapid restoration changed original placement");
        if (rapidMachine != null)
        {
            if (!rapidMachineRestoring)
            {
                Modes(rapidMachine).Get(rapidMachineMode).SetOn(true,true);
                rapidMachineRestoring = true;
                return false;
            }
            if (rapidMachine.GetTagReadonly("HEATING_PLATE_STATE").GetInt() != rapidMachineMode) return false;
            Record("PASS rapid-machine-input", "native station mode accepted and restored by host while independent item transfers completed; local UI stayed open");
        }
        Record("PASS rapid-native-releases", "three different items moved and restored through original pickup/release without waiting between items; exact host entities conserved");
        rapidStage = 3;
        return !InputConflictRequested;
    }

    private static void ReleaseAtNativeSlot(GameItemElement item, SlotMarker slot)
    {
        BeginAtNativeSlot(item, slot);
        ReleaseNativeDrag();
    }

    private static void BeginAtNativeSlot(GameItemElement item, SlotMarker slot, bool holdAcrossFrames=false)
    {
        Require(!Interactable.isPaused, "local input was globally paused by the other player's UI");
        var drag = ItemMouseDragHandler.current;
        AccessTools.Field(typeof(ItemMouseDragHandler), "currentItem").SetValue(drag, item);
        AccessTools.Field(typeof(ItemMouseDragHandler), "lastMouseClickedKey").SetValue(drag, KeyCode.Mouse0);
        AccessTools.Method(typeof(ItemMouseDragHandler), "StartDrag").Invoke(drag, null);
        Require(drag.IsDraggingItem && drag.GetCurrentItem() == item, "rapid original pickup was swallowed");
        PositionDragAtSlot(item, slot);
        AccessTools.Field(typeof(ItemMouseDragHandler), "lastInventory").SetValue(drag, slot.inventory);
        AccessTools.Field(typeof(ItemMouseDragHandler), "lastItem").SetValue(drag, null);
        if(holdAcrossFrames) KeepNativeQaDragAcrossFrames(drag);
    }

    private static void KeepNativeQaDragAcrossFrames(ItemMouseDragHandler drag)
    {
        // This QA input comes from the original handler, without a physical
        // mouse button. Rev12's Update would reset it before the network round trip.
        AccessTools.Field(typeof(ItemMouseDragHandler),"lastMouseClickedKey").SetValue(drag,KeyCode.None);
    }

    private static void PositionDragAtSlot(GameItemElement item, SlotMarker slot)
    {
        var grid = (GameGridInventory)slot.inventory;
        var shape = slot.itemGridShape;
        float size = UISettings.current.gridSize;
        Vector3 center = grid.rectTransform.TransformPoint(new Vector3(
            (shape.minX + shape.width / 2f) * size, -(shape.minY + shape.height / 2f) * size, 0));
        item.fakeMiddle.rectTransform.position += center - (Vector3)item.GetFakeRotatorCenteredPosition();
    }

    private static void ReleaseNativeDrag()
    {
        var drag = ItemMouseDragHandler.current;
        var key=AccessTools.Field(typeof(ItemMouseDragHandler),"lastMouseClickedKey");
        if(drag.IsDraggingItem && (KeyCode)key.GetValue(drag)==KeyCode.None) key.SetValue(drag,KeyCode.Mouse0);
        AccessTools.Method(typeof(ItemMouseDragHandler), "OnEventRelease").Invoke(drag,
            new object[] { new HashSet<KeyCode> { KeyCode.Mouse0 } });
        Require(!drag.IsDraggingItem && drag.GetCurrentItem() == null, "rapid release retained the local hand");
    }
}
