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
    private bool appraisalQaHostBlocked;
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
            session.Store.appraiserLevel=2;
            session.Store.appraisalBlacklist=new List<string>();
            session.Run.Partner.appraiserLevel=0;
            session.Run.Partner.appraisalBlacklist=new List<string>();
            JArray nativeNames;
            using(session.Enter(NativeActorRun.Slot.Host))
                nativeNames=new JArray(AppraisalItemRegistry.GetUnlocked(AppraisalRankHelper.GetRank()).Select(type=>
                    new JObject { ["id"]=type.id,["itemId"]=type.itemId,["name"]=type.GetDisplayName() }));
            Require(nativeNames.Count==3 && nativeNames.Cast<JObject>().All(row=>!string.IsNullOrWhiteSpace((string)row["name"]) &&
                (string)row["name"]!=(string)row["id"] && (string)row["name"]!=(string)row["itemId"]),
                "Host native appraisal names are missing or raw identifiers");
            appraisalQaControl.ModifyTag(AppraisalQaNames,tag=>tag.Enable().SetString(nativeNames.ToString(Formatting.None)));
            Record("PASS appraisal-native-name-source",nativeNames.ToString(Formatting.None));
            using(session.Enter(NativeActorRun.Slot.Host)) AppraisalItemsWindow.Open();
            Require(CustomUIManager.Instance.IsOpen("appraisal_items") && AppraisalQaToggles().Length==3,
                "Host original rank-two appraisal window unavailable");
            appraisalQaWatching=true;
            appraisalQaHostStarted=true;
            appraisalQaControl.EnableTag(AppraisalQaReady);
            return;
        }
        Require(CustomUIManager.Instance.IsOpen("appraisal_items") && AppraisalQaToggles().Length==3 &&
            session.Store.appraiserLevel==2 && session.Run.Partner.appraiserLevel==0 && session.Run.Partner.appraisalBlacklist.Count==0,
            "appraisal panel diverged or changed personal qualifications");
        Require(appraisalQaOriginalWrites<=2,"an appraisal toggle executed more than once");
        int phase=appraisalQaControl.GetTagReadonly(AppraisalQaStage).GetInt();
        if(phase>=2 && !appraisalQaHostBlocked)
        {
            AppraisalQaToggles()[0].isOn=false;
            Require(session.Store.appraisalBlacklist.SequenceEqual(new[]{AppraisalItemRegistry.HAND}) &&
                !AppraisalItemRegistry.GetPool(2).Any(type=>type.id==AppraisalItemRegistry.HAND),
                "Host original toggle did not change the shared customer pool");
            appraisalQaHostBlocked=true;
        }
        if(phase<3) return;
        Require(appraisalQaOriginalWrites==2 && session.Store.appraisalBlacklist.Count==0 && AppraisalQaToggles()[0].isOn &&
            AppraisalItemRegistry.GetPool(2).Any(type=>type.id==AppraisalItemRegistry.HAND),
            "Partner toggle did not restore the shared pool and the open Host panel");
        appraisalQaWatching=false;
        CustomUIManager.Instance.CloseWindow("appraisal_items");
        session.Store.appraiserLevel=appraisalQaHostRank;
        session.Store.appraisalBlacklist=appraisalQaHostBlacklist;
        session.Run.Partner.appraiserLevel=appraisalQaPartnerRank;
        session.Run.Partner.appraisalBlacklist=appraisalQaPartnerBlacklist;
        appraisalQaControl.EnableTag(AppraisalQaDone);
        appraisalQaHostDone=true;
        Record("PASS appraisal-host","one original toggle per player; shared category pool and open Host panel updated; distinct personal ranks retained; QA fields restored");
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
    private static string AppraisalQaProof()=>(string)AccessTools.Method(typeof(NativeWorld),"AppraisalProof").Invoke(NativeWorld.Guest,null);
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
                if(!control.IsTag(AppraisalQaReady) || world.RemoteHostActor?.appraiserLevel!=2) return false;
                Require(AppraisalRankHelper.GetRank()==0,"guest personal rank was replaced by the shop rank");
                certificate.ActivateSlotItem();
                Require(world.AppraisalFrontendVisible(certificate) && QaWaiting(link),"certificate did not open the original local window in the input frame");
                appraisalQaStep=2; break;
            case 2:
                var toggles=AppraisalQaToggles();
                Require(toggles.Length==3 && toggles[0].interactable && toggles[1].interactable && !toggles[2].interactable && toggles[0].isOn,
                    "Guest native panel did not render the shop qualification and allowed categories");
                CheckAppraisalRenderedNames(control,toggles);
                AppraisalQaClickControl(control);
                appraisalQaStep=11; break;
            case 11:
                if(!world.RemoteHostActor.appraisalBlacklist.Contains(AppraisalItemRegistry.HAND)) return false;
                Require(world.AppraisalFrontendVisible(certificate) && !AppraisalQaToggles()[0].isOn && AppraisalRankHelper.GetRank()==0,
                    "Host change did not refresh an already open Guest toggle or overwrote the personal rank");
                NativeOffscreenCapture.Capture("qa-native-appraisal-certificate");
                Record("PASS bug-09-host-appraisal-refresh","Host native toggle changed the already open Guest original panel");
                AppraisalQaToggles()[0].isOn=true;
                Require(world.RemoteHostActor.appraisalBlacklist.Contains(AppraisalItemRegistry.HAND) &&
                    PlayerStore.Instance.appraisalBlacklist.Count==0 && QaWaiting(link),
                    "Guest toggle edited confirmed settings before the authoritative result");
                appraisalQaStep=3; break;
            case 3:
                if(world.RemoteHostActor.appraisalBlacklist.Contains(AppraisalItemRegistry.HAND)) return false;
                Require(appraisalQaFirstCommand!=null && AppraisalQaToggles()[0].isOn,"confirmed appraisal state did not reach original local Toggle");
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
                Require(QaResult(link)==(ushort)ShopResult.Stale && world.RemoteHostActor.appraisalBlacklist.Count==0,
                    "obsolete appraisal binding edited a reopened window or wrong category");
                CustomUIManager.Instance.CloseWindow("appraisal_items");
                AppraisalQaClickControl(control); appraisalQaStep=10; break;
            case 10:
                if(!control.IsTag(AppraisalQaDone)) return false;
                Record("PASS appraisal-guest","native certificate and shop rank-two toggles with personal rank zero; host changes visible live; stale proof/wrong actor/locked category/old view rejected; local close respected");
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
            Require(NativeActorSession.Current?.Run.CurrentActor==NativeActorRun.Slot.Host,"appraisal write did not run in the shop owner context");
            appraisalQaOriginalWrites++;
        }
    }
}
