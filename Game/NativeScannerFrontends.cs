using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Core;

namespace ProbablyCoop;

internal sealed partial class NativeWorld
{
    private readonly Dictionary<ulong,(FrontendKind Kind,LocalFrontendView View,ActivityRequests.Entry Attempt,string Action)> scannerRequests=
        new Dictionary<ulong,(FrontendKind,LocalFrontendView,ActivityRequests.Entry,string)>();
    private static ItemFeature ScannerFeature(GameItem item)=>item.IsTag("LIQUID_CONTAINER_TAG")
        ? WaterFeatureHelper.GetWaterFeature(item) : ChemicalProductHelper.GetChemicalFeature(item);
    private static string ScanText(GameItem item)=>string.Format("[{0}]: {1:F2}% ",
        LocHelper.GetLocalizedMechanic("mech_last_scan"),(double)WaterHelper.GetWaterPurity(item)/100.0)+WaterFeatureHelper.GetWaterDisplayName(WaterHelper.GetWaterPurity(item));

    internal bool RequestScanner(FrontendKind kind,GameItem item,string action)
    {
        if(item==null || !localFrontends.TryGetValue(kind,out var view) ||
            !ReferenceEquals(kind==FrontendKind.WaterStrip ? StripWater(view.Target) : view.Target,item) || !TryData(view.Target,out var data)) return false;
        string activity="frontend/"+(int)kind+"/"+view.Serial;
        if(action=="observe" && (uiRequests.PendingActivity(activity) ||
            item.GetTagReadonly("LAST_SCAN_RESULT_TAG").IsEnabled() && item.GetTagReadonly("LAST_SCAN_RESULT_TAG").GetString()==ScanText(item))) return false;
        var command=new JObject { ["kind"]="frontend",["type"]=(int)kind,["view"]=view.Serial,
            ["id"]=data["entity"],["action"]=action };
        if(action!="observe")
        {
            var feature=ScannerFeature(item); if(feature==null || !TryData(feature,out var featureData)) return false;
            command["token"]=data["moveToken"];
            command["feature"]=HashData(featureData);
            if(action=="confront") command["trade"]=tradeToken;
        }
        if(kind==FrontendKind.WaterStrip)
        {
            if(!TryData(item,out var waterData)) return false;
            command["water"]=waterData["entity"]; command["waterToken"]=waterData["moveToken"];
        }
        if(!Submit(command)) return false;
        scannerRequests[LastSubmittedUiRequest]=(kind,view,UiRequestState(LastSubmittedUiRequest),action);
        if(NativeBackgroundQa.Enabled && action!="observe") NativeBackgroundQa.RememberScannerCommand(command);
        if(NativeBackgroundQa.Enabled && kind==FrontendKind.WaterStrip) NativeBackgroundQa.RememberWaterStripCommand(command);
        return true;
    }
    private ushort ExecuteScanner(JObject command,FrontendKind kind)
    {
        if(!nativeFrontends.TryGetValue(kind,out var view) || view.Serial!=(ulong)command["view"] || !ValidFrontend(view) ||
            (uint)command["id"]!=model.Id(view.Target)) return (ushort)ShopResult.Stale;
        var target=kind==FrontendKind.WaterStrip ? StripWater(view.Target) : view.Target; string action=(string)command["action"];
        if(target==null || kind==FrontendKind.WaterStrip &&
            ((uint?)command["water"]!=model.Id(target) || (string)command["waterToken"]!=MoveToken(target))) return (ushort)ShopResult.Stale;
        if(HandTouches(target) || view.Origins.Any(HandTouches)) return (ushort)ShopResult.Busy;
        if(action=="observe")
        {
            // The original water panel records its latest read at refresh and
            // close. No background authority tick continues after local close.
            if(kind!=FrontendKind.WaterScanner || !target.IsTag("LIQUID_CONTAINER_TAG")) return (ushort)ShopResult.Unavailable;
            WaterHelper.SetLastScanResult(target);
        }
        else
        {
            if(!CurrentItem(command,out var current) || !ReferenceEquals(current,view.Target)) return (ushort)ShopResult.Stale;
            var feature=ScannerFeature(target);
            if(feature==null || (string)command["feature"]!=HashData(NativeMirrorProbe.FeatureData(feature,model))) return (ushort)ShopResult.Stale;
            bool water=target.IsTag("LIQUID_CONTAINER_TAG");
            if(action=="match")
            {
                if(water ? !(kind==FrontendKind.WaterStrip ? WaterStripUIManager.Instance.CanMatch(target) : WaterScannerUIManager.CanMatch(target)) : kind!=FrontendKind.ChemScanner ||
                    !target.IsTag("CHEM_ITEM_TAG") || !ChemScannerUIManager.Instance.CanMatchChem(target)) return (ushort)ShopResult.Unavailable;
                // The exact shared helper sequence from the native OnMatch.
                // Its local button/text/audio statements stay on each frontend.
                feature.ExposeFeature(lowkey:true);
                if(water) WaterFeatureHelper.OnWaterLabelUpdated(target); else ChemicalProductHelper.GradeProduct(target);
            }
            else if(action=="confront")
            {
                if((string)command["trade"]!=TradeToken()) return (ushort)ShopResult.Stale;
                if(water ? !(kind==FrontendKind.WaterStrip ? WaterStripUIManager.Instance.CanConfront(target) : WaterScannerUIManager.CanConfront(target)) : kind!=FrontendKind.ChemScanner ||
                    !target.IsTag("CHEM_ITEM_TAG") || !ChemScannerUIManager.CanConfrontChem(target)) return (ushort)ShopResult.Unavailable;
                if(!DialogUIManager.Instance.StartDialogue(feature.GetExposeDialog())) return (ushort)ShopResult.Busy;
                InspectableHelper.OnConfrontSuccess(target);
            }
            else return (ushort)ShopResult.Unavailable;
        }
        nextCapture=0; return (ushort)ShopResult.Applied;
    }
    private void ReconcileScanners()
    {
        foreach(var pair in scannerRequests.ToArray())
        {
            var row=pair.Value; var attempt=row.Attempt;
            if(!attempt.Result.HasValue || attempt.Result==(ushort)ShopResult.Applied && attempt.Serial>frontendThrough) continue;
            scannerRequests.Remove(pair.Key);
            if(row.Action=="observe") continue;
            if(!localFrontends.TryGetValue(row.Kind,out var view) || !ReferenceEquals(view,row.View) || !FrontendVisible(row.Kind,view.Target)) continue;
            if(row.Action=="match" && attempt.Result==(ushort)ShopResult.Applied) EmporiumAudioPlayer.Instance.audioLabeler.Play();
            if(row.Kind==FrontendKind.WaterStrip) RefreshStripLabels(StripWater(view.Target));
            else if(row.Kind==FrontendKind.WaterScanner) WaterScannerUIManager.Instance.RefreshUI();
            else ChemScannerUIManager.Instance.RefreshUI();
        }
    }
    [HarmonyPatch]
    private static class RouteScannerOpen
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(WaterScannerUIManager),nameof(WaterScannerUIManager.Open));
            yield return AccessTools.Method(typeof(ChemScannerUIManager),nameof(ChemScannerUIManager.Open));
        }
        private static bool Prefix(MethodBase __originalMethod,GameItem water)
        {
            var world=RemoteUiExecution; if(world==null) return true;
            bool isWater=__originalMethod.DeclaringType==typeof(WaterScannerUIManager);
            // Native RefreshUI.GetLabeledString also performs this lazy
            // initialization; keep it on the source before publishing a view.
            if(water!=null && water.IsTag("LIQUID_CONTAINER_TAG")) WaterFeatureHelper.GetWaterFeature(water);
            if(isWater) WaterHelper.SetLastScanResult(water);
            world.CaptureFrontend(isWater ? FrontendKind.WaterScanner : FrontendKind.ChemScanner,water);
            return false;
        }
    }
    [HarmonyPatch(typeof(WaterHelper),nameof(WaterHelper.SetLastScanResult))]
    private static class ReplicaScanObservation
    {
        private static bool Prefix(GameItem gameItem)
        {
            if(!NativeGuest.Active) return true;
            if(gameItem!=null && gameItem.IsTag("LIQUID_CONTAINER_TAG")) Guest?.RequestScanner(FrontendKind.WaterScanner,gameItem,"observe");
            return false;
        }
    }
    [HarmonyPatch]
    private static class RouteScannerCommit
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(WaterScannerUIManager),nameof(WaterScannerUIManager.OnMatch));
            yield return AccessTools.Method(typeof(WaterScannerUIManager),nameof(WaterScannerUIManager.OnConfront));
            yield return AccessTools.Method(typeof(ChemScannerUIManager),nameof(ChemScannerUIManager.OnMatch));
            yield return AccessTools.Method(typeof(ChemScannerUIManager),nameof(ChemScannerUIManager.OnConfront));
        }
        private static bool Prefix(object __instance,MethodBase __originalMethod)
        {
            if(!NativeGuest.Active) return true;
            var kind=__instance is WaterScannerUIManager ? FrontendKind.WaterScanner : FrontendKind.ChemScanner;
            var target=__instance is WaterScannerUIManager water ? water.currentWaterContainer : ((ChemScannerUIManager)__instance).currentScannedItem;
            Guest?.RequestScanner(kind,target,__originalMethod.Name=="OnMatch" ? "match" : "confront");
            return false;
        }
    }
}
