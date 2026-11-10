using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Core;

namespace ProbablyCoop;

internal sealed partial class NativeWorld
{
    private sealed class ScratchRequest
    { internal LocalFrontendView View; internal ActivityRequests.Entry Attempt; internal int Slot; }
    private readonly Dictionary<ulong,ScratchRequest> scratchRequests=new Dictionary<ulong,ScratchRequest>();
    private GameItem scratchRenderedTarget;
    private string scratchRenderedFace;
    private readonly bool[] scratchShown=new bool[3];
    private static readonly System.Reflection.FieldInfo ScratchNativeRevealed=AccessTools.Field(typeof(ScratchInput),"revealed");
    private static string ScratchFace(GameItem item)=>HashData(new JObject {
        ["symbols"]=new JArray(Enumerable.Range(1,3).Select(slot=>item.GetTagReadonly("SCRATCHER_SYMBOL"+slot).GetInt())),
        ["luck"]=LotteryHelper.GetSpawnLuck(item),
        ["directions"]=new JArray(Enumerable.Range(1,3).Select(slot=>new JObject {
            ["enabled"]=item.IsTag("SCRATCHER_LUCK_INT"+slot),
            ["value"]=item.IsTag("SCRATCHER_LUCK_INT"+slot) ? item.GetTagReadonly("SCRATCHER_LUCK_INT"+slot).GetInt() : 0 })) });
    private void OpenScratch(GameItem item)
    {
        ScratcherUIManager.Instance.OpenUI(item);
        scratchRenderedTarget=item; scratchRenderedFace=ScratchFace(item);
        for(int i=0;i<3;i++) scratchShown[i]=item.GetTagReadonly("SCRATCHER_STATE"+(i+1)).GetInt()==1;
    }
    internal bool ScratchReady=>Ready && localFrontends.TryGetValue(FrontendKind.Scratcher,out var view) && !view.Predicted &&
        FrontendVisible(FrontendKind.Scratcher,view.Target) && ReferenceEquals(view.Target,scratchRenderedTarget);
    internal bool RequestScratch(int slot)
    {
        if(slot is <0 or >3 || !ScratchReady || !localFrontends.TryGetValue(FrontendKind.Scratcher,out var view) || !TryData(view.Target,out var data)) return false;
        if(slot==0 && !view.Target.IsTag("SCRATCHER_NEW_TAG") || slot>0 && view.Target.GetTagReadonly("SCRATCHER_STATE"+slot).GetInt()==1) return true;
        if(scratchRequests.Values.Any(row=>ReferenceEquals(row.View,view) && row.Slot==slot)) return true;
        var command=new JObject { ["kind"]="frontend",["type"]=(int)FrontendKind.Scratcher,["view"]=view.Serial,
            ["id"]=data["entity"],["token"]=data["moveToken"],["face"]=scratchRenderedFace,["slot"]=slot };
        if(!Submit(command)) return false;
        scratchRequests[LastSubmittedUiRequest]=new ScratchRequest { View=view,Slot=slot,Attempt=UiRequestState(LastSubmittedUiRequest) };
        if(slot>0) scratchShown[slot-1]=true;
        if(NativeBackgroundQa.Enabled) NativeBackgroundQa.RememberScratchCommand(command);
        return true;
    }
    private static void StartScratch(GameItem item)
    {
        if(!item.IsTag("SCRATCHER_NEW_TAG")) return;
        // Exact business portion of native ScratchInput.OnAnyScratch.
        item.DisableTag("SCRATCHER_NEW_TAG"); item.unitValue=0;
        item.RemoveGameItemType("LOTTERY"); LotteryHelper.OnScratcherStarted();
    }
    private ushort ExecuteScratch(JObject command)
    {
        if(!nativeFrontends.TryGetValue(FrontendKind.Scratcher,out var view) || view.Serial!=(ulong)command["view"] || !ValidFrontend(view) ||
            !CurrentItem(command,out var item) || !ReferenceEquals(item,view.Target) || (string)command["face"]!=ScratchFace(item)) return (ushort)ShopResult.Stale;
        if(!item.IsTag("SCRATCHER_TAG") || !GeneralHelper.IsItemEffectivelyOwned(item)) return (ushort)ShopResult.Unavailable;
        if(HandTouches(item) || view.Origins.Any(HandTouches)) return (ushort)ShopResult.Busy;
        if(command["slot"]?.Type!=JTokenType.Integer || (int)command["slot"] is <0 or >3) return (ushort)ShopResult.Unavailable;
        int slot=(int)command["slot"];
        // Slot requests commute; they never depend on a prior slot's reply.
        // A reordered start or repeated completed slot has no second effect.
        StartScratch(item);
        if(slot>0 && item.GetTagReadonly("SCRATCHER_STATE"+slot).GetInt()!=1) ScratcherHelper.MarkSymbolScratched(item,slot);
        nextCapture=0; return (ushort)ShopResult.Applied;
    }
    private void ReconcileScratch()
    {
        var ui=ScratcherUIManager.Instance;
        bool visible=localFrontends.TryGetValue(FrontendKind.Scratcher,out var view) && FrontendVisible(FrontendKind.Scratcher,view.Target);
        if(visible && !view.Predicted)
        {
            string face=ScratchFace(view.Target);
            if(!ReferenceEquals(scratchRenderedTarget,view.Target) || scratchRenderedFace!=face)
            {
                // A confirmed source reroll replaces the unconfirmed face.
                // Stable graphs never reset local mask pixels or flakes.
                ui.Reset(); ui.SetPrizeSprite(); ui.LoadScratch();
                scratchRenderedTarget=view.Target; scratchRenderedFace=face;
                for(int i=0;i<3;i++) scratchShown[i]=view.Target.GetTagReadonly("SCRATCHER_STATE"+(i+1)).GetInt()==1;
            }
            for(int i=0;i<3;i++) if(!scratchShown[i] && view.Target.GetTagReadonly("SCRATCHER_STATE"+(i+1)).GetInt()==1)
            { ui.scratchInputs[i].RevealAll(); scratchShown[i]=true; }
        }
        foreach(var pair in scratchRequests.ToArray())
        {
            var row=pair.Value; var attempt=row.Attempt;
            if(!attempt.Result.HasValue || attempt.Result==(ushort)ShopResult.Applied && attempt.Serial>frontendThrough) continue;
            scratchRequests.Remove(pair.Key);
            if(attempt.Result==(ushort)ShopResult.Applied || !visible || !ReferenceEquals(view,row.View) ||
                !ReferenceEquals(scratchRenderedTarget,view.Target)) continue;
            if(row.Slot==0)
            {
                ui.Reset(); ui.LoadScratch();
                for(int i=0;i<3;i++) scratchShown[i]=view.Target.GetTagReadonly("SCRATCHER_STATE"+(i+1)).GetInt()==1;
            }
            else if(view.Target.GetTagReadonly("SCRATCHER_STATE"+row.Slot).GetInt()!=1)
            { ui.scratchInputs[row.Slot-1].ResetScratch(); scratchShown[row.Slot-1]=false; }
        }
    }
    internal void RefreshHostScratch()
    {
        var ui=ScratcherUIManager.Instance; var item=ui?.currentActiveScratcherItem;
        if(item==null || !ui.ui.activeSelf || !item.IsTag("SCRATCHER_TAG")) return;
        string face=ScratchFace(item);
        if(ReferenceEquals(scratchRenderedTarget,item) && scratchRenderedFace!=face)
        { ui.Reset(); ui.SetPrizeSprite(); ui.LoadScratch(); }
        scratchRenderedTarget=item; scratchRenderedFace=face;
        // Another actor can finish this very ticket. Adopt only newly shared
        // reveals; the native source's own completed mask is already visible.
        for(int i=0;i<3;i++) if(item.GetTagReadonly("SCRATCHER_STATE"+(i+1)).GetInt()==1 &&
            !(bool)ScratchNativeRevealed.GetValue(ui.scratchInputs[i])) ui.scratchInputs[i].RevealAll();
    }
    [HarmonyPatch(typeof(ScratcherUIManager),nameof(ScratcherUIManager.OpenUI))]
    private static class RouteScratchOpen
    {
        private static bool Prefix(GameItem gameItem)
        {
            var world=RemoteUiExecution;
            if(world!=null)
            {
                ScratcherHelper.TryRerollForBetterLuck(gameItem);
                world.CaptureFrontend(FrontendKind.Scratcher,gameItem); return false;
            }
            return !NativeGuest.Active || Guest?.localFrontends.TryGetValue(FrontendKind.Scratcher,out var view)==true && ReferenceEquals(view.Target,gameItem);
        }
    }
    [HarmonyPatch(typeof(ScratcherHelper),nameof(ScratcherHelper.TryRerollForBetterLuck))]
    private static class ReplicaScratchRng
    { private static bool Prefix()=>!NativeGuest.Active; }
    [HarmonyPatch(typeof(ScratchInput),"Update")]
    private static class ReplicaScratchInputReady
    { private static bool Prefix()=>!NativeGuest.Active || Guest?.ScratchReady==true; }
    [HarmonyPatch(typeof(ScratchInput),nameof(ScratchInput.OnAnyScratch))]
    private static class RouteScratchStart
    {
        private static bool Prefix()
        { if(!NativeGuest.Active) return true; Guest?.RequestScratch(0); return false; }
    }
    [HarmonyPatch(typeof(ScratcherUIManager),nameof(ScratcherUIManager.OnScratchComplete))]
    private static class RouteScratchComplete
    {
        private static bool Prefix(ScratcherUIManager __instance,int slot)
        {
            if(!NativeGuest.Active) return Host==null || __instance.currentActiveScratcherItem==null ||
                slot is <1 or >3 || __instance.currentActiveScratcherItem.GetTagReadonly("SCRATCHER_STATE"+slot).GetInt()!=1;
            if(Guest?.RequestScratch(slot)!=true && slot is >=1 and <=3) __instance.scratchInputs[slot-1].ResetScratch();
            return false;
        }
    }
}
