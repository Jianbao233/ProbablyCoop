using System;
using System.Linq;
using HarmonyLib;
using ProbablyCoop.Core;

namespace ProbablyCoop;
internal sealed partial class NativeBackgroundQa
{
    private int groupStep,groupDx,groupDy;
    private double groupNext,groupUntil;
    private GameItemElement[] groupItems;
    private (int x,int y)[] groupOrigin;
    private int groupTotal;
    private bool RunGroupQa(ReplicaLink link,double now)
    {
        if(groupStep==9) return true;
        if(now<groupNext) return false;
        var handler=ItemMultiSelectHandler.current; var inventory=EmporiumEntry.Instance.backInvinvElement;
        if(groupStep==0)
        {
            groupItems=inventory.childItems.OfType<GameItemElement>().Where(GeneralHelper.IsItemOwned).Take(2).ToArray();
            Require(groupItems.Length==2,"missing native group fixtures");
            groupOrigin=groupItems.Select(i=>(i.modifiedShape.minX,i.modifiedShape.minY)).ToArray();
            var selection=NativeGroupTransfer.Selected(handler); selection.Clear(); selection.AddRange(groupItems);
            bool found=false;
            for(int y=-3;y<=3 && !found;y++) for(int x=-4;x<=4 && !found;x++)
                if((x!=0 || y!=0) && (bool)AccessTools.Method(typeof(ItemMultiSelectHandler),"IsFormationValid").Invoke(handler,new object[]{inventory,x,y}))
                { groupDx=x; groupDy=y; found=true; }
            Require(found,"no valid translated native formation");
            QaGroupButton.Held=true;
            AccessTools.Method(typeof(ItemMultiSelectHandler),"StartGroupDrag").Invoke(handler,null);
            Require(handler.IsGroupDragging && groupItems.All(handler.IsGroupDraggingItem),"original multi-select preview did not start");
            groupStep=10; groupUntil=now+20; return false;
        }
        if(groupStep==10)
        {
            Require(now<groupUntil && handler.IsGroupDragging,"group reservation was not confirmed");
            if(!NativeWorld.Guest.HasConfirmedGesture) return false;
            AccessTools.Method(typeof(ItemMultiSelectHandler),"CommitPrecisePlacement").Invoke(handler,new object[]{inventory,groupDx,groupDy});
            for(int i=0;i<groupItems.Length;i++)
                Require(groupItems[i].modifiedShape.minX==groupOrigin[i].x+groupDx &&
                    groupItems[i].modifiedShape.minY==groupOrigin[i].y+groupDy,
                    "native group placement feedback waited for the host");
            QaGroupButton.Held=false;
            handler.Cancel();
            Require(link.HasUnacknowledgedRequests,"native group commit emitted no request"); groupStep=1; groupUntil=now+20; return false;
        }
        Require(now<groupUntil,"native group transfer timed out");
        if(groupStep==6)
        {
            groupTotal=inventory.childItems.Concat(EmporiumEntry.Instance.invElement.childItems).Sum(i=>i.unitCount);
            Require(NativeWorld.Guest.RequestGroup(groupItems,EmporiumEntry.Instance.invElement),"normal group command not submitted");
            groupStep=7; return false;
        }
        if(groupStep==7)
        {
            if(link.HasUnacknowledgedRequests) return false;
            Require(QaLatestResult(link)==(ushort)ShopResult.Applied,"normal group transfer rejected");
            groupStep=8; groupNext=now+0.8; return false;
        }
        if(groupStep==8)
        {
            Require(groupItems.All(i=>i.IsDestroyed() || i.parentInventory==EmporiumEntry.Instance.invElement),"normal group left source item behind");
            Require(groupTotal==inventory.childItems.Concat(EmporiumEntry.Instance.invElement.childItems).Sum(i=>i.unitCount),"normal group quantity changed");
            Record("PASS authoritative-group-transfer","two-item ordinary transfer applied through host native inventory operations; quantity conserved");
            groupStep=9; return true;
        }
        if(groupStep==1 || groupStep==3 || groupStep==5)
        {
            if(link.HasUnacknowledgedRequests) return false;
            Require(QaLatestResult(link)==(ushort)(groupStep==5 ? ShopResult.NoSpace : ShopResult.Applied),"native group result="+QaLatestResult(link)+" step="+groupStep);
            groupStep++; groupNext=now+0.7;
            if(groupStep==6)
            {
                for(int i=0;i<2;i++) Require(groupItems[i].modifiedShape.minX==groupOrigin[i].x && groupItems[i].modifiedShape.minY==groupOrigin[i].y,"invalid group move changed an item");
                Record("PASS native-group-placement","original multi-select commit; two-item formation moved and restored; out-of-bounds whole group rejected without mutation");
            }
            return false;
        }
        for(int i=0;i<2;i++) Require(groupItems[i].parentInventory==inventory && groupItems[i].modifiedShape.minX==groupOrigin[i].x+(groupStep==2 ? groupDx : 0) &&
            groupItems[i].modifiedShape.minY==groupOrigin[i].y+(groupStep==2 ? groupDy : 0),"group formation projection mismatch");
        var selected=NativeGroupTransfer.Selected(handler); selected.AddRange(groupItems);
        AccessTools.Method(typeof(ItemMultiSelectHandler),"CommitPrecisePlacement").Invoke(handler,new object[]{inventory,groupStep==2 ? -groupDx : 127,groupStep==2 ? -groupDy : 127}); selected.Clear();
        Require(link.HasUnacknowledgedRequests,"native group retry emitted no request"); groupStep++; return false;
    }
}

// No physical mouse is held on the private QA desktop. Advance the real
// drag preview during the two scripted holds. The native press/release,
// placement and transport are exercised separately from OS input sampling.
[HarmonyPatch(typeof(ItemMultiSelectHandler),"OnEventUpdate")]
internal static class QaGroupButton
{
    internal static bool Held;
    [HarmonyPrepare] private static bool Install()=> NativeQaDisplay.IsIsolatedQa &&
        NativeBackgroundQa.IsGuestProduct &&
        Array.IndexOf(Environment.GetCommandLineArgs(),"--probably-coop-qa")>=0;
    private static bool Prefix(ItemMultiSelectHandler __instance,ref bool __result)
    {
        if(!Held || !__instance.IsGroupDragging) return true;
        AccessTools.Method(typeof(ItemMultiSelectHandler),"PruneSelection").Invoke(__instance,null);
        if(!__instance.IsGroupDragging) { __result=false; return false; }
        AccessTools.Method(typeof(ItemMultiSelectHandler),"MoveGhosts").Invoke(__instance,null);
        AccessTools.Method(typeof(ItemMultiSelectHandler),"UpdateDropTarget").Invoke(__instance,null);
        __result=true;
        return false;
    }
}
