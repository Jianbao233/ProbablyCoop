using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Core;
using TMPro;
using UnityEngine.UI;

namespace ProbablyCoop;

// Isolated real-process supplement. The fixture selects distinguishable native
// appraisal ranks, uses the original certificate and Toggle callbacks, and
// restores those QA ranks after both sides have observed the result.
internal sealed partial class NativeBackgroundQa
{
    private const string AppraisalQaCertificate="QA_APPRAISAL_CERTIFICATE";
    private const string AppraisalQaControl="QA_APPRAISAL_CONTROL";
    private const string AppraisalQaReady="QA_APPRAISAL_READY";
    private const string AppraisalQaDone="QA_APPRAISAL_DONE";
    private const string AppraisalQaStage="QA_APPRAISAL_STAGE";
    private const string AppraisalQaNames="QA_APPRAISAL_NATIVE_NAMES";
    private GameItem appraisalQaCertificate,appraisalQaControl;
    private CustomUIWindow appraisalQaHostWindow;
    private int appraisalQaHostRank,appraisalQaPartnerRank;
    private List<string> appraisalQaHostBlacklist,appraisalQaPartnerBlacklist;
    private bool appraisalQaHostStarted,appraisalQaHostDone;
    private int appraisalQaStep;
    private double appraisalQaDeadline,appraisalQaClosedUntil;
    private ulong appraisalQaCloseSerial;
    private static bool appraisalQaWatching;
    private static int appraisalQaOriginalWrites;
    private static JObject appraisalQaFirstCommand;

    internal static void RememberAppraisalCommand(JObject command)
    { appraisalQaFirstCommand??=(JObject)command.DeepClone(); }

    private bool AppraisalQaHostDone=>appraisalQaHostDone;

    private void SeedAppraisalQa()
    {
        Require(Enabled && IsHostProduct && NativeActorSession.Current!=null,"appraisal QA requires an isolated cooperative Host");
        appraisalQaCertificate=DirectoryMaster.Item("cert3");
        appraisalQaControl=DirectoryMaster.Item("processed_meat");
        appraisalQaCertificate.EnableTag(AppraisalQaCertificate);
        appraisalQaControl.EnableTag(AppraisalQaControl);
        var button=new MachineryButton("QA appraisal phase",()=>appraisalQaControl.ModifyTag(AppraisalQaStage,
            tag=>tag.Enable().SetInt(tag.GetInt()+1)));
        appraisalQaControl.SetContentWindow(MachineHelper.CreateMachineryWindow(true,new MachineryPanel("QA appraisal",button,"qaAppraisal")));
        foreach(var item in new[]{appraisalQaCertificate,appraisalQaControl})
        {
            GeneralHelper.SetItemOwned(item,true);
            Require(TryPlaceInEmptyCell(item,EmporiumEntry.Instance.invElement),"appraisal fixture does not fit shared stock");
        }
        appraisalQaWatching=false; appraisalQaOriginalWrites=0; appraisalQaFirstCommand=null;
        Record("FIXTURE appraisal-native","registered cert3 factory and separate QA coordination control; no save, no custom certificate callback");
    }

    private void CheckHostAppraisalQa()
    {
        if(appraisalQaHostDone || appraisalQaControl==null || appraisalQaControl.GetTagReadonly(AppraisalQaStage).GetInt()==0) return;
        var session=NativeActorSession.Current;
        Require(session!=null,"appraisal QA lost the native actor session");
        if(!appraisalQaHostStarted)
        {
            appraisalQaHostRank=session.Store.appraiserLevel;
            appraisalQaPartnerRank=session.Run.Partner.appraiserLevel;
            appraisalQaHostBlacklist=session.Store.appraisalBlacklist;
            appraisalQaPartnerBlacklist=session.Run.Partner.appraisalBlacklist;
            session.Store.appraiserLevel=0;
            session.Store.appraisalBlacklist=new List<string>();
            session.Run.Partner.appraiserLevel=2;
            session.Run.Partner.appraisalBlacklist=new List<string>();
            JArray nativeNames;
            using(session.Enter(NativeActorRun.Slot.Partner))
                nativeNames=new JArray(AppraisalItemRegistry.GetUnlocked(AppraisalRankHelper.GetRank()).Select(type=>
                    new JObject { ["id"]=type.id,["itemId"]=type.itemId,["name"]=type.GetDisplayName() }));
            Require(nativeNames.Count==3 && nativeNames.Cast<JObject>().All(row=>!string.IsNullOrWhiteSpace((string)row["name"]) &&
                (string)row["name"]!=(string)row["id"] && (string)row["name"]!=(string)row["itemId"]),
                "Host native appraisal names are missing or raw identifiers");
            appraisalQaControl.ModifyTag(AppraisalQaNames,tag=>tag.Enable().SetString(nativeNames.ToString(Formatting.None)));
            Record("PASS appraisal-native-name-source",nativeNames.ToString(Formatting.None));
            using(session.Enter(NativeActorRun.Slot.Host)) AppraisalItemsWindow.Open();
            appraisalQaHostWindow=CustomUIManager.Instance.GetWindow("appraisal_items");
            Require(appraisalQaHostWindow?.IsAlive==true && AppraisalQaToggles().Length==1,"Host original rank-zero appraisal window unavailable");
            appraisalQaWatching=true;
            appraisalQaHostStarted=true;
            appraisalQaControl.EnableTag(AppraisalQaReady);
            return;
        }
        Require(ReferenceEquals(CustomUIManager.Instance.GetWindow("appraisal_items"),appraisalQaHostWindow) &&
            appraisalQaHostWindow.IsAlive && AppraisalQaToggles().Length==1,
            "Guest certificate opening or edit replaced the Host original window");
        Require(session.Store.appraiserLevel==0 && session.Store.appraisalBlacklist.Count==0,
            "Partner appraisal edit changed the Host personal fields");
        Require(appraisalQaOriginalWrites<=1,"appraisal request executed the original write more than once");
        if(appraisalQaControl.GetTagReadonly(AppraisalQaStage).GetInt()<2) return;
        Require(appraisalQaOriginalWrites==1 && session.Run.Partner.appraiserLevel==2 &&
            session.Run.Partner.appraisalBlacklist.SequenceEqual(new[]{AppraisalItemRegistry.HAND}),
            "original appraisal toggle did not edit exactly the Partner category list once");
        appraisalQaWatching=false;
        CustomUIManager.Instance.CloseWindow("appraisal_items");
        session.Store.appraiserLevel=appraisalQaHostRank;
        session.Store.appraisalBlacklist=appraisalQaHostBlacklist;
        session.Run.Partner.appraiserLevel=appraisalQaPartnerRank;
        session.Run.Partner.appraisalBlacklist=appraisalQaPartnerBlacklist;
        appraisalQaControl.EnableTag(AppraisalQaDone);
        appraisalQaHostDone=true;
        Record("PASS appraisal-host","one original Partner SetAllowed; Host rank/list/window untouched; invalid edits no writes; QA ranks/lists restored");
    }

    private static Toggle[] AppraisalQaToggles()=>CustomUIManager.Instance.GetWindow("appraisal_items")?.Rect.GetComponentsInChildren<Toggle>(true) ?? Array.Empty<Toggle>();
    private void CheckAppraisalRenderedNames(GameItem control,Toggle[] toggles)
    {
        var expected=JArray.Parse(control.GetTagReadonly(AppraisalQaNames).GetString());
        Require(expected.Count==toggles.Length,"appraisal source/replica row counts differ");
        var evidence=new JArray();
        for(int i=0;i<toggles.Length;i++)
        {
            // AddRow begins a native CustomUIBuilder 'row', then adds its
            // category label as the first child and the Toggle afterwards.
            var row=toggles[i].transform.parent;
            var window=CustomUIManager.Instance.GetWindow("appraisal_items").Rect;
            while(row!=null && row!=window && (row.name!="row" || row.GetComponent<HorizontalLayoutGroup>()==null)) row=row.parent;
            Require(row!=null && row!=window && row.childCount>0,"native appraisal category row hierarchy missing");
            var label=row.GetChild(0).GetComponentInChildren<TextMeshProUGUI>(true);
            string wanted=(string)expected[i]["name"],actual=label?.text;
            Require(label!=null && label.gameObject.activeInHierarchy && actual==wanted &&
                actual!=(string)expected[i]["id"] && actual!=(string)expected[i]["itemId"],
                "appraisal original rendered category differs from Host GetDisplayName: "+expected[i]["id"]+" expected="+wanted+" actual="+actual);
            evidence.Add(new JObject { ["id"]=expected[i]["id"],["source"]=wanted,["rendered"]=actual });
        }
        Record("PASS appraisal-native-name-parity",evidence.ToString(Formatting.None));
    }
    private static string AppraisalQaProof()=>(string)AccessTools.Method(typeof(NativeWorld),"AppraisalProof").Invoke(null,null);
    private static void AppraisalQaClickControl(GameItem control)
    {
        var button=(MachineryButton)((MachineryPanelRow)control.contentWindow.childElement).Panels.Single(panel=>panel.role=="qaAppraisal").content;
        AccessTools.Method(typeof(MachineryButton),"OnSelect").Invoke(button,null);
    }
    private bool RunGuestAppraisalQa(ReplicaLink link,double now)
    {
        if(appraisalQaStep==12) return true;
        if(appraisalQaDeadline==0) appraisalQaDeadline=now+75;
        Require(now<appraisalQaDeadline,"native appraisal QA timed out at "+appraisalQaStep);
        if(QaWaiting(link)) return false;
        var world=NativeWorld.Guest;
        if(world?.Ready!=true) return false;
        var certificate=world.Objects.Values.OfType<GameItem>().SingleOrDefault(item=>item.IsTag(AppraisalQaCertificate));
        var control=world.Objects.Values.OfType<GameItem>().SingleOrDefault(item=>item.IsTag(AppraisalQaControl));
        if(certificate==null || control==null) return false;
        void Replay(JObject command)
        { Require(link.Submit(Encoding.UTF8.GetBytes(command.ToString(Formatting.None)),now),"cannot submit appraisal negative witness"); }
        JObject CurrentProofCommand()
        {
            var command=(JObject)appraisalQaFirstCommand.DeepClone();
            Require(world.TryData(certificate,out var data),"certificate replica data missing");
            command["token"]=data["moveToken"]; command["expected"]=AppraisalQaProof();
            return command;
        }
        switch(appraisalQaStep)
        {
            case 0:
                AppraisalQaClickControl(control); appraisalQaStep=1; break;
            case 1:
                if(!control.IsTag(AppraisalQaReady) || AppraisalRankHelper.GetRank()!=2) return false;
                certificate.ActivateSlotItem();
                Require(world.AppraisalFrontendVisible(certificate) && QaWaiting(link),"certificate did not open the original local window in the input frame");
                appraisalQaStep=2; break;
            case 2:
                var toggles=AppraisalQaToggles();
                Require(toggles.Length==3 && toggles[0].interactable && toggles[1].interactable && !toggles[2].interactable,
                    "Guest original appraisal window did not follow Partner rank/category restrictions");
                CheckAppraisalRenderedNames(control,toggles);
                NativeOffscreenCapture.Capture("qa-native-appraisal-certificate");
                Require(!PlayerStore.Instance.appraisalBlacklist.Contains(AppraisalItemRegistry.HAND),"appraisal fixture started blocked");
                toggles[0].isOn=false;
                Require(!PlayerStore.Instance.appraisalBlacklist.Contains(AppraisalItemRegistry.HAND) && QaWaiting(link),
                    "original Guest Toggle wrote the local confirmed actor state before source acknowledgement");
                appraisalQaStep=3; break;
            case 3:
                if(!PlayerStore.Instance.appraisalBlacklist.Contains(AppraisalItemRegistry.HAND)) return false;
                Require(appraisalQaFirstCommand!=null && !AppraisalQaToggles()[0].isOn,"confirmed appraisal state did not reach original local Toggle");
                Replay((JObject)appraisalQaFirstCommand.DeepClone()); appraisalQaStep=4; break;
            case 4:
                Require(QaResult(link)==(ushort)ShopResult.Stale,"old appraisal proof was accepted");
                var wrongActor=CurrentProofCommand(); wrongActor["actor"]=(int)NativeActorRun.Slot.Host;
                Replay(wrongActor); appraisalQaStep=5; break;
            case 5:
                Require(QaResult(link)==(ushort)ShopResult.Stale,"Partner could submit appraisal edits for Host");
                var locked=CurrentProofCommand(); locked["itemType"]=AppraisalItemRegistry.PLASMA_FUEL;
                Replay(locked); appraisalQaStep=6; break;
            case 6:
                Require(QaResult(link)==(ushort)ShopResult.Unavailable,"native locked appraisal category was writable");
                CustomUIManager.Instance.CloseWindow("appraisal_items");
                appraisalQaCloseSerial=world.LastSubmittedUiRequest; appraisalQaClosedUntil=now+0.8; appraisalQaStep=7; break;
            case 7:
                if(now<appraisalQaClosedUntil) return false;
                Require(!world.AppraisalFrontendVisible(certificate) && world.LastSubmittedUiRequest==appraisalQaCloseSerial,
                    "local appraisal close sent a business command or a snapshot reopened it");
                certificate.ActivateSlotItem(); appraisalQaStep=8; break;
            case 8:
                Require(world.AppraisalFrontendVisible(certificate),"certificate explicit reopen missing");
                Replay(CurrentProofCommand()); appraisalQaStep=9; break;
            case 9:
                Require(QaResult(link)==(ushort)ShopResult.Stale && PlayerStore.Instance.appraisalBlacklist.SequenceEqual(new[]{AppraisalItemRegistry.HAND}),
                    "obsolete appraisal binding edited a reopened window or wrong category");
                CustomUIManager.Instance.CloseWindow("appraisal_items");
                AppraisalQaClickControl(control); appraisalQaStep=10; break;
            case 10:
                if(!control.IsTag(AppraisalQaDone)) return false;
                Record("PASS appraisal-guest","native certificate and original rank-two Toggle; no local confirmed write; stale proof/wrong actor/locked category/old view rejected; independent close/reopen");
                appraisalQaStep=12; return true;
        }
        return false;
    }

    [HarmonyPatch(typeof(AppraisalItemsWindow),"SetAllowed")]
    private static class ObserveAppraisalOriginalWrite
    {
        private static void Postfix(bool __runOriginal)
        {
            if(!Enabled || !IsHostProduct || !appraisalQaWatching || !__runOriginal || NativeGuest.Active) return;
            Require(NativeActorSession.Current?.Run.CurrentActor==NativeActorRun.Slot.Partner,"appraisal source callback lost its initiating actor");
            appraisalQaOriginalWrites++;
        }
    }
}
