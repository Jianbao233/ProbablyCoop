using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Core;

namespace ProbablyCoop;

// Native Unity modals are not PixelElement inventory windows. This boundary
// delivers a fixed API kind and entity identity to the initiating actor. It
// never transfers delegates or uses a network-supplied type/method name.
internal sealed partial class NativeWorld
{
    internal enum FrontendKind { ChemFinisher=1, PlantAnalyser, Log, Exchange, WaterScanner, ChemScanner, WaterStrip, Scratcher, Hopper, StorageCoordinator, MindReprogrammer, Appraisal }
    private sealed class FrontendView
    {
        internal ulong Serial,Context;
        internal GameItem Target;
        internal GameItem[] Origins;
    }
    private sealed class LocalFrontendView
    { internal ulong Serial; internal GameItem Target; internal bool Predicted; }
    private ulong frontendSerial;
    private readonly Dictionary<FrontendKind,FrontendView> nativeFrontends=new Dictionary<FrontendKind,FrontendView>();
    private readonly Dictionary<FrontendKind,LocalFrontendView> localFrontends=new Dictionary<FrontendKind,LocalFrontendView>();
    internal bool HasFrontendBindings=>host ? nativeFrontends.Count!=0 : localFrontends.Count!=0;

    private void CaptureFrontend(FrontendKind kind,GameItem target)
    {
        if(!host || !ReferenceEquals(RemoteUiExecution,this) || target==null)
            throw new InvalidOperationException("Native frontend requires a validated authority callback");
        ulong serial=frontendExecutingNonce!=0 && frontendExecutingKind==kind ? frontendExecutingNonce : AuthorityFrontend|checked(++frontendSerial);
        nativeFrontends[kind]=new FrontendView { Serial=serial,Context=ObserveOperationContext(),
            Target=target,Origins=remoteChoiceOrigins.ToArray() };
        nextCapture=0;
    }
    private bool ValidFrontend(FrontendView view)=>view.Context==ObserveOperationContext() &&
        view.Target.parentInventory!=null && view.Target.unitCount>0 && Accessible(view.Target.parentInventory) &&
        model.Objects.TryGetValue(model.Id(view.Target),out var target) && ReferenceEquals(target,view.Target) &&
        view.Origins.All(origin=>origin.parentInventory!=null && origin.unitCount>0 && Accessible(origin.parentInventory));
    private JArray CaptureFrontends()
    {
        RefreshHostScratch();
        foreach(var pair in nativeFrontends.ToArray())
            if(!ValidFrontend(pair.Value) || pair.Key==FrontendKind.WaterStrip && StripWater(pair.Value.Target)==null) nativeFrontends.Remove(pair.Key);
        return new JArray(nativeFrontends.OrderBy(pair=>(int)pair.Key).Select(pair=>new JObject {
            ["type"]=(int)pair.Key,["serial"]=pair.Value.Serial,["context"]=pair.Value.Context,["id"]=model.Id(pair.Value.Target),
            ["logistics"]=CaptureLogisticsPresentation(pair.Key,pair.Value.Target) }));
    }
    private void ApplyFrontends(JArray data)
    {
        if(data==null || data.Count>16) throw new InvalidDataException("Invalid native frontend collection");
        var present=new HashSet<FrontendKind>();
        projectedFrontends.Clear();
        foreach(JObject entry in data)
        {
            var kind=(FrontendKind)(int)entry["type"];
            ulong serial=(ulong)entry["serial"];
            if(!Enum.IsDefined(typeof(FrontendKind),kind) || !present.Add(kind) || serial==0 ||
                (ulong)entry["context"]!=observedOperationContext ||
                !model.Objects.TryGetValue((uint)entry["id"],out var value) || value is not GameItem target)
                throw new InvalidDataException("Invalid native frontend binding");
            projectedFrontends[kind]=new LocalFrontendView { Serial=serial,Target=target };
            ApplyLogisticsPresentation(kind,target,entry["logistics"] as JObject);
            if(localFrontends.TryGetValue(kind,out var previous))
            {
                if(previous.Serial==serial && ReferenceEquals(previous.Target,target)) { previous.Predicted=false; continue; }
                if(previous.Predicted) continue; // Older in-flight graphs do not erase a newer local opening.
            }
            if(previous!=null) CloseFrontend(kind,previous.Target);
            localFrontends[kind]=new LocalFrontendView { Serial=serial,Target=target };
            OpenFrontend(kind,target);
        }
        foreach(var pair in localFrontends.ToArray())
            if(!present.Contains(pair.Key) && !pair.Value.Predicted) { CloseFrontend(pair.Key,pair.Value.Target); localFrontends.Remove(pair.Key); }
    }
    private static void OpenFrontend(FrontendKind kind,GameItem target)
    {
        switch(kind)
        {
            case FrontendKind.ChemFinisher: ChemFinisherUIManager.Instance.OpenUI(target); break;
            case FrontendKind.PlantAnalyser: PlantAnalyserUIManager.Instance.OpenUI(target); break;
            case FrontendKind.Log: LogUIManager.Instance.OpenUI(target); break;
            case FrontendKind.Exchange: ExchangeUIManager.Instance.OpenPanel(target); break;
            case FrontendKind.WaterScanner: WaterScannerUIManager.Instance.Open(null,target); break;
            case FrontendKind.ChemScanner: ChemScannerUIManager.Instance.Open(null,target); break;
            case FrontendKind.WaterStrip: Guest?.OpenStrip(target); break;
            case FrontendKind.Scratcher: Guest?.OpenScratch(target); break;
            case FrontendKind.Hopper: HopperHelper.OpenWindow(target); break;
            case FrontendKind.StorageCoordinator: MachineStorageCoordinator.OpenWindow(target); break;
            case FrontendKind.MindReprogrammer: MindReprogrammerHelper.OpenWindow(target); break;
            case FrontendKind.Appraisal: Guest?.OpenAppraisalFrontend(target); break;
        }
    }
    private static void CloseFrontend(FrontendKind kind,GameItem target)
    {
        if(kind==FrontendKind.Appraisal) { Guest?.CloseAppraisalFrontend(target); return; }
        if(IsLogisticsFrontend(kind)) { NativeLogisticsUi.Close(kind,target); return; }
        switch(kind)
        {
            case FrontendKind.ChemFinisher:
                var chem=ChemFinisherUIManager.Instance;
                if(chem!=null && ReferenceEquals(chem.currentItem,target))
                { if(chem.UIPanel.activeSelf) chem.HideUI(); chem.currentItem=null; }
                break;
            case FrontendKind.PlantAnalyser:
                var plant=PlantAnalyserUIManager.Instance;
                if(plant!=null && ReferenceEquals(plant.currentItem,target))
                { if(plant.UIPanel.activeSelf) plant.HideUI(); plant.currentItem=null; }
                break;
            case FrontendKind.Log:
                if(LogUIManager.Instance?.UIPanel.activeSelf==true) LogUIManager.Instance.CloseUI();
                break;
            case FrontendKind.Exchange:
                if(ExchangeUIManager.Instance?.panel.activeSelf==true) ExchangeUIManager.Instance.ClosePanel();
                break;
            case FrontendKind.WaterScanner:
                var water=WaterScannerUIManager.Instance;
                if(water!=null && ReferenceEquals(water.currentWaterContainer,target))
                { water.panel.SetActive(false); if(water.coroutine!=null) water.StopCoroutine(water.coroutine); water.currentWaterContainer=null; }
                break;
            case FrontendKind.ChemScanner:
                var scanner=ChemScannerUIManager.Instance;
                if(scanner!=null && ReferenceEquals(scanner.currentScannedItem,target))
                { scanner.panel.SetActive(false); if(scanner.coroutine!=null) scanner.StopCoroutine(scanner.coroutine); scanner.currentScannedItem=null; }
                break;
            case FrontendKind.WaterStrip:
                var strip=WaterStripUIManager.Instance;
                if(strip!=null) { if(strip.panel.activeSelf) strip.Close(); strip.currentWaterContainer=null; }
                break;
            case FrontendKind.Scratcher:
                var scratch=ScratcherUIManager.Instance;
                if(scratch!=null && ReferenceEquals(scratch.currentActiveScratcherItem,target))
                { if(scratch.ui.activeSelf) scratch.CloseUI(); scratch.currentActiveScratcherItem=null; }
                break;
        }
    }
    private void ClearFrontends()
    {
        if(!host) ClearEditors();
        if(!host) foreach(var pair in localFrontends) CloseFrontend(pair.Key,pair.Value.Target);
        localFrontends.Clear(); nativeFrontends.Clear();
        projectedFrontends.Clear(); frontendPredictions.Clear(); frontendThrough=0;
        scannerRequests.Clear();
        documentReads.Clear(); documentReadAttempt=null; documentReadToken="";
        ClearLogistics();
        ClearAppraisal();
        scratchRequests.Clear(); scratchRenderedTarget=null; scratchRenderedFace=null; Array.Clear(scratchShown,0,scratchShown.Length);
        if(host) frontendLastClientNonce=0;
    }
    private bool CloseKnownFrontend(GameItem item,string action)
    {
        if(action=="activate") foreach(var pair in localFrontends)
            if(IsLogisticsFrontend(pair.Key) && ReferenceEquals(pair.Value.Target,item) && NativeLogisticsUi.Visible(pair.Key,item))
            { NativeLogisticsUi.Close(pair.Key,item); return true; }
        if(action!="activate" || !localFrontends.TryGetValue(FrontendKind.Log,out var log) ||
            !ReferenceEquals(log.Target,item) || LogUIManager.Instance?.UIPanel.activeSelf!=true) return false;
        LogUIManager.Instance.CloseUI(); return true;
    }
    internal bool RequestFrontendRecipe(GameItem item,string recipe)
    {
        if(!localFrontends.TryGetValue(FrontendKind.ChemFinisher,out var view) || !ReferenceEquals(view.Target,item) ||
            !TryData(item,out var data)) return false;
        var command=new JObject { ["kind"]="frontend",["type"]=(int)FrontendKind.ChemFinisher,["view"]=view.Serial,
            ["id"]=(uint)data["entity"],["token"]=data["moveToken"],["recipe"]=recipe };
        bool accepted=Submit(command);
        if(accepted && NativeBackgroundQa.Enabled) NativeBackgroundQa.RememberFrontendCommand(command);
        return accepted;
    }
    private ushort ExecuteFrontend(JObject command)
    {
        var kind=(FrontendKind)(int)command["type"];
        if(shop.Settling) return (ushort)ShopResult.Busy;
        if(IsLogisticsFrontend(kind)) return ExecuteLogistics(command,kind);
        if(kind==FrontendKind.Appraisal) return ExecuteAppraisal(command);
        if(kind==FrontendKind.Scratcher) return ExecuteScratch(command);
        if(kind is FrontendKind.WaterScanner or FrontendKind.ChemScanner or FrontendKind.WaterStrip) return ExecuteScanner(command,kind);
        if(kind!=FrontendKind.ChemFinisher || !nativeFrontends.TryGetValue(kind,out var view) ||
            view.Serial!=(ulong)command["view"] || !ValidFrontend(view) || !CurrentItem(command,out var item) ||
            !ReferenceEquals(item,view.Target)) return (ushort)ShopResult.Stale;
        if(!GeneralHelper.IsItemEffectivelyOwned(item)) return (ushort)ShopResult.Unavailable;
        if(HandTouches(item)) return (ushort)ShopResult.Busy;
        string recipe=(string)command["recipe"];
        if(!ChemFinisherRecipe.chemFinisherRecipes.Any(entry=>entry.identifier==recipe)) return (ushort)ShopResult.Unavailable;
        // This original callback only edits the bound machine's recipe tag.
        // Borrow its data field synchronously, preserving the host's own panel.
        var ui=ChemFinisherUIManager.Instance; var previous=ui.currentItem;
        try { ui.currentItem=item; ui.OnChemRecipeSelected(recipe); }
        finally { ui.currentItem=previous; }
        nextCapture=0; return (ushort)ShopResult.Applied;
    }

    [HarmonyPatch]
    private static class RouteNativeFrontendOpen
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(ChemFinisherUIManager),nameof(ChemFinisherUIManager.OpenUI));
            yield return AccessTools.Method(typeof(PlantAnalyserUIManager),nameof(PlantAnalyserUIManager.OpenUI));
            yield return AccessTools.Method(typeof(LogUIManager),nameof(LogUIManager.OpenUI));
            yield return AccessTools.Method(typeof(LogUIManager),nameof(LogUIManager.Toggle));
            yield return AccessTools.Method(typeof(ExchangeUIManager),nameof(ExchangeUIManager.OpenPanel));
            yield return AccessTools.Method(typeof(HopperHelper),nameof(HopperHelper.OpenWindow));
            yield return AccessTools.Method(typeof(MachineStorageCoordinator),nameof(MachineStorageCoordinator.OpenWindow));
            yield return AccessTools.Method(typeof(MindReprogrammerHelper),nameof(MindReprogrammerHelper.OpenWindow));
        }
        private static bool Prefix(MethodBase __originalMethod,GameItem __0)
        {
            var world=RemoteUiExecution;
            if(world==null) return true;
            FrontendKind kind=__originalMethod.DeclaringType==typeof(ChemFinisherUIManager) ? FrontendKind.ChemFinisher :
                __originalMethod.DeclaringType==typeof(HopperHelper) ? FrontendKind.Hopper :
                __originalMethod.DeclaringType==typeof(MachineStorageCoordinator) ? FrontendKind.StorageCoordinator :
                __originalMethod.DeclaringType==typeof(MindReprogrammerHelper) ? FrontendKind.MindReprogrammer :
                __originalMethod.DeclaringType==typeof(PlantAnalyserUIManager) ? FrontendKind.PlantAnalyser :
                __originalMethod.DeclaringType==typeof(ExchangeUIManager) ? FrontendKind.Exchange : FrontendKind.Log;
            // Native log opening also progresses a shared tutorial stage. That
            // part remains on the authority; no host window is shown/replaced.
            if(kind==FrontendKind.Log) TutorialUIManager.Instance.OnRecorderOpen();
            world.CaptureFrontend(kind,__0); return false;
        }
    }
    [HarmonyPatch(typeof(ChemFinisherUIManager),nameof(ChemFinisherUIManager.OnChemRecipeSelected))]
    private static class RouteNativeRecipeCommit
    {
        private static bool Prefix(ChemFinisherUIManager __instance,string identifier)
        {
            if(!NativeGuest.Active) return true;
            NativeWorld.Guest?.RequestFrontendRecipe(__instance.currentItem,identifier); return false;
        }
    }
    [HarmonyPatch(typeof(TutorialUIManager),nameof(TutorialUIManager.OnRecorderOpen))]
    private static class ReplicaRecorderTutorial
    { private static bool Prefix()=>!NativeGuest.Active; }
}
