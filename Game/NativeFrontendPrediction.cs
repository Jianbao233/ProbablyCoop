using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Core;

namespace ProbablyCoop;

internal sealed partial class NativeWorld
{
    private const ulong AuthorityFrontend=1UL<<63;
    private ulong frontendClientNonce,frontendLastClientNonce,frontendExecutingNonce,frontendThrough;
    private FrontendKind frontendExecutingKind;
    private sealed class FrontendPrediction
    {
        internal FrontendKind Kind;
        internal LocalFrontendView View,Previous;
        internal bool PreviousVisible;
        internal ActivityRequests.Entry Attempt;
    }
    private readonly Dictionary<ulong,FrontendPrediction> frontendPredictions=new Dictionary<ulong,FrontendPrediction>();
    private readonly Dictionary<FrontendKind,LocalFrontendView> projectedFrontends=new Dictionary<FrontendKind,LocalFrontendView>();

    private static bool FrontendTrigger(JObject command,FrontendKind kind)=>kind switch {
        FrontendKind.ChemFinisher=>(string)command["kind"]=="control",
        FrontendKind.PlantAnalyser or FrontendKind.WaterScanner or FrontendKind.ChemScanner=>
            (string)command["kind"]=="item" && (string)command["action"]=="target",
        FrontendKind.Log or FrontendKind.Exchange or FrontendKind.WaterStrip or FrontendKind.Scratcher or FrontendKind.Hopper or FrontendKind.StorageCoordinator or FrontendKind.MindReprogrammer or FrontendKind.Appraisal=>(string)command["kind"]=="item" && (string)command["action"]=="activate",
        _=>false };
    private FrontendPrediction PlanFrontend(JObject command)
    {
        if(host || !Ready || !uiRequests.HasRoom || command["id"]?.Type!=JTokenType.Integer ||
            !model.Data.TryGetValue((uint)command["id"],out var origin)) return null;
        var kind=(FrontendKind)((int?)origin["frontendKind"] ?? 0);
        if(!FrontendTrigger(command,kind)) return null;
        uint id=kind==FrontendKind.ChemFinisher ? (uint)origin["owner"] :
            kind is FrontendKind.PlantAnalyser or FrontendKind.WaterScanner or FrontendKind.ChemScanner ? (uint)command["target"] : (uint)command["id"];
        if(!model.Objects.TryGetValue(id,out var value) || value is not GameItem target) return null;
        if(kind==FrontendKind.WaterStrip && StripWater(target)==null) return null;
        if(kind==FrontendKind.Scratcher && ScratcherHelper.CanRedeemPrize(target)) return null;
        // The original water helper lazily creates a missing feature. A guest
        // renders only projected features; let the validated host opening
        // materialize missing source data before this unusual case is shown.
        if(kind is FrontendKind.WaterScanner or FrontendKind.ChemScanner && target.IsTag("LIQUID_CONTAINER_TAG") &&
            !target.IsTag("WINE_CONTAINER_TAG") && target.FindItemFeatureByCategory("CATEGORY_WATER_PURITY")==null) return null;
        ulong nonce=checked(++frontendClientNonce);
        if(nonce>=AuthorityFrontend) throw new InvalidOperationException("Native frontend nonce exhausted");
        command["frontendKind"]=(int)kind; command["frontendNonce"]=nonce;
        localFrontends.TryGetValue(kind,out var previous);
        return new FrontendPrediction { Kind=kind,View=new LocalFrontendView { Serial=nonce,Target=target,Predicted=true },
            Previous=previous,PreviousVisible=previous!=null && FrontendVisible(kind,previous.Target) };
    }
    private bool AcceptFrontendPrediction(JObject command)
    {
        frontendExecutingNonce=0;
        if(command["frontendNonce"]==null) return true;
        if(command["frontendNonce"]?.Type!=JTokenType.Integer || !ulong.TryParse(command["frontendNonce"].ToString(),out ulong nonce) ||
            nonce==0 || nonce>=AuthorityFrontend || nonce<=frontendLastClientNonce ||
            command["frontendKind"]?.Type!=JTokenType.Integer || command["id"]?.Type!=JTokenType.Integer ||
            !model.Objects.TryGetValue((uint)command["id"],out var origin)) return false;
        int capability=NativeFrontendCapabilities.Kind(origin);
        if(capability==0 || capability!=(int)command["frontendKind"] || !FrontendTrigger(command,(FrontendKind)capability)) return false;
        frontendLastClientNonce=nonce; frontendExecutingNonce=nonce; frontendExecutingKind=(FrontendKind)capability;
        return true;
    }
    private void BeginFrontendPrediction(FrontendPrediction prediction,ulong issued)
    {
        if(prediction==null) return;
        prediction.Attempt=UiRequestState(issued);
        if(prediction.Previous!=null) CloseFrontend(prediction.Kind,prediction.Previous.Target);
        localFrontends[prediction.Kind]=prediction.View;
        frontendPredictions[issued]=prediction;
        OpenFrontend(prediction.Kind,prediction.View.Target);
    }
    private static bool FrontendVisible(FrontendKind kind,GameItem target)=>kind switch {
        FrontendKind.ChemFinisher=>ChemFinisherUIManager.Instance?.UIPanel.activeSelf==true && ReferenceEquals(ChemFinisherUIManager.Instance.currentItem,target),
        FrontendKind.PlantAnalyser=>PlantAnalyserUIManager.Instance?.UIPanel.activeSelf==true && ReferenceEquals(PlantAnalyserUIManager.Instance.currentItem,target),
        FrontendKind.Log=>LogUIManager.Instance?.UIPanel.activeSelf==true,
        FrontendKind.Exchange=>ExchangeUIManager.Instance?.panel.activeSelf==true,
        FrontendKind.WaterScanner=>WaterScannerUIManager.Instance?.panel.activeSelf==true && ReferenceEquals(WaterScannerUIManager.Instance.currentWaterContainer,target),
        FrontendKind.ChemScanner=>ChemScannerUIManager.Instance?.panel.activeSelf==true && ReferenceEquals(ChemScannerUIManager.Instance.currentScannedItem,target),
        FrontendKind.WaterStrip=>WaterStripUIManager.Instance?.panel.activeSelf==true && ReferenceEquals(WaterStripUIManager.Instance.currentWaterContainer,Guest?.StripWater(target)),
        FrontendKind.Scratcher=>ScratcherUIManager.Instance?.ui.activeSelf==true && ReferenceEquals(ScratcherUIManager.Instance.currentActiveScratcherItem,target),
        FrontendKind.Hopper or FrontendKind.StorageCoordinator or FrontendKind.MindReprogrammer=>NativeLogisticsUi.Visible(kind,target),
        FrontendKind.Appraisal=>Guest?.AppraisalFrontendVisible(target)==true,
        _=>false };
    private void ReconcileFrontends(ulong through)
    {
        frontendThrough=Math.Max(frontendThrough,through);
        foreach(var pair in frontendPredictions.ToArray())
        {
            var prediction=pair.Value; var attempt=prediction.Attempt;
            if(!attempt.Result.HasValue || attempt.Result==(ushort)ShopResult.Applied && attempt.Serial>frontendThrough) continue;
            frontendPredictions.Remove(pair.Key);
            if(!localFrontends.TryGetValue(prediction.Kind,out var current) || !ReferenceEquals(current,prediction.View)) continue;
            if(attempt.Result==(ushort)ShopResult.Applied && projectedFrontends.TryGetValue(prediction.Kind,out var actual) &&
                actual.Serial==current.Serial && ReferenceEquals(actual.Target,current.Target))
            { current.Predicted=false; continue; }
            CloseFrontend(prediction.Kind,current.Target); localFrontends.Remove(prediction.Kind);
            var previous=prediction.Previous;
            if(previous!=null && projectedFrontends.TryGetValue(prediction.Kind,out var restored) &&
                previous.Serial==restored.Serial && ReferenceEquals(previous.Target,restored.Target))
            {
                localFrontends[prediction.Kind]=restored;
                if(prediction.PreviousVisible) OpenFrontend(prediction.Kind,restored.Target);
            }
        }
        ReconcileScanners();
        ReconcileScratch();
        PumpDocumentReads();
        ReconcileEditors();
        ReconcileLogistics();
        ReconcileAppraisal();
    }
}
