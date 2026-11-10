using System;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Core;
using UnityEngine;

namespace ProbablyCoop;

internal sealed partial class NativeBackgroundQa
{
    private readonly bool saveRoundTrip=Array.IndexOf(Environment.GetCommandLineArgs(),"--probably-coop-qa-save")>=0;
    private int dayStep,originalDay;
    private double dayUntil,dayNext;
    private bool hostDayStarted,hostDayVerified;
    private int hostDayTarget;
    private double hostReportCaptureAt;
    private void CheckHostDay(CoopSession session)
    {
        if(!saveRoundTrip || session==null || !eveningFixture || MapUIManager.Instance.IsOutside) return;
        if(PlayerStore.Instance.storeState==PlayerStore.StoreState.CLOSED && StoreStation.Instance.dayCounter!=hostDayTarget)
        { hostDayStarted=false; hostDayVerified=false; }
        if(!hostDayStarted && PlayerStore.Instance.storeState==PlayerStore.StoreState.CLOSED && (hostDayTarget!=0 || PlayerStore.Instance.visitLeftTonight==0))
        {
            originalDay=StoreStation.Instance.dayCounter; hostDayTarget=originalDay; hostDayStarted=true;
            PrepareActorGameplayCheckpointHost();
            EndOfDayUIManager.Instance.OpenUI();
        }
        var link=session.Shop;
        if(hostReportCaptureAt>0 && Time.realtimeSinceStartupAsDouble>=hostReportCaptureAt)
        { CaptureNativeDayUi(session); hostReportCaptureAt=0; }
        if(link.View.Phase==ShopDay.Ready && (link.View.ReadyMask==2 || link.View.ReadyMask==3))
        {
            var button=(TabletButton)AccessTools.Field(typeof(EndOfDayUIManager),"endDayButton").GetValue(EndOfDayUIManager.Instance);
            Require(button?.Interactable==true,"host native day button unavailable");
            button.button.onClick.Invoke();
        }
        // ShopLink refreshes at 5 Hz. Closing the next day's shutter may run
        // this QA tick before it replaces yesterday's Saved view with Ready.
        // A Saved notification only verifies this transition if its day has
        // advanced; retain the exact +1/native-slot checks for a new result.
        if(hostDayStarted && !hostDayVerified && link.View.Phase==ShopDay.Saved && link.View.Day>originalDay)
        {
            Require(link.View.Day==originalDay+1 && StoreStation.Instance.dayCounter==originalDay+1 &&
                CoopRun.VerifyCheckpoint(PlayerStore.Instance,originalDay+1),
                $"native saved checkpoint identity/day mismatch: start={originalDay} view={link.View.Day} native={StoreStation.Instance.dayCounter}");
            hostDayVerified=true;
            CheckActorGameplayCheckpointHost();
            CheckActorNightQa(originalDay);
            Record("PASS host-native-day",$"day={originalDay}->{StoreStation.Instance.dayCounter}; native SaveGame and read-back verified; phase=Saved");
            hostReportCaptureAt=Time.realtimeSinceStartupAsDouble+0.7;
        }
    }
    private bool RunGuestDay(CoopSession session,double now)
    {
        if(!saveRoundTrip) return true;
        if(now<dayNext) return false;
        if(dayStep==0) { originalDay=StoreStation.Instance.dayCounter; dayUntil=now+30; dayStep=1; }
        Require(now<dayUntil,"native end-day flow timed out; phase="+session.Shop.View.Phase);
        if(dayStep==1)
        {
            if(session.Shop.View.Phase!=ShopDay.Ready) return false;
            if(!EndOfDayUIManager.Instance.IsOpen) { NativeDayUi.ShowGuestPlan(); dayNext=now+0.5; return false; }
            var button=(TabletButton)AccessTools.Field(typeof(EndOfDayUIManager),"endDayButton").GetValue(EndOfDayUIManager.Instance);
            Require(button?.Interactable==true,"guest original day button unavailable");
            button.button.onClick.Invoke();
            Require(session.Shop.Waiting,"guest native day readiness did not send request");
            Record("PASS guest-day-ready","original nightly service tablet button, shared readiness"); dayStep=2; return false;
        }
        if(session.Shop.View.Phase==ShopDay.SaveUnverified) throw new InvalidOperationException("native save rejected/unverified");
        if(session.Shop.View.Phase!=ShopDay.Saved || StoreStation.Instance.dayCounter!=originalDay+1) return false;
        if(dayStep==2) { dayStep=3; dayNext=now+1; return false; }
        Require(AdvCalendarUIManager.Instance.UIPanel.activeInHierarchy && AdvCalendarUIManager.Instance.nightlyReportSection.activeInHierarchy,"guest current native morning report tab missing");
        Require(PlayerStore.Instance.saveSlotId==-1,"guest gained save identity");
        if(NativeWorld.Guest.HasPendingWork) return false;
        CheckActorGameplayCheckpointGuest();
        var expected=NativeWorld.Guest.MorningReport.Select(line=>"-"+line).ToArray();
        var actual=StartOfDayUIManager.Instance.contentGroupObject.GetComponentsInChildren<TMPro.TextMeshProUGUI>().Where(t=>t.gameObject.activeInHierarchy).Select(t=>t.text).ToArray();
        if(!expected.SequenceEqual(actual)) Record("TRACE report-mismatch",Newtonsoft.Json.JsonConvert.SerializeObject(new { expected,actual,reportDay=NativeWorld.Guest.MorningReportDay }));
        Require(expected.Length>0 && expected.SequenceEqual(actual),"native morning report lost lines or formatting");
        Record("PASS native-full-morning-report","all "+expected.Length+" host lines rendered by original guest panel, including rich text");
        Record("PASS guest-native-day",$"day={originalDay}->{StoreStation.Instance.dayCounter}; original morning report and cash={PlayerStore.Instance.playerCash}; no guest save");
        CaptureNativeDayUi(session);
        NativeOffscreenCapture.Capture("qa-day"); return true;
    }
    private void CaptureNativeDayUi(CoopSession connection)
    {
        int day=StoreStation.Instance.dayCounter;
        Require(AdvCalendarUIManager.Instance.nightlyReportSection.activeInHierarchy,"native report is not actually visible");
        NativeOffscreenCapture.Capture("qa-day-"+day);
        var data=new Newtonsoft.Json.Linq.JObject { ["day"]=day,["cash"]=PlayerStore.Instance.playerCash,
            ["ui"]=NativeNegotiationProbe.Capture(AdvCalendarUIManager.Instance.UIPanel.transform) };
        JObject personal=null;
        if(host && NativeActorSession.Current is NativeActorSession actors)
        {
            Require(Enabled && IsHostProduct && ReferenceEquals(actors.Store,PlayerStore.Instance) &&
                !actors.Hydrating && !actors.NightActive && !actors.TransitionActive && !actors.Faulted && !actors.Terminal &&
                actors.VerifiedCheckpoint?.Day==day && actors.VerifiedCheckpoint.Run==actors.Run.Run &&
                actors.VerifiedCheckpoint.Slot==actors.NativeSaveSlot,
                "nightly Host actor snapshot is not its current verified checkpoint");
            personal=actors.Run.CapturePersonalCheckpoint();
            Require(JToken.DeepEquals(personal,actors.VerifiedCheckpoint.Personal),
                "nightly Host personal state changed after the observed native save");
        }
        else if(!host && NativeWorld.Guest?.HasActorProjection==true)
        {
            var world=NativeWorld.Guest;
            Require(Enabled && IsGuestProduct && world.Ready && !world.HasPendingWork &&
                connection?.State==LinkState.Connected && connection.Native.State!=null &&
                PlayerStore.Instance.saveSlotId==-1 && world.RemoteHostActor!=null &&
                ReferenceEquals(EmporiumEntry.Instance.afterhourPocketSlotInvBackpack,
                    world.ActorCarry.Get(NativeActorRun.Slot.Partner,NativeActorInventory.CarrySlot.Backpack)),
                "nightly Guest actor snapshot is not the current slot-free Partner frontend");
            var join=NativeJoinSnapshot.Read(connection.Native.State,connection.Native.FullState,connection.Native.FullRevision);
            Require(!join.Required,"nightly Guest partner character was not configured");
            personal=new JObject { ["schema"]=1,["run"]=join.Run.ToString("D"),
                ["actors"]=new JArray(world.RemoteHostActor.Serialize(),NativeActorState.Capture(PlayerStore.Instance).Serialize()) };
        }
        // This object is expected to be identical in both real processes, so
        // the existing day/UI comparer remains valid. Role/slot truth is
        // checked above and recorded separately, never normalized away.
        if(personal!=null) data["actorPersonal"]=personal;
        ModHelper.WriteModDataFileText("probably_coop","qa-day-"+day+".json",data.ToString());
        if(personal!=null) Record("PASS actor-night-personal-snapshot","day="+day+" role="+(host ? "Host" : "Guest")+
            " run="+(string)personal["run"]+" nativeSlot="+PlayerStore.Instance.saveSlotId+
            "; current native Host and Partner personal fields; Host also paired with verified save");
    }
}
