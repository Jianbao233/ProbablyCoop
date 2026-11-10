using System;
using System.Linq;
using System.Text;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Core;

namespace ProbablyCoop;

internal sealed partial class NativeBackgroundQa
{
    private const string ScannerConfrontFixture="QA_SCANNER_CONFRONT_FIXTURE",ScannerConfrontDone="QA_SCANNER_CONFRONT_DONE",ScannerConfrontRole="qaScannerConfrontDone";
    private GameItem[] scannerConfrontItems;
    private GameItem scannerConfrontWater,scannerConfrontChem,scannerConfrontWaterTool,scannerConfrontChemTool;
    private GameItem scannerConfrontStrip,scannerConfrontStripWater;
    private bool scannerConfrontSeeded,scannerConfrontHostDone;
    private int scannerConfrontStep,scannerConfrontOpeningAdvances;
    private double scannerConfrontUntil,scannerConfrontNext;
    private JObject scannerConfrontAfter;
    private ulong scannerConfrontReplay;
    private static byte[] firstScannerConfrontCommand;
    private void CheckHostScannerConfront()
    {
        if(scannerConfrontHostDone || !scannerHostDone || hostDayStarted || !PlayerStore.Instance.isClientArrived) return;
        if(!scannerConfrontSeeded)
        {
            scannerConfrontWater=SeedFrontendItem("small_bottled_water","QA_SCANNER_CONFRONT_WATER");
            scannerConfrontChem=SeedFrontendItem("birth_control_pill","QA_SCANNER_CONFRONT_CHEM");
            scannerConfrontWaterTool=SeedFrontendItem("aquascan","QA_SCANNER_CONFRONT_WATER_TOOL");
            scannerConfrontChemTool=SeedFrontendItem("chem_scanner","QA_SCANNER_CONFRONT_CHEM_TOOL");
            scannerConfrontStrip=SeedFrontendItem("water_test_strip","QA_STRIP_CONFRONT_TOOL");
            scannerConfrontStripWater=SeedFrontendItem("small_bottled_water","QA_STRIP_CONFRONT_WATER");
            WaterHelper.FillWithPureWater(scannerConfrontWater); WaterFeatureHelper.SetPresumedPurity(scannerConfrontWater,5000);
            // This rejection oracle compares every scalar across a real RTT.
            // The native ambient tick asymptotes to 26 C independently of any
            // command. Start the disposable water at thermal equilibrium so
            // that clock-driven cooling cannot masquerade as a replay effect.
            scannerConfrontWater.ModifyTag("CONTAINER_TEMP_TAG",tag=>tag.Enable().SetDouble(26.0),skipValidate:true);
            WaterHelper.FillWithPureWater(scannerConfrontStripWater); WaterFeatureHelper.SetPresumedPurity(scannerConfrontStripWater,5000);
            var feature=ChemicalProductHelper.GetChemicalFeature(scannerConfrontChem);
            ChemicalProductHelper.SetPurityFeature(scannerConfrontChem,feature.realCondition,
                feature.realCondition.identifier=="lab_pure" ? ItemConditionList.CreateStarchGrade() : ItemConditionList.CreateLabPure());
            GeneralHelper.SetItemOwned(scannerConfrontWater,false); GeneralHelper.SetItemOwned(scannerConfrontChem,false);
            GeneralHelper.SetItemOwned(scannerConfrontStripWater,false);
            scannerConfrontItems=new[]{scannerConfrontWater,scannerConfrontChem,scannerConfrontWaterTool,scannerConfrontChemTool,scannerConfrontStrip,scannerConfrontStripWater};
            foreach(var item in scannerConfrontItems) {item.DisableTag(FrontendFixture); item.EnableTag(ScannerConfrontFixture);}
            scannerConfrontSeeded=true;
            return;
        }
        if(!qaMachine.IsTag(ScannerConfrontDone)) return;
        Require(DialogUIManager.Instance.currentDialog==null && scannerConfrontWater.IsTag("ALREADY_CONFRONTED_TAG") &&
            scannerConfrontChem.IsTag("ALREADY_CONFRONTED_TAG") && scannerConfrontStripWater.IsTag("ALREADY_CONFRONTED_TAG"),"original shared scanner dialogues did not finish");
        foreach(var item in scannerConfrontItems) item.Destroy();
        scannerConfrontHostDone=true;
        Record("PASS host-native-scanner-confront","real arrived customer, original water/chemical expose dialogues and shared effects; guest controls native dialogue advance; fixtures retired before sale/day save");
        Record("PASS host-native-strip-confront","native strip consumption and original water expose dialogue against the arrived customer; confirmed feature and associated water only; fixture retired");
    }
    private bool RunScannerConfront(ReplicaLink link,double now)
    {
        if(scannerConfrontStep==99) return true;
        if(scannerConfrontUntil==0) scannerConfrontUntil=now+70;
        Require(now<scannerConfrontUntil,"scanner confrontation timed out at "+scannerConfrontStep);
        if(QaWaiting(link) || now<scannerConfrontNext) return false;
        var world=NativeWorld.Guest; var water=WaterScannerUIManager.Instance; var chem=ChemScannerUIManager.Instance;
        switch(scannerConfrontStep)
        {
            case 0:
                if(!world.Objects.Values.OfType<GameItem>().Any(item=>item.IsTag("QA_SCANNER_CONFRONT_WATER"))) return false;
                // A newly created cooperative run may still have the native
                // customer's opening/tutorial conversation. Finish it through
                // the same original advance input as the later expose lines;
                // never bypass StartDialogue's priority lock or clear a line.
                if(world.PendingMainDialogue) return false;
                if(world.HasDialogue)
                {
                    DialogUIManager.Instance.OnAdvancePressed();
                    scannerConfrontOpeningAdvances++; scannerConfrontNext=now+0.25;
                    return false;
                }
                if(scannerConfrontOpeningAdvances>0)
                    Record("PASS native-scanner-opening-dialogue","existing customer opening advanced through original input before scanner use; advances="+scannerConfrontOpeningAdvances);
                scannerConfrontWater=FrontendItem("QA_SCANNER_CONFRONT_WATER"); scannerConfrontChem=FrontendItem("QA_SCANNER_CONFRONT_CHEM");
                scannerConfrontWaterTool=FrontendItem("QA_SCANNER_CONFRONT_WATER_TOOL"); scannerConfrontChemTool=FrontendItem("QA_SCANNER_CONFRONT_CHEM_TOOL");
                scannerConfrontStrip=FrontendItem("QA_STRIP_CONFRONT_TOOL"); scannerConfrontStripWater=FrontendItem("QA_STRIP_CONFRONT_WATER");
                ItemSelectHandler.current.TrySelectItem(scannerConfrontWaterTool); scannerConfrontStep=1; return false;
            case 1:
                if(!scannerConfrontWaterTool.MayTarget(scannerConfrontWater) || !scannerConfrontWaterTool.CanTarget(scannerConfrontWater)) return false;
                OriginalScannerTarget(scannerConfrontWaterTool,scannerConfrontWater); scannerConfrontStep=2; return false;
            case 2:
                Require(water.panel.activeSelf && water.confrontButton.gameObject.activeSelf && WaterScannerUIManager.CanConfront(scannerConfrontWater),"original water scanner confront unavailable for native customer");
                water.OnConfront();
                Require(QaWaiting(link) && !scannerConfrontWater.IsTag("ALREADY_CONFRONTED_TAG"),"water confrontation ran shared effect on guest or emitted no request");
                scannerConfrontStep=3; return false;
            case 3:
                if(!scannerConfrontWater.IsTag("ALREADY_CONFRONTED_TAG") || !WaterFeatureHelper.GetWaterFeature(scannerConfrontWater).IsExposed()) return false;
                Require(world.HasDialogue && firstScannerConfrontCommand!=null,"original water expose dialogue was not projected");
                NativeOffscreenCapture.Capture("qa-native-scanner-confront");
                scannerConfrontAfter=NativeMirrorProbe.CaptureValues(scannerConfrontWater);
                var old=JObject.Parse(Encoding.UTF8.GetString(firstScannerConfrontCommand));
                world.TryData(scannerConfrontWater,out var data); old["token"]=data["moveToken"];
                old["trade"]=(string)AccessTools.Field(typeof(NativeWorld),"tradeToken").GetValue(world);
                Require(link.Submit(Encoding.UTF8.GetBytes(old.ToString(Formatting.None)),now),"cannot replay old scanner expose feature proof");
                scannerConfrontReplay=link.PendingRequest;
                scannerConfrontStep=4; return false;
            case 4:
                var after=NativeMirrorProbe.CaptureValues(scannerConfrontWater);
                var oldFields=scannerConfrontAfter.Descendants().OfType<JValue>().ToDictionary(value=>value.Path,value=>(JToken)value);
                var newFields=after.Descendants().OfType<JValue>().ToDictionary(value=>value.Path,value=>(JToken)value);
                var changes=oldFields.Keys.Union(newFields.Keys).Where(key=>!JToken.DeepEquals(
                    oldFields.TryGetValue(key,out var left) ? left : null,newFields.TryGetValue(key,out var right) ? right : null)).ToArray();
                Record("TRACE scanner-confront-replay","request="+scannerConfrontReplay+" completed="+link.CompletedRequest+" exactResult="+link.Result+
                    " globalResult="+QaResult(link)+" changed="+string.Join(",",changes));
                Require(link.CompletedRequest==scannerConfrontReplay && link.Result==(ushort)ShopResult.Stale && JToken.DeepEquals(scannerConfrontAfter,after),"obsolete scanner confront repeated shared effects");
                scannerConfrontStep=5; return false;
            case 5:
                if(world.HasDialogue) { DialogUIManager.Instance.OnAdvancePressed(); scannerConfrontNext=now+0.25; return false; }
                water.Close(); ItemSelectHandler.current.TrySelectItem(scannerConfrontChemTool); scannerConfrontStep=6; return false;
            case 6:
                if(!scannerConfrontChemTool.MayTarget(scannerConfrontChem) || !scannerConfrontChemTool.CanTarget(scannerConfrontChem)) return false;
                OriginalScannerTarget(scannerConfrontChemTool,scannerConfrontChem); scannerConfrontStep=7; return false;
            case 7:
                Require(chem.panel.activeSelf && chem.confrontButton.gameObject.activeSelf && ChemScannerUIManager.CanConfrontChem(scannerConfrontChem),"native chemical scanner confront unavailable for arrived customer");
                chem.OnConfront(); scannerConfrontStep=8; return false;
            case 8:
                if(!scannerConfrontChem.IsTag("ALREADY_CONFRONTED_TAG") || !ChemicalProductHelper.GetChemicalFeature(scannerConfrontChem).IsExposed()) return false;
                scannerConfrontStep=9; return false;
            case 9:
                if(world.HasDialogue) { DialogUIManager.Instance.OnAdvancePressed(); scannerConfrontNext=now+0.25; return false; }
                chem.Close(); ItemSelectHandler.current.TrySelectItem(scannerConfrontStrip); scannerConfrontStep=11; return false;
            case 11:
                if(!scannerConfrontStrip.MayTarget(scannerConfrontStripWater) || !scannerConfrontStrip.CanTarget(scannerConfrontStripWater)) return false;
                OriginalScannerTarget(scannerConfrontStrip,scannerConfrontStripWater); scannerConfrontStep=12; return false;
            case 12:
                var stripUi=WaterStripUIManager.Instance;
                Require(stripUi.panel.activeSelf && ReferenceEquals(stripUi.currentWaterContainer,scannerConfrontStripWater) &&
                    stripUi.confrontButton.gameObject.activeSelf && stripUi.CanConfront(scannerConfrontStripWater),"native strip confront unavailable for real customer water");
                stripUi.OnConfront(); scannerConfrontStep=13; return false;
            case 13:
                if(!scannerConfrontStripWater.IsTag("ALREADY_CONFRONTED_TAG") || !WaterFeatureHelper.GetWaterFeature(scannerConfrontStripWater).IsExposed()) return false;
                Require(world.HasDialogue,"strip original expose dialogue was not shared");
                NativeOffscreenCapture.Capture("qa-native-strip-confront");
                scannerConfrontStep=14; return false;
            case 14:
                if(world.HasDialogue) { DialogUIManager.Instance.OnAdvancePressed(); scannerConfrontNext=now+0.25; return false; }
                WaterStripUIManager.Instance.Close();
                var button=(MachineryButton)((MachineryPanelRow)MachineReplica().contentWindow.childElement).Panels.Single(panel=>panel.role==ScannerConfrontRole).content;
                AccessTools.Method(typeof(MachineryButton),"OnSelect").Invoke(button,null);
                scannerConfrontStep=10; return false;
            case 10:
                if(world.Objects.Values.OfType<GameItem>().Any(item=>item.IsTag(ScannerConfrontFixture))) return false;
                Require(!water.panel.activeSelf && !chem.panel.activeSelf && water.currentWaterContainer==null && chem.currentScannedItem==null &&
                    !WaterStripUIManager.Instance.panel.activeSelf && WaterStripUIManager.Instance.currentWaterContainer==null,"scanner confrontation target retirement left bound native windows");
                Record("PASS native-scanner-confront","original scanner buttons expose water/chemical features through real shared customer dialogues; original dialogue advancement; obsolete proof rejected without duplicate effects; target cleanup");
                Record("PASS native-strip-confront","original strip target/test and confront button against real customer water; source associated water validated; original shared expose dialogue, guest advancement and lifetime cleanup");
                scannerConfrontStep=99; return true;
        }
        return false;
    }
}
