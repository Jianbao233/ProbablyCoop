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
    private const string StripFixture="QA_STRIP_FIXTURE",StripRole="qaStripDone",StripDone="QA_STRIP_DONE";
    private GameItem stripItem,stripWater,stripHostItem,stripHostWater;
    private int stripStep;
    private static int stripSourceTests;
    private bool stripSeeded,stripHostDone;
    private double stripUntil,stripNext;
    private static byte[] stripCommand;
    private readonly bool stripOnly=Array.IndexOf(Environment.GetCommandLineArgs(),"--probably-coop-qa-water-strip-only")>=0;
    private double stripHostFinishAt;
    private void SeedStripControl()=>((MachineryPanelRow)qaMachine.contentWindow.childElement).Add(new MachineryPanel("Strip QA",
        new MachineryButton("QA strip complete",()=>qaMachine.EnableTag(StripDone)),StripRole));
    internal static void RememberWaterStripCommand(JObject command)=>stripCommand??=Encoding.UTF8.GetBytes(command.ToString(Formatting.None));
    private static string StripText()=>string.Join("|",WaterStripUIManager.Instance.testedTMP.text,
        WaterStripUIManager.Instance.purityTMP.text,WaterStripUIManager.Instance.labelTMP.text,
        WaterStripUIManager.Instance.qualityStrip.color);
    private void SeedStripFrontends()
    {
        stripSourceTests=0;
        stripItem=SeedFrontendItem("water_test_strip","QA_STRIP_CLIENT");
        stripWater=SeedFrontendItem("small_bottled_water","QA_STRIP_WATER");
        stripHostItem=SeedFrontendItem("water_test_strip","QA_STRIP_HOST");
        stripHostWater=SeedFrontendItem("small_bottled_water","QA_STRIP_HOST_WATER");
        foreach(var item in new[]{stripItem,stripWater,stripHostItem,stripHostWater})
        { item.DisableTag(FrontendFixture); item.EnableTag(StripFixture); }
        foreach(var water in new[]{stripWater,stripHostWater})
        { WaterHelper.AddWater(water,0,-1,true,9995,9995); WaterFeatureHelper.SetPresumedPurity(water,5000); }
        stripHostItem.Target(stripHostWater);
        qaMachine.ModifyTag("QA_STRIP_NATIVE_TEXT",tag=>tag.Enable().SetString(StripText()));
        stripSeeded=true;
    }
    private void CheckHostStripFrontends()
    {
        if(!stripSeeded || stripHostDone) return;
        Require(WaterStripUIManager.Instance.panel.activeSelf && ReferenceEquals(WaterStripUIManager.Instance.currentWaterContainer,stripHostWater),
            "guest strip view replaced the host original strip window or water");
        if(!qaMachine.IsTag(StripDone)) return;
        Require(stripSourceTests==1 && stripItem.IsTag("IS_SPENT") && stripItem.unitValue==0 &&
            stripItem.spritePath=="strip0" && WaterFeatureHelper.GetWaterFeature(stripWater).IsExposed(),
            "source native strip was not consumed/tested once, colored or matched");
        WaterStripUIManager.Instance.Close(); WaterStripUIManager.Instance.currentWaterContainer=null;
        foreach(var item in new[]{stripItem,stripWater,stripHostItem,stripHostWater}) item.Destroy();
        stripHostDone=true;
        if(!stripOnly) SeedScratchFrontends();
        Record("PASS host-native-water-strip","original test helper once, native used-strip sprite/name/value and water label shared; host original window retained independent target; fixtures removed before days/save");
    }
    private bool RunStripFrontends(ReplicaLink link,double now)
    {
        if(stripStep==99) return true;
        if(stripUntil==0) stripUntil=now+45;
        Require(now<stripUntil,"water strip workflow timed out at "+stripStep);
        if(QaWaiting(link) || now<stripNext) return false;
        var world=NativeWorld.Guest; var ui=WaterStripUIManager.Instance;
        switch(stripStep)
        {
            case 0:
                if(!world.Objects.Values.OfType<GameItem>().Any(item=>item.IsTag("QA_STRIP_CLIENT"))) return false;
                stripItem=FrontendItem("QA_STRIP_CLIENT"); stripWater=FrontendItem("QA_STRIP_WATER");
                ItemSelectHandler.current.TrySelectItem(stripItem); stripStep=1; return false;
            case 1:
                if(!stripItem.MayTarget(stripWater) || !stripItem.CanTarget(stripWater)) return false;
                var before=NativeMirrorProbe.CaptureValues(stripItem);
                OriginalScannerTarget(stripItem,stripWater);
                Require(QaWaiting(link) && JToken.DeepEquals(before,NativeMirrorProbe.CaptureValues(stripItem)),
                    "fresh strip test consumed or changed replicated strip locally");
                stripStep=2; return false;
            case 2:
                if(!ui.panel.activeSelf || !stripItem.IsTag("IS_SPENT")) return false;
                Record("TRACE native-water-strip-result","sameWater="+ReferenceEquals(ui.currentWaterContainer,stripWater)+" sprite="+stripItem.spritePath+
                    " value="+stripItem.unitValue+" association="+stripItem.GetTagReadonly("ASSOCIATED_ITEM_ID").GetInt()+" waterId="+stripWater.uniqueId+
                    " text="+StripText()+" expected="+MachineReplica().GetTagReadonly("QA_STRIP_NATIVE_TEXT").GetString());
                Require(ReferenceEquals(ui.currentWaterContainer,stripWater) && stripItem.spritePath=="strip0" && stripItem.unitValue==0 &&
                    stripItem.GetTagReadonly("ASSOCIATED_ITEM_ID").GetInt()==stripWater.uniqueId &&
                    StripText()==MachineReplica().GetTagReadonly("QA_STRIP_NATIVE_TEXT").GetString(),"guest original strip UI differs from source original result");
                NativeOffscreenCapture.Capture("qa-native-water-strip");
                ui.Close(); before=NativeMirrorProbe.CaptureValues(stripItem);
                stripItem.ActivateSlotItem();
                Require(ui.panel.activeSelf && ReferenceEquals(ui.currentWaterContainer,stripWater) && QaWaiting(link) &&
                    JToken.DeepEquals(before,NativeMirrorProbe.CaptureValues(stripItem)),"used strip did not open original panel immediately on a detached display card");
                ui.Close(); stripNext=now+0.8; stripStep=3; return false;
            case 3:
                Require(!ui.panel.activeSelf,"strip authority confirmation reopened a locally closed window");
                stripItem.ActivateSlotItem(); stripStep=4; return false;
            case 4:
                Require(ui.panel.activeSelf && ui.matchButton.gameObject.activeSelf,"original strip match button unavailable");
                var waterBefore=NativeMirrorProbe.CaptureValues(stripWater); ui.OnMatch();
                Require(QaWaiting(link) && JToken.DeepEquals(waterBefore,NativeMirrorProbe.CaptureValues(stripWater)),"strip label committed on guest");
                stripStep=5; return false;
            case 5:
                if(!WaterFeatureHelper.GetWaterFeature(stripWater).IsExposed()) return false;
                Require(!ui.matchButton.gameObject.activeSelf,"strip commit did not update local original label controls");
                Require(stripCommand!=null,"strip match command witness missing");
                var bad=JObject.Parse(Encoding.UTF8.GetString(stripCommand));
                world.TryData(stripItem,out var stripData); bad["token"]=stripData["moveToken"];
                bad["water"]=0u;
                Require(link.Submit(Encoding.UTF8.GetBytes(bad.ToString(Formatting.None)),now),"cannot probe forged strip water association");
                stripStep=6; return false;
            case 6:
                Require(QaResult(link)==(ushort)ShopResult.Stale,"forged water association was accepted");
                var button=(MachineryButton)((MachineryPanelRow)MachineReplica().contentWindow.childElement).Panels.Single(panel=>panel.role==StripRole).content;
                AccessTools.Method(typeof(MachineryButton),"OnSelect").Invoke(button,null); stripStep=7; return false;
            case 7:
                if(world.Objects.Values.OfType<GameItem>().Any(item=>item.IsTag(StripFixture))) return false;
                Require(!ui.panel.activeSelf && ui.currentWaterContainer==null,"retired strip/water left active UI or ghost water binding");
                Record("PASS native-water-strip","native tool-target consume/test, original result/used sprite and shared value; used-strip input-frame original open on detached data; local close stays closed; shared match and forged association rejection; retirement clears UI");
                stripStep=99; return true;
        }
        return false;
    }
    [HarmonyPatch(typeof(WaterTestHelper),nameof(WaterTestHelper.HandleWaterCheckTestingStrip))]
    private static class StripTestOracle
    {
        private static void Postfix(GameItem testingStrip)
        { if(Enabled && !NativeGuest.Active && testingStrip.IsTag("QA_STRIP_CLIENT")) stripSourceTests++; }
    }
}
