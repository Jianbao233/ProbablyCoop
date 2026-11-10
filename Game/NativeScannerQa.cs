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
    private GameItem scannerWater,scannerOtherWater,scannerHostWater,scannerChemical,scannerWaterTool,scannerChemTool;
    private GameItem[] scannerItems;
    private const string ScannerFixture="QA_SCANNER_FIXTURE",ScannerDone="QA_SCANNER_DONE",ScannerRole="qaScannerDone";
    private bool scannerHostDone;
    private const string ScannerWaterChangeRole="qaScannerWaterChange";
    private int scannerWaterChanges;
    private int scannerStep;
    private double scannerNext;
    private string scannerLabelBefore;
    private static byte[] firstScannerCommand;
    internal static void RememberScannerCommand(JObject command)
    {
        var bytes=Encoding.UTF8.GetBytes(command.ToString(Formatting.None));
        if((string)command["action"]=="confront") firstScannerConfrontCommand??=bytes;
        else firstScannerCommand??=bytes;
    }
    private void SeedScannerFrontends()
    {
        scannerWater=SeedFrontendItem("small_bottled_water","QA_SCANNER_WATER");
        scannerOtherWater=SeedFrontendItem("small_bottled_water","QA_SCANNER_OTHER_WATER");
        scannerHostWater=SeedFrontendItem("small_bottled_water","QA_SCANNER_HOST_WATER");
        scannerChemical=SeedFrontendItem("birth_control_pill","QA_SCANNER_CHEMICAL");
        scannerWaterTool=SeedFrontendItem("aquascan","QA_SCANNER_WATER_TOOL");
        scannerChemTool=SeedFrontendItem("chem_scanner","QA_SCANNER_CHEM_TOOL");
        foreach(var water in new[]{scannerWater,scannerOtherWater,scannerHostWater}) WaterHelper.FillWithPureWater(water);
        WaterFeatureHelper.SetPresumedPurity(scannerWater,5000);
        WaterFeatureHelper.SetPresumedPurity(scannerOtherWater,5000);
        var feature=ChemicalProductHelper.GetChemicalFeature(scannerChemical);
        ChemicalProductHelper.SetPurityFeature(scannerChemical,feature.realCondition,
            feature.realCondition.identifier=="lab_pure" ? ItemConditionList.CreateStarchGrade() : ItemConditionList.CreateLabPure());
        scannerItems=new[]{scannerWater,scannerOtherWater,scannerHostWater,scannerChemical,scannerWaterTool,scannerChemTool};
        foreach(var item in scannerItems) { item.DisableTag(FrontendFixture); item.EnableTag(ScannerFixture); }
    }
    private void OpenHostScannerFrontends()
    {
        var water=WaterScannerUIManager.Instance; var chem=ChemScannerUIManager.Instance;
        water.Open(scannerWaterTool,scannerWater);
        qaMachine.ModifyTag("QA_SCANNER_WATER_TEXT",tag=>tag.Enable().SetString(ScannerWaterText()));
        water.Close(); WaterHelper.ResetLastScanResult(scannerWater);
        chem.Open(scannerChemTool,scannerChemical);
        qaMachine.ModifyTag("QA_SCANNER_CHEM_TEXT",tag=>tag.Enable().SetString(ScannerChemText()));
        chem.Close();
        water.Open(scannerWaterTool,scannerHostWater); chem.Open(scannerChemTool,scannerHostWater);
    }
    private static string ScannerWaterText()=>string.Join("|",WaterScannerUIManager.Instance.testedTMP.text,
        WaterScannerUIManager.Instance.labelTMP.text,WaterScannerUIManager.Instance.contaminantTMP.text);
    private static string ScannerChemText()
    {
        var ui=ChemScannerUIManager.Instance;
        return string.Join("|",ui.productNameTMP.text,ui.testedTMP.text,ui.labelTMP.text,ui.contaminantTMP.text,ui.totalVolumeTMP.text,
            ui.euphoriaEffect.gameObject.activeSelf,ui.toxicityEffect.gameObject.activeSelf,ui.synaprestEffect.gameObject.activeSelf,ui.birthControlEffect.gameObject.activeSelf);
    }
    private void CheckHostScannerFrontends()=>Require(WaterScannerUIManager.Instance.panel.activeSelf &&
        ReferenceEquals(WaterScannerUIManager.Instance.currentWaterContainer,scannerHostWater) && ChemScannerUIManager.Instance.panel.activeSelf &&
        ReferenceEquals(ChemScannerUIManager.Instance.currentScannedItem,scannerHostWater),"guest scanner changed the host's own original panel/target");
    private void CloseHostScannerFrontends()
    {
        WaterScannerUIManager.Instance.Close(); ChemScannerUIManager.Instance.Close();
        foreach(var item in scannerItems) item.Destroy();
    }
    private void CheckHostScannerLifecycle()
    {
        if(scannerHostDone) return;
        CheckHostScannerFrontends();
        if(!qaMachine.IsTag(ScannerDone)) return;
        CloseHostScannerFrontends(); scannerHostDone=true;
        SeedStripFrontends();
        Record("PASS host-native-scanners","host original water/chemical panels retained independent targets while guest scanned/matched; fixtures removed before business/day saves");
    }
    private void MutateScannerWater()
    {
        Require(scannerWater?.parentInventory!=null && scannerWaterChanges<2,"scanner dynamic fixture is unavailable");
        WaterHelper.EmptyContainer(scannerWater);
        if(++scannerWaterChanges==1) WaterHelper.FillWithRustWater(scannerWater); else WaterHelper.FillWithPureWater(scannerWater);
    }
    private void OriginalScannerWaterChange()
    {
        var button=(MachineryButton)((MachineryPanelRow)MachineReplica().contentWindow.childElement).Panels.Single(panel=>panel.role==ScannerWaterChangeRole).content;
        AccessTools.Method(typeof(MachineryButton),"OnSelect").Invoke(button,null);
    }
    private static void OriginalScannerTarget(GameItem tool,GameItem target)
    {
        Require(tool.MayTarget(target) && tool.CanTarget(target),"native scanner original target predicates refused fixture");
        tool.Target(target); ItemSelectHandler.current.EndSelect();
    }
    private bool RunScannerFrontends(ReplicaLink link,double now)
    {
        if(scannerStep==99) return true;
        if(now<scannerNext) return false;
        var water=WaterScannerUIManager.Instance; var chem=ChemScannerUIManager.Instance;
        var world=NativeWorld.Guest;
        switch(scannerStep)
        {
            case 0:
                scannerWater=FrontendItem("QA_SCANNER_WATER"); scannerOtherWater=FrontendItem("QA_SCANNER_OTHER_WATER");
                scannerChemical=FrontendItem("QA_SCANNER_CHEMICAL");
                scannerWaterTool=FrontendItem("QA_SCANNER_WATER_TOOL"); scannerChemTool=FrontendItem("QA_SCANNER_CHEM_TOOL");
                ItemSelectHandler.current.TrySelectItem(scannerWaterTool); scannerStep=1; return false;
            case 1:
                if(!scannerWaterTool.MayTarget(scannerWater) || !scannerWaterTool.CanTarget(scannerWater)) return false;
                OriginalScannerTarget(scannerWaterTool,scannerWater);
                Require(water.panel.activeSelf && ReferenceEquals(water.currentWaterContainer,scannerWater) && QaWaiting(link),
                    "original water scanner did not compute locally in the input frame");
                var values=NativeMirrorProbe.CaptureValues(scannerWater); water.RefreshUI();
                Require(JToken.DeepEquals(values,NativeMirrorProbe.CaptureValues(scannerWater)),"guest water scanner refresh wrote shared item state");
                scannerStep=2; return false;
            case 2:
                Require(ScannerWaterText()==MachineReplica().GetTagReadonly("QA_SCANNER_WATER_TEXT").GetString() &&
                    scannerWater.GetTagReadonly("LAST_SCAN_RESULT_TAG").IsEnabled(),"water scanner text differs from original host or original read was not committed");
                NativeOffscreenCapture.Capture("qa-native-water-scanner");
                scannerLabelBefore=water.labelTMP.text;
                var before=NativeMirrorProbe.CaptureValues(scannerWater);
                water.OnMatch();
                Require(QaWaiting(link) && JToken.DeepEquals(before,NativeMirrorProbe.CaptureValues(scannerWater)),"water match was not routed or mutated guest shared scalars");
                scannerStep=3; return false;
            case 3:
                if(!WaterFeatureHelper.GetWaterFeature(scannerWater).IsExposed()) return false;
                water.RefreshUI();
                Require(water.labelTMP.text!=scannerLabelBefore && !water.matchButton.gameObject.activeSelf,"water match did not refresh the native local label");
                Require(firstScannerCommand!=null,"water original match command witness missing");
                var obsolete=JObject.Parse(Encoding.UTF8.GetString(firstScannerCommand));
                world.TryData(scannerWater,out var current); obsolete["token"]=current["moveToken"];
                Require(link.Submit(Encoding.UTF8.GetBytes(obsolete.ToString(Formatting.None)),now),"cannot replay stale scanner feature proof");
                scannerStep=4; return false;
            case 4:
                Require(QaResult(link)==(ushort)ShopResult.Stale && WaterFeatureHelper.GetWaterFeature(scannerWater).IsExposed(),"stale scanner feature repeated shared label effect");
                OriginalScannerWaterChange(); scannerStep=40; return false;
            case 40:
                if(WaterHelper.GetWaterPurity(scannerWater)>=9600 || !scannerWater.GetTagReadonly("LAST_SCAN_RESULT_TAG").IsEnabled()) return false;
                water.RefreshUI();
                Require(water.testedTMP.text==WaterScannerUIManager.GetTestedString(scannerWater),"native water refresh did not compute changed composition locally");
                water.Close(); scannerNext=now+0.8; scannerStep=41; return false;
            case 41:
                Require(!water.panel.activeSelf,"dynamic scanner confirmation reopened closed panel");
                OriginalScannerWaterChange(); scannerNext=now+1.5; scannerStep=42; return false;
            case 42:
                if(WaterHelper.GetWaterPurity(scannerWater)<9990) return false;
                Require(!water.panel.activeSelf && !scannerWater.GetTagReadonly("LAST_SCAN_RESULT_TAG").IsEnabled(),
                    "closed scanner kept observing after composition changed");
                Record("PASS native-water-scanner-dynamic","native source liquid replacement while original guest panel open refreshes local reading/shared last scan; after local close, later composition reset remains unscanned");
                scannerStep=5; return false;
            case 5:
                Require(!water.panel.activeSelf,"snapshot reopened locally closed water scanner");
                Record("PASS native-water-scanner","original tool target/UI computes in input frame; local refresh is read-only, authority latest scan recorded; original match button commits shared label; stale feature rejected; local close retained");
                ItemSelectHandler.current.TrySelectItem(scannerChemTool); scannerStep=6; return false;
            case 6:
                if(!scannerChemTool.MayTarget(scannerChemical) || !scannerChemTool.CanTarget(scannerChemical)) return false;
                OriginalScannerTarget(scannerChemTool,scannerChemical);
                Require(chem.panel.activeSelf && ReferenceEquals(chem.currentScannedItem,scannerChemical) && QaWaiting(link),"native chemical scanner waited for authority to compute its frontend");
                var chemicalValues=NativeMirrorProbe.CaptureValues(scannerChemical); chem.RefreshUI();
                Require(JToken.DeepEquals(chemicalValues,NativeMirrorProbe.CaptureValues(scannerChemical)),"local chemical scanner refresh changed shared item");
                scannerStep=7; return false;
            case 7:
                Require(ScannerChemText()==MachineReplica().GetTagReadonly("QA_SCANNER_CHEM_TEXT").GetString(),"chemical scanner original composition/effect chart differs from host");
                NativeOffscreenCapture.Capture("qa-native-chem-scanner");
                Require(chem.CanMatchChem(scannerChemical),"chemical fixture lacks mismatched native label");
                chem.OnMatch(); scannerStep=8; return false;
            case 8:
                if(!ChemicalProductHelper.GetChemicalFeature(scannerChemical).IsExposed()) return false;
                Require(ChemicalProductHelper.GetChemicalFeature(scannerChemical).IsFeatureMatch(),"original chemical grade helper did not update real and labeled grades");
                chem.Close(); ItemSelectHandler.current.TrySelectItem(scannerChemTool); scannerStep=9; return false;
            case 9:
                if(!scannerChemTool.MayTarget(scannerOtherWater) || !scannerChemTool.CanTarget(scannerOtherWater)) return false;
                OriginalScannerTarget(scannerChemTool,scannerOtherWater); scannerStep=10; return false;
            case 10:
                Require(chem.currentScannedItem==scannerOtherWater && chem.testedTMP.text==WaterScannerUIManager.GetTestedString(scannerOtherWater),"chemical scanner's native water mode did not render original tested text");
                chem.OnMatch(); scannerStep=11; return false;
            case 11:
                if(!WaterFeatureHelper.GetWaterFeature(scannerOtherWater).IsExposed()) return false;
                chem.Close();
                Record("PASS native-chem-scanner","original input-frame chemical UI/composition/effect charts, pure local refresh, original match commits grade once; same class supports native liquid mode without per-item patches; host panels independent");
                var button=(MachineryButton)((MachineryPanelRow)MachineReplica().contentWindow.childElement).Panels.Single(panel=>panel.role==ScannerRole).content;
                AccessTools.Method(typeof(MachineryButton),"OnSelect").Invoke(button,null);
                scannerStep=12; return false;
            case 12:
                if(world.Objects.Values.OfType<GameItem>().Any(item=>item.IsTag(ScannerFixture))) return false;
                Require(!water.panel.activeSelf && !chem.panel.activeSelf && water.currentWaterContainer==null && chem.currentScannedItem==null,
                    "retired scanner target left an active original panel or ghost binding");
                Record("PASS native-scanner-lifetime","host retired scanner targets and tools; original guest windows closed, coroutine targets released before day and reconnect");
                scannerStep=99; return true;
        }
        return false;
    }
}
