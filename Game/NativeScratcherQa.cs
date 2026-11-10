using System;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Core;
using UnityEngine;
using UnityEngine.UI;

namespace ProbablyCoop;

internal sealed partial class NativeBackgroundQa
{
    private const string ScratchFixture="QA_SCRATCH_FIXTURE",ScratchRole="qaScratchDone",ScratchDone="QA_SCRATCH_DONE";
    private GameItem scratchItem,scratchWinner,scratchHostItem;
    private bool scratchSeeded,scratchHostDone,scratchOldCoin;
    private int scratchStep,scratchOldStarted,scratchOldFinished,scratchOldWinnings,scratchOldCash,scratchExpectedFirst,partialScratchPixels;
    private double scratchUntil,scratchNext,scratchHostFinishAt;
    private static int scratchRerolls,scratchRedemptions,scratchGuestWrites;
    private static readonly int[] scratchMarks=new int[6];
    private static byte[] scratchFirstCommand;
    private byte[] scratchRedeemCommand;
    private readonly bool scratchOnly=Array.IndexOf(Environment.GetCommandLineArgs(),"--probably-coop-qa-scratcher-only")>=0;
    private static readonly MethodInfo NativeScratchStroke=AccessTools.Method(typeof(ScratchInput),"StrokeTo");
    private static readonly FieldInfo NativeScratchMask=AccessTools.Field(typeof(ScratchInput),"mask");
    private static readonly FieldInfo NativeScratchRevealed=AccessTools.Field(typeof(ScratchInput),"revealed");
    private static readonly FieldInfo NativeScratchImage=AccessTools.Field(typeof(ScratchInput),"ticketImage");
    internal static void RememberScratchCommand(JObject command)
    { if((int)command["slot"]>0) scratchFirstCommand??=Encoding.UTF8.GetBytes(command.ToString(Formatting.None)); }
    private void SeedScratchControl()=>((MachineryPanelRow)qaMachine.contentWindow.childElement).Add(new MachineryPanel("Scratch QA",
        new MachineryButton("QA scratch complete",()=>qaMachine.EnableTag(ScratchDone)),ScratchRole));
    private void SeedScratchFrontends()
    {
        var store=PlayerStore.Instance;
        scratchOldStarted=store.scratchersStartedTotal; scratchOldFinished=store.scratchersFinishedTotal;
        scratchOldWinnings=store.scratcherWinningsTotal; scratchOldCash=store.playerCash; scratchOldCoin=store.isGamblersCoinRewardGiven;
        scratchRerolls=scratchRedemptions=scratchGuestWrites=0; Array.Clear(scratchMarks,0,scratchMarks.Length);
        scratchItem=SeedFrontendItem("scratcher","QA_SCRATCH_REROLL");
        scratchWinner=SeedFrontendItem("scratcher","QA_SCRATCH_WINNER");
        scratchHostItem=SeedFrontendItem("scratcher","QA_SCRATCH_HOST");
        foreach(var item in new[]{scratchItem,scratchWinner,scratchHostItem})
        { item.DisableTag(FrontendFixture); item.EnableTag(ScratchFixture); }
        LotteryHelper.SetSpawnLuck(scratchItem,-1000); // First real source Open must execute its native reroll.
        LotteryHelper.SetSpawnLuck(scratchWinner,int.MaxValue); LotteryHelper.SetSpawnLuck(scratchHostItem,int.MaxValue);
        for(int i=1;i<=3;i++) scratchWinner.ModifyTag("SCRATCHER_SYMBOL"+i,tag=>tag.Enable().SetInt(1));
        ScratcherUIManager.Instance.OpenUI(scratchHostItem);
        qaMachine.ModifyTag("QA_SCRATCH_STARTED",tag=>tag.Enable().SetInt(scratchOldStarted));
        qaMachine.ModifyTag("QA_SCRATCH_FINISHED",tag=>tag.Enable().SetInt(scratchOldFinished));
        qaMachine.ModifyTag("QA_SCRATCH_WINNINGS",tag=>tag.Enable().SetInt(scratchOldWinnings));
        qaMachine.ModifyTag("QA_SCRATCH_CASH",tag=>tag.Enable().SetInt(scratchOldCash));
        scratchSeeded=true;
    }
    private void CheckHostScratchFrontends()
    {
        if(!scratchSeeded || scratchHostDone) return;
        Require(ScratcherUIManager.Instance.ui.activeSelf && ReferenceEquals(ScratcherUIManager.Instance.currentActiveScratcherItem,scratchHostItem),
            "guest scratch workflow replaced host own original ticket UI");
        if(!qaMachine.IsTag(ScratchDone)) return;
        var store=PlayerStore.Instance;
        var ui=ScratcherUIManager.Instance;
        var originalTicket=ui.currentActiveScratcherItem;
        bool oldDragging=UIDragHandler.isDraggingAnyUI;
        try
        {
            // After the independent-window assertions, the source deliberately
            // inspects the peer-completed ticket using its actual native UI.
            ui.OpenUI(scratchItem); ui.scratchInputs[0].ResetScratch();
            NativeWorld.Host.RefreshHostScratch();
            Require((bool)NativeScratchRevealed.GetValue(ui.scratchInputs[0]),"source native mask did not adopt peer-completed slot");
            UIDragHandler.isDraggingAnyUI=true;
            NativeWorld.Host.RefreshHostScratch();
            Require(UIDragHandler.isDraggingAnyUI,"stable shared scratch state interrupted unrelated native UI drag");
            ui.OnScratchComplete(1);
        }
        finally { UIDragHandler.isDraggingAnyUI=oldDragging; ui.OpenUI(originalTicket); }
        Require(scratchRerolls==1 && scratchMarks.All(count=>count==1) && scratchRedemptions==1 &&
            store.scratchersStartedTotal==scratchOldStarted+2 && store.scratchersFinishedTotal==scratchOldFinished+2 &&
            store.scratcherWinningsTotal==scratchOldWinnings+ScratchReward(scratchItem)+350 && store.playerCash==scratchOldCash+350,
            "source scratch RNG/slot/start/finish/value/redemption was not exactly once");
        ScratcherUIManager.Instance.CloseUI();
        foreach(var item in new[]{scratchItem,scratchWinner,scratchHostItem}) if(item.parentInventory!=null) item.Destroy();
        store.scratchersStartedTotal=scratchOldStarted; store.scratchersFinishedTotal=scratchOldFinished;
        store.scratcherWinningsTotal=scratchOldWinnings; store.playerCash=scratchOldCash; store.isGamblersCoinRewardGiven=scratchOldCoin;
        scratchHostDone=true;
        Record("PASS host-native-scratcher","native source reroll once, six slot helpers once, two started/finished records, one 350 redemption; independent host ticket remained open; fixture economy restored before days/save");
    }
    private static int ScratchReward(GameItem item)
    {
        var group=Enumerable.Range(1,3).Select(i=>item.GetTagReadonly("SCRATCHER_SYMBOL"+i).GetInt()).GroupBy(value=>value).OrderByDescending(g=>g.Count()).First();
        return group.Count()==3 ? group.Key==1 ? 350 : 75 : group.Count()==2 ? group.Key==1 ? 30 : 10 : 0;
    }
    private static int ScratchPixels(ScratchInput input)
    {
        var previous=RenderTexture.active; var texture=new Texture2D(56,56,TextureFormat.RGBA32,false);
        try
        {
            RenderTexture.active=(RenderTexture)NativeScratchMask.GetValue(input); texture.ReadPixels(new Rect(0,0,56,56),0,0); texture.Apply();
            return texture.GetPixels32().Count(pixel=>pixel.r>128);
        }
        finally { RenderTexture.active=previous; UnityEngine.Object.Destroy(texture); }
    }
    private static void DrawNativeScratch(ScratchInput input)
    {
        input.OnAnyScratch();
        // Genuine native GPU strokes; its original ReadPixels threshold invokes
        // OnScratchComplete. Do not call the completion API from this test.
        for(int y=12;y<45;y+=3) for(int x=12;x<45;x+=3) NativeScratchStroke.Invoke(input,new object[]{x,y});
        Require((bool)NativeScratchRevealed.GetValue(input) && ScratchPixels(input)==56*56,"original GPU scratch threshold did not reveal its slot");
    }
    private static void CheckScratchTextures(GameItem item)
    {
        var ui=ScratcherUIManager.Instance;
        for(int i=0;i<3;i++) Require(ReferenceEquals(((RawImage)NativeScratchImage.GetValue(ui.scratchInputs[i])).texture,
            ui.symbolTextures[item.GetTagReadonly("SCRATCHER_SYMBOL"+(i+1)).GetInt()-1]),"local scratch texture differs from confirmed native symbols");
    }
    private bool RunScratchFrontends(ReplicaLink link,double now)
    {
        if(scratchStep==99) return true;
        if(scratchUntil==0) scratchUntil=now+60;
        Require(now<scratchUntil,"native scratch workflow timed out at "+scratchStep);
        if(QaWaiting(link) || now<scratchNext) return false;
        var world=NativeWorld.Guest; var ui=ScratcherUIManager.Instance; var store=PlayerStore.Instance;
        switch(scratchStep)
        {
            case 0:
                if(!world.Objects.Values.OfType<GameItem>().Any(item=>item.IsTag("QA_SCRATCH_REROLL"))) return false;
                scratchItem=FrontendItem("QA_SCRATCH_REROLL"); scratchWinner=FrontendItem("QA_SCRATCH_WINNER");
                scratchOldStarted=MachineReplica().GetTagReadonly("QA_SCRATCH_STARTED").GetInt();
                scratchOldFinished=MachineReplica().GetTagReadonly("QA_SCRATCH_FINISHED").GetInt();
                scratchOldWinnings=MachineReplica().GetTagReadonly("QA_SCRATCH_WINNINGS").GetInt();
                scratchOldCash=MachineReplica().GetTagReadonly("QA_SCRATCH_CASH").GetInt();
                var before=NativeMirrorProbe.CaptureValues(scratchItem); scratchItem.ActivateSlotItem();
                Record("TRACE native-scratch-open","visible="+ui.ui.activeSelf+" sameTarget="+ReferenceEquals(ui.currentActiveScratcherItem,scratchItem)+
                    " waiting="+QaWaiting(link)+" ready="+world.ScratchReady+" sharedEqual="+JToken.DeepEquals(before,NativeMirrorProbe.CaptureValues(scratchItem)));
                Require(ui.ui.activeSelf && ReferenceEquals(ui.currentActiveScratcherItem,scratchItem) && QaWaiting(link) && !world.ScratchReady &&
                    JToken.DeepEquals(before,NativeMirrorProbe.CaptureValues(scratchItem)),"native scratch UI did not open locally without guest RNG/item writes");
                ui.CloseUI(); scratchNext=now+0.8; scratchStep=1; return false;
            case 1:
                Require(!ui.ui.activeSelf,"scratch confirmation reopened a locally closed window");
                scratchItem.ActivateSlotItem(); scratchStep=2; return false;
            case 2:
                if(!world.ScratchReady) return false;
                Require(LotteryHelper.GetSpawnLuck(scratchItem)>-1000,"source native lucky reroll did not run");
                CheckScratchTextures(scratchItem); before=NativeMirrorProbe.CaptureValues(scratchItem);
                ui.scratchInputs[0].OnAnyScratch(); NativeScratchStroke.Invoke(ui.scratchInputs[0],new object[]{16,16});
                partialScratchPixels=ScratchPixels(ui.scratchInputs[0]);
                Require(partialScratchPixels>0 && partialScratchPixels<56*56 && !(bool)NativeScratchRevealed.GetValue(ui.scratchInputs[0]) &&
                    QaWaiting(link) && JToken.DeepEquals(before,NativeMirrorProbe.CaptureValues(scratchItem)),"original partial GPU stroke did not stay local");
                scratchNext=now+0.8; scratchStep=3; return false;
            case 3:
                Require(ScratchPixels(ui.scratchInputs[0])==partialScratchPixels && store.scratchersStartedTotal==scratchOldStarted+1,
                    "stable source graph reset partial local pixels or omitted shared first-scratch record");
                before=NativeMirrorProbe.CaptureValues(scratchItem);
                foreach(var input in ui.scratchInputs) DrawNativeScratch(input);
                Require(QaWaiting(link) && JToken.DeepEquals(before,NativeMirrorProbe.CaptureValues(scratchItem)) && scratchGuestWrites==0,
                    "native local three-slot strokes executed shared reward/RNG before authority confirmation");
                scratchStep=4; return false;
            case 4:
                if(!ScratcherHelper.IsFullyScratched(scratchItem)) return false;
                scratchExpectedFirst=ScratchReward(scratchItem);
                Require(scratchItem.unitValue==scratchExpectedFirst && store.scratchersFinishedTotal==scratchOldFinished+1 &&
                    store.scratcherWinningsTotal==scratchOldWinnings+scratchExpectedFirst && scratchFirstCommand!=null,"source native scratch reward/stat projection mismatch");
                NativeOffscreenCapture.Capture("qa-native-scratcher");
                Require(link.Submit(scratchFirstCommand,now),"cannot replay completed scratch slot"); scratchStep=5; return false;
            case 5:
                Require(QaResult(link)==(ushort)ShopResult.Applied && store.scratchersFinishedTotal==scratchOldFinished+1,"repeated slot was not an idempotent success");
                var old=JObject.Parse(Encoding.UTF8.GetString(scratchFirstCommand)); old["face"]="obsolete";
                Require(link.Submit(Encoding.UTF8.GetBytes(old.ToString(Formatting.None)),now),"cannot probe obsolete scratch face"); scratchStep=6; return false;
            case 6:
                Require(QaResult(link)==(ushort)ShopResult.Stale,"obsolete scratch face was accepted");
                ui.CloseUI(); scratchWinner.ActivateSlotItem(); scratchStep=7; return false;
            case 7:
                if(!world.ScratchReady) return false;
                Require(link.Submit(scratchFirstCommand,now),"cannot probe old scratch view against new ticket"); scratchStep=8; return false;
            case 8:
                Require(QaResult(link)==(ushort)ShopResult.Stale && ui.ui.activeSelf && ReferenceEquals(ui.currentActiveScratcherItem,scratchWinner),"old view rejection damaged replacement native ticket UI");
                CheckScratchTextures(scratchWinner); ulong issued=world.LastSubmittedUiRequest;
                ui.OnTurnButtonClicked(); Require(ui.back.activeSelf,"native scratch back did not open");
                ui.OnTurnButtonClicked(); Require(!ui.back.activeSelf && world.LastSubmittedUiRequest==issued,"native scratch flip was not local");
                foreach(var input in ui.scratchInputs) DrawNativeScratch(input);
                Require(QaWaiting(link) && !ScratcherHelper.IsFullyScratched(scratchWinner),"winning ticket waited one network round trip per slot or committed on guest");
                scratchStep=9; return false;
            case 9:
                if(!ScratcherHelper.IsFullyScratched(scratchWinner)) return false;
                Require(scratchWinner.unitValue==350 && store.scratchersStartedTotal==scratchOldStarted+2 &&
                    store.scratchersFinishedTotal==scratchOldFinished+2 && store.scratcherWinningsTotal==scratchOldWinnings+scratchExpectedFirst+350,
                    "native jackpot/counts did not replicate once");
                world.TryData(scratchWinner,out var data);
                scratchRedeemCommand=Encoding.UTF8.GetBytes(new JObject { ["kind"]="item",["action"]="activate",["id"]=data["entity"],
                    ["token"]=data["moveToken"],["context"]=(ulong)AccessTools.Field(typeof(NativeWorld),"observedOperationContext").GetValue(world) }.ToString(Formatting.None));
                scratchWinner.ActivateSlotItem(); scratchStep=10; return false;
            case 10:
                if(world.Objects.Values.OfType<GameItem>().Any(item=>item.IsTag("QA_SCRATCH_WINNER"))) return false;
                Require(store.playerCash==scratchOldCash+350 && !ui.ui.activeSelf && ui.currentActiveScratcherItem==null,"original scratch redemption did not close retired UI or add exactly 350");
                Require(link.Submit(scratchRedeemCommand,now),"cannot replay redeemed ticket"); scratchStep=11; return false;
            case 11:
                Require(QaResult(link)==(ushort)ShopResult.Stale && store.playerCash==scratchOldCash+350,"redeemed ticket paid twice");
                var button=(MachineryButton)((MachineryPanelRow)MachineReplica().contentWindow.childElement).Panels.Single(panel=>panel.role==ScratchRole).content;
                AccessTools.Method(typeof(MachineryButton),"OnSelect").Invoke(button,null); scratchStep=12; return false;
            case 12:
                if(world.Objects.Values.OfType<GameItem>().Any(item=>item.IsTag(ScratchFixture))) return false;
                Require(!ui.ui.activeSelf && ui.currentActiveScratcherItem==null && scratchGuestWrites==0 && store.playerCash==scratchOldCash &&
                    store.scratchersStartedTotal==scratchOldStarted && store.scratchersFinishedTotal==scratchOldFinished && store.scratcherWinningsTotal==scratchOldWinnings,
                    "scratch fixture retirement left UI, guest business writes or test economy");
                Record("PASS native-scratcher","original input-frame UI, confirmed source reroll, native GPU partial/full strokes and completion threshold; stable snapshots preserve partial pixels; six local slot reveals before shared replies; local flip/close, slot idempotence, obsolete face/view, original 350 redemption once and cleanup");
                scratchStep=99; return true;
        }
        return false;
    }
    [HarmonyPatch(typeof(ScratcherHelper),nameof(ScratcherHelper.SetRandomSymbols))]
    private static class ScratchRngOracle
    {
        private static void Postfix(GameItem gameItem)
        { if(!Enabled) return; if(NativeGuest.Active) scratchGuestWrites++; else if(gameItem.IsTag("QA_SCRATCH_REROLL")) scratchRerolls++; }
    }
    [HarmonyPatch(typeof(ScratcherHelper),nameof(ScratcherHelper.MarkSymbolScratched))]
    private static class ScratchMarkOracle
    {
        private static void Postfix(GameItem gameItem,int symbolIndex)
        {
            if(!Enabled) return; if(NativeGuest.Active) { scratchGuestWrites++; return; }
            int offset=gameItem.IsTag("QA_SCRATCH_REROLL") ? 0 : gameItem.IsTag("QA_SCRATCH_WINNER") ? 3 : -1;
            if(offset>=0 && symbolIndex is >=1 and <=3) scratchMarks[offset+symbolIndex-1]++;
        }
    }
    [HarmonyPatch(typeof(LotteryHelper),nameof(LotteryHelper.TryRedeemPrize))]
    private static class ScratchRedeemOracle
    {
        private static void Postfix(GameItem gameItem,bool __result)
        { if(Enabled && __result && gameItem.IsTag("QA_SCRATCH_WINNER")) { if(NativeGuest.Active) scratchGuestWrites++; else scratchRedemptions++; } }
    }
}
