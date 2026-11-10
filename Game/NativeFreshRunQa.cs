using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Core;
using UnityEngine;

namespace ProbablyCoop;

internal sealed partial class NativeBackgroundQa
{
    private readonly bool freshRun=Array.IndexOf(Environment.GetCommandLineArgs(),"--probably-coop-qa-fresh-run")>=0;
    private readonly HashSet<int> freshStages=new HashSet<int>();
    private readonly HashSet<string> freshClients=new HashSet<string>();
    private bool freshRegistered,freshHintsClosed,freshRecorderOpened,freshBuySeen,freshSellSeen,freshDayRecorded;
    private int freshSlot,freshStep,freshReadCount,freshTrades;
    private string freshRunId,freshLastClient;
    private double freshNext,freshUntil,freshFinishAt;
    private GameItem[] freshNotes;
    private bool freshTradePending;
    private int freshTradeCash;
    private StoreClient.ClientIntent freshTradeIntent;
    private static byte[] freshReadCommand;
    private static int freshSourcePageReads,freshSourceDoubleClicks;
    internal static void RememberDocumentRead(JObject command)
    { freshReadCommand??=System.Text.Encoding.UTF8.GetBytes(command.ToString(Formatting.None)); }
    private static int TutorialStage()=>(int)AccessTools.Field(typeof(TutorialUIManager),"tutorialStage").GetValue(TutorialUIManager.Instance);
    private void TickFreshHost(RoomSelector room,CoopSession session,double now)
    {
        var store=PlayerStore.Instance;
        if(!freshRegistered)
        {
            Record("TRACE fresh-initial-identity","slot="+store.saveSlotId+" day="+StoreStation.Instance.dayCounter+" tutorial="+store.tutorialEnable+
                " skipIntro="+store.skipIntro+" registered="+CoopRun.IsRegistered(store)+" verified="+CoopRun.VerifyCheckpoint(store,1));
            Require(store.saveSlotId!=5 && store.saveSlotId>=0 && store.tutorialEnable && !store.skipIntro &&
                StoreStation.Instance.dayCounter==1 && CoopRun.IsRegistered(store) && CoopRun.VerifyCheckpoint(store,1),"fresh source did not allocate/register/save an actual new day-one cooperation slot");
            freshSlot=store.saveSlotId; freshRunId=store.runID; freshRegistered=true;
            ModHelper.WriteModDataFileText("probably_coop","qa-room.txt",room.Code);
            Record("PASS fresh-native-initial-save","original new game/InitialSave allocated slot="+freshSlot+" run="+freshRunId+" with tutorial and native day-one checkpoint; old QA slot5 retained");
        }
        freshStages.Add(TutorialStage());
        CheckBoundarySource(room);
        if(freshDayRecorded)
        {
            if(boundaryRecovery && boundaryJoins<3) return;
            if(now>=freshFinishAt) Finish("PASS host runtime");
            return;
        }
        if(session?.State!=LinkState.Connected) return;
        if(!freshHintsClosed)
        {
            Require(StoreUIManager.Instance.controlHintOverlay.activeSelf && TutorialStage()==110,"original first-run control hints missing");
            StoreUIManager.Instance.ToggleControlHintOverlay();
            Require(TutorialStage()==0,"closing original local control hints did not advance native intro");
            freshHintsClosed=true;
        }
        if(DialogSettingUIManager.Instance.optionPanel.activeSelf) DialogSettingUIManager.Instance.CloseUI();
        if(TutorialStage()==8 && freshSourcePageReads==4 && !freshRecorderOpened)
        {
            RecorderUIManager.Instance.OpenUI();
            Require(TutorialStage()==9,"original host recorder did not advance shared onboarding");
            RecorderUIManager.Instance.CloseUI(); freshRecorderOpened=true;
            Record("PASS fresh-host-recorder","host used original recorder after guest moved/read original mentor notes; tutorial advanced 8->9 without stage assignment");
        }
        if(store.storeState==PlayerStore.StoreState.CLOSED)
        { eveningFixture=true; if(hostDayTarget==0) hostDayTarget=1; }
        CheckHostDay(session);
        if(!hostDayVerified) return;
        if(!freshDayRecorded)
        {
            WriteFreshFacts("host");
            Require(freshStages.Contains(3) && freshStages.Contains(5) && freshRecorderOpened &&
                freshSourcePageReads==4 && freshSourceDoubleClicks==4 &&
                store.saveSlotId==freshSlot && store.runID==freshRunId && CoopRun.VerifyCheckpoint(store,2),
                "fresh native guide/notes/recorder/new-slot day-two checkpoint was not completed; stages="+string.Join(",",freshStages.OrderBy(x=>x))+" recorder="+freshRecorderOpened);
            freshDayRecorded=true;
            VerifyBoundarySource();
            Record("PASS fresh-native-host","original new slot remained isolated through mentor, guest note interactions, native buying/selling, first day settlement and original save read-back; source tutorial stages="+string.Join(",",freshStages.OrderBy(x=>x)));
        }
        if(freshFinishAt==0) freshFinishAt=now+5;
        if(now>=freshFinishAt) Finish("PASS host runtime");
    }
    private bool RunFreshGuest(CoopSession session,double now)
    {
        if(freshUntil==0) freshUntil=now+240;
        Require(now<freshUntil,"fresh native day timed out at "+freshStep+" client="+PlayerStore.Instance.currentClientInstance?.GetClientBlueprint()?.identifier);
        if(QaWaiting(session.Native) || session.Shop.Waiting || now<freshNext) return false;
        var world=NativeWorld.Guest; var store=PlayerStore.Instance; var entry=EmporiumEntry.Instance;
        if(freshTradePending)
        {
            Require(freshTradeIntent==StoreClient.ClientIntent.SELL ? store.playerCash<freshTradeCash : store.playerCash>freshTradeCash,
                "fresh native food transaction did not change authoritative cash in the expected direction");
            freshTradePending=false; freshTrades++;
            if(freshTradeIntent==StoreClient.ClientIntent.SELL) freshBuySeen=true; else freshSellSeen=true;
            Record("PASS fresh-native-trade","client="+freshLastClient+" sourceIntent="+freshTradeIntent+" cash="+freshTradeCash+"->"+store.playerCash);
        }
        if(freshStep==0)
        {
            if(!world.CanOpen) return false;
            Require(StoreStation.Instance.dayCounter==1 && store.saveSlotId==-1,"guest loaded a native slot instead of joining fresh source");
            StoreShutterButton.Instance.OnClick(); freshStep=1; freshNext=now+3.5; return false;
        }
        if(freshStep==2)
        {
            var remaining=freshNotes.FirstOrDefault(item=>!ReferenceEquals(item.parentInventory,entry.invElement));
            if(remaining!=null)
            {
                var slot=entry.invElement.TryFindOneValidInventorySlot(remaining);
                Require(slot?.IsValid()==true && slot.numTransfer==remaining.unitCount,"mentor note has no original inventory placement");
                slot.TryAcceptOnce(); freshNext=now+0.8; return false;
            }
            freshStep=3;
            Record("PASS fresh-native-mentor-stock","original mentor conversation delivered two real note entities; guest original slot placement moved both to inventory");
        }
        if(freshStep==3)
        {
            if(freshReadCount<4)
            {
                ulong first=0;
                while(freshReadCount<4)
                {
                    freshNotes[freshReadCount%2].ActivateSlotItem();
                    Require(PaperUIManager.Instance.panel.activeSelf,"original mentor note did not open on guest");
                    if(freshReadCount==0) { first=world.LastSubmittedUiRequest; NativeOffscreenCapture.Capture("qa-fresh-mentor-note"); }
                    else Require(world.LastSubmittedUiRequest==first,"local rapid note reads bypassed the ordered guide queue");
                    PaperUIManager.Instance.CloseUI(); freshReadCount++;
                }
                Require(world.HasPendingWork,"rapid original reads left no shared guide work");
                freshNext=now+0.8; return false;
            }
            if(world.HasPendingWork) return false;
            Require(freshReadCommand!=null,"native page read emitted no shared guide callback");
            Require(session.Native.Submit(freshReadCommand,now),"cannot replay obsolete guide chapter proof");
            freshStep=5; return false;
        }
        if(freshStep==5)
        {
            Require(session.Native.Result==(ushort)ShopResult.Stale,"old tutorial read advanced a later chapter");
            Record("PASS fresh-native-page-reads","local original mentor-note UI and four source native read callbacks; source recorder continued guide; obsolete chapter proof rejected");
            freshStep=1;
        }
        if(store.storeState==PlayerStore.StoreState.CLOSED || freshStep==4)
        {
            freshStep=4;
            if(!RunGuestDay(session,now)) return false;
            Require(freshBuySeen && freshSellSeen && freshTrades>=3 && freshClients.Contains("mentor") && freshClients.Contains("wanted_intro"),
                "fresh day omitted original mentor, all three food trades or mandatory officer introduction");
            WriteFreshFacts("guest");
            VerifyBoundaryGuest();
            Record("PASS fresh-native-guest","native new-shop day one, mentor and mandatory story dialogue, note UI, three food transactions, cooperative first day settlement and full morning report; no guest save");
            return true;
        }
        string clientId=store.currentClientInstance?.GetClientBlueprint()?.identifier;
        if(clientId!=null && freshClients.Add(clientId)) Record("TRACE fresh-native-client",clientId);
        if(clientId=="mentor" && freshNotes==null)
        {
            var notes=entry.frontInvinvElement.childItems.Concat(entry.backInvinvElement.childItems)
                .Where(item=>item.IsTag("TUTORIAL_PAPER_TAG") && GeneralHelper.IsItemOwned(item)).ToArray();
            if(notes.Length==2)
            {
                freshNotes=notes;
                freshStep=2; return false;
            }
        }
        if(world.HasDialogue)
        {
            var dialog=DialogUIManager.Instance;
            var choice=dialog.dialogChoiceBubbles.FirstOrDefault(b=>b.gameObject.activeSelf && b.IsInteractable());
            if(choice!=null) choice.OnDialogChoiceClick(); else dialog.OnAdvancePressed();
            freshNext=now+0.4; return false;
        }
        if(world.PendingMainDialogue || store.currentClientInstance!=null && !store.isClientArrived) return false;
        if(clientId!=null)
        {
            var client=store.currentClientInstance.GetClientBlueprint();
            if(!client.isBusinessComplete && client.clientIntent is StoreClient.ClientIntent.SELL or StoreClient.ClientIntent.BUY)
            {
                if(client.clientIntent==StoreClient.ClientIntent.SELL)
                {
                    var offered=entry.frontInvinvElement.childItems.FirstOrDefault(item=>!GeneralHelper.IsItemOwned(item));
                    if(offered!=null)
                    {
                        var slot=entry.backInvinvElement.TryFindOneValidInventorySlot(offered);
                        Require(slot?.IsValid()==true,"fresh original seller goods have no back-counter placement"); slot.TryAcceptOnce();
                        freshNext=now+0.8; return false;
                    }
                }
                if(client.clientIntent==StoreClient.ClientIntent.BUY && !entry.backInvinvElement.childItems.Any(item=>GeneralHelper.IsItemOwned(item) && client.IsClientBuyingThisItem(item)))
                {
                    var food=entry.invElement.childItems.Where(item=>GeneralHelper.IsItemOwned(item) && client.IsClientBuyingThisItem(item)).OrderBy(item=>item.GetNegociatedValue()).FirstOrDefault();
                    if(food!=null)
                    {
                        var slot=entry.backInvinvElement.TryFindOneValidInventorySlot(food);
                        Require(slot?.IsValid()==true,"fresh original buyer has no counter placement");
                        slot.TryAcceptOnce(); freshNext=now+0.7; return false;
                    }
                }
                var ui=NegociationUIManager.Instance; ui.OpenUI(); ui.RefreshUI();
                var button=(TabletButton)AccessTools.Field(typeof(NegociationUIManager),"primaryButton").GetValue(ui);
                if(button?.Interactable==true)
                {
                    freshLastClient=clientId; freshTradeCash=store.playerCash; freshTradeIntent=client.clientIntent;
                    button.button.onClick.Invoke();
                    var frame=(NegotiationTabletFrame)AccessTools.Field(typeof(NegociationUIManager),"frame").GetValue(ui);
                    if(frame.confirmRoot.activeSelf) ui.OnConfirmPanelYes();
                    freshTradePending=QaWaiting(session.Native);
                    freshNext=now+0.8; return false;
                }
            }
            if(store.IsClientDimissable())
            { UnityEngine.Object.FindObjectOfType<StoreBell>().GetComponent<Interactable>().OnClick(); freshNext=now+2.5; }
            return false;
        }
        if(world.QueuedClients>0)
        { UnityEngine.Object.FindObjectOfType<StoreBell>().GetComponent<Interactable>().OnClick(); freshNext=now+2.5; return false; }
        if(world.CanClose) { StoreShutterButton.Instance.OnClick(); freshNext=now+2.5; }
        return false;
    }
    private void WriteFreshFacts(string role)
    {
        var store=PlayerStore.Instance;
        var counts=EmporiumEntry.Instance.GetAllItems().GroupBy(item=>item.identifier).OrderBy(group=>group.Key)
            .ToDictionary(group=>group.Key,group=>group.Sum(item=>item.unitCount));
        var facts=new JObject { ["role"]=role,["day"]=StoreStation.Instance.dayCounter,["cash"]=store.playerCash,
            ["stock"]=JObject.FromObject(counts),["nativeSlot"]=store.saveSlotId,["run"]=host ? freshRunId : "",
            ["tutorialStages"]=new JArray(freshStages.OrderBy(x=>x)),["clients"]=new JArray(freshClients),["trades"]=freshTrades };
        ModHelper.WriteModDataFileText("probably_coop","qa-fresh-run.json",facts.ToString(Formatting.Indented));
    }
    [HarmonyPatch(typeof(TutorialUIManager),nameof(TutorialUIManager.OnPageRead))]
    private static class FreshPageReadOracle
    {
        private static void Postfix()
        { if(Enabled && IsHostProduct && Array.IndexOf(Environment.GetCommandLineArgs(),"--probably-coop-qa-fresh-run")>=0) freshSourcePageReads++; }
    }
    [HarmonyPatch(typeof(TutorialUIManager),nameof(TutorialUIManager.OnDoubleClick))]
    private static class FreshDoubleClickOracle
    {
        private static void Postfix()
        { if(Enabled && IsHostProduct && Array.IndexOf(Environment.GetCommandLineArgs(),"--probably-coop-qa-fresh-run")>=0) freshSourceDoubleClicks++; }
    }
}
