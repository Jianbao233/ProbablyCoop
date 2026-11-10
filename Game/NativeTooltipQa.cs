using System;
using System.Linq;
using UnityEngine;

namespace ProbablyCoop;

internal sealed partial class NativeBackgroundQa
{
    private int qaReadStep;
    private double qaReadDeadline;
    private GameItemElement previousRead;
    private ulong heldObservation;
    private bool VerifyGuestTooltipReads(double now)
    {
        if(qaReadStep==2) return true;
        if(qaReadDeadline==0) qaReadDeadline=now+15;
        Require(now<qaReadDeadline,"native tooltip read reply missing at target "+qaReadStep);
        var world=NativeWorld.Guest;
        var item=qaReadStep==0 ? (GameItemElement)MachineReplica() : world.Objects.Values.OfType<GameItemElement>().Single(i=>i.identifier=="large_bottled_water");
        var normal=item.GetTooltip();
        var advanced=item.GetTooltipAdvanced();
        Require(!string.IsNullOrEmpty(normal.nakedText) && !string.IsNullOrEmpty(advanced.nakedText),"first native hover lost its complete baseline");
        if(world.CurrentFastTooltip(item,false)==null) return false;
        if(qaReadStep==0)
        {
            if(heldObservation==0) { heldObservation=world.CurrentFastObservation; return false; }
            if(world.CurrentFastObservation<=heldObservation) return false;
            Record("PASS stationary-native-hover","two increasing host observations for the same original item without selecting another target");
        }
        Require(ReferenceEquals(normal,world.CurrentFastTooltip(item,false)) && ReferenceEquals(advanced,world.CurrentFastTooltip(item,true)),
            "normal or advanced native getter did not use the matching read");
        if(previousRead!=null) Require(world.CurrentFastTooltip(previousRead,true)==null,"previous item's read leaked into the new target");
        var overlay=OverlayHandler.current;
        try
        {
            overlay.ShowTooltip(new Vector2(Screen.width*0.35f,Screen.height*0.5f),Vector2.zero,qaReadStep==0 ? normal : advanced,
                item.IsGameItemType("MACHINE") ? 72 : 60);
            Canvas.ForceUpdateCanvases();
            NativeOffscreenCapture.Capture(qaReadStep==0 ? "qa-focused-machine" : "qa-focused-water");
        }
        finally { overlay.HideTooltip(); }
        previousRead=item;
        qaReadStep++;
        qaReadDeadline=now+15;
        if(qaReadStep==2) Record("PASS native-tooltip-reads","both native getters had complete first-hover text; two distinct entities received their own host replies through the live adapter");
        return qaReadStep==2;
    }
}
