using System;
using DG.Tweening;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Core;
using UnityEngine;

namespace ProbablyCoop;

internal sealed partial class NativeBackgroundQa
{
    private readonly bool boundaryRecovery=Array.IndexOf(Environment.GetCommandLineArgs(),"--probably-coop-qa-boundary-recovery")>=0;
    private static NativeBackgroundQa boundarySource;
    private int boundaryStep,boundaryJoins,boundaryDrops,boundaryArrivals,boundaryNights,boundaryBegins,boundarySaves;
    private int boundaryCaptureFrame;
    private bool boundaryWasConnected,boundarySourceWalkDrop,boundarySourceDayDrop;
    private double boundaryWaitUntil,boundaryWalkElapsed;
    private string boundaryCode;
    private NativeWorld boundaryOldWorld;
    private JObject boundaryWalkStamp;
    private static readonly System.Reflection.FieldInfo BoundaryHostMotion=AccessTools.Field(typeof(NativeWorld),"customerMotion");
    private static readonly System.Reflection.FieldInfo BoundaryGuestMotion=AccessTools.Field(typeof(NativeWorld),"appliedMotionPhase");
    private static NativeWorld.CustomerMotion BoundaryMotion(NativeWorld world,bool source)=>source ?
        (NativeWorld.CustomerMotion)BoundaryHostMotion.GetValue(world) : (NativeWorld.CustomerMotion)BoundaryGuestMotion.GetValue(world);
    private void CheckBoundarySource(RoomSelector room)
    {
        if(!boundaryRecovery) return;
        boundarySource=this;
        bool connected=room.Session?.State==LinkState.Connected;
        if(connected && !boundaryWasConnected) boundaryJoins++;
        if(!connected && boundaryWasConnected)
        {
            boundaryDrops++;
            bool walk=PlayerStore.Instance.currentClientInstance?.GetClientBlueprint().identifier=="mentor" &&
                BoundaryMotion(NativeWorld.Host,true)==NativeWorld.CustomerMotion.Approaching;
            bool settling=NativeShop.Gate?.Settling==true;
            boundarySourceWalkDrop|=walk; boundarySourceDayDrop|=settling;
            Record("TRACE boundary-source-drop","drop="+boundaryDrops+" mentorApproaching="+walk+" settling="+settling+" nativeDay="+StoreStation.Instance.dayCounter);
        }
        boundaryWasConnected=connected;
    }
    private bool TickBoundaryGuest(RoomSelector room,NativeGuest guest,bool inMenu,double now)
    {
        if(!boundaryRecovery || host) return false;
        if(boundaryCaptureFrame!=0)
        {
            if(Time.frameCount<=boundaryCaptureFrame) return true;
            var credit=UnityEngine.Object.FindObjectOfType<StoreCreditDisplay>();
            Require(credit?.creditDisplayText?.text==PlayerStore.Instance.GetCash()+GeneralHelper.CreditIcon(),
                "original credit display did not reflect restored cash by its next update frame");
            NativeOffscreenCapture.Capture("qa-boundary-approach-after"); boundaryCaptureFrame=0;
            Record("PASS boundary-native-credit-frame","original credit Update displays restored authoritative cash by next frame; no replacement UI writer");
        }
        if(boundaryStep is 1 or 3)
        {
            Require(now<boundaryWaitUntil,"real boundary reconnect timed out at "+boundaryStep);
            if(inMenu && !room.HasLobby && !room.Busy) room.Join(boundaryCode);
            var world=NativeWorld.Guest;
            if(guest==null || world?.Ready!=true || ReferenceEquals(world,boundaryOldWorld) || room.Session?.State!=LinkState.Connected) return true;
            Require(PlayerStore.Instance.saveSlotId==-1,"boundary client loaded a native slot");
            Require(world.AuditProjection(out var audit),audit);
            if(boundaryStep==1)
            {
                Require(PlayerStore.Instance.currentClientInstance?.GetClientBlueprint().identifier=="mentor" &&
                    JToken.DeepEquals(boundaryWalkStamp,ReconnectStamp()),"rejoining approach replaced the customer or changed committed inventory/cash/day");
                var path=StoreClientMono.Instance.GetComponent<DOTweenPath>();
                var phase=BoundaryMotion(world,false);
                Require(phase==NativeWorld.CustomerMotion.Approaching && path.tween!=null && path.tween.Elapsed(false)>=boundaryWalkElapsed &&
                    path.tween.Elapsed(false)<path.tween.Duration(false),"client did not restore inside the original approaching path; phase="+phase);
                Record("PASS native-mid-approach-rejoin","actual room Leave/menu/Join and fresh NativeWorld while same mentor still approaches; original tween seeks "+
                    boundaryWalkElapsed.ToString("F3")+"->"+path.tween.Elapsed(false).ToString("F3")+" / "+path.tween.Duration(false).ToString("F3")+"s; committed entity graph unchanged; "+audit);
                boundaryCaptureFrame=Time.frameCount;
                boundaryStep=2;
            }
            else
            {
                Require(room.Session.Shop.View.Phase is ShopDay.Settling or ShopDay.Saved,"settlement rejoin lost its authoritative phase");
                Record("PASS native-settlement-rejoin","actual room Leave during Settling, native menu/Join and fresh model restored authoritative phase="+room.Session.Shop.View.Phase+"; guest slot remains -1");
                boundaryStep=4;
            }
            boundaryOldWorld=null;
            return boundaryCaptureFrame!=0;
        }
        if(guest==null || NativeWorld.Guest?.Ready!=true || room.Session?.State!=LinkState.Connected) return false;
        if(boundaryStep==0 && PlayerStore.Instance.currentClientInstance?.GetClientBlueprint().identifier=="mentor" &&
            BoundaryMotion(NativeWorld.Guest,false)==NativeWorld.CustomerMotion.Approaching)
        {
            var path=StoreClientMono.Instance.GetComponent<DOTweenPath>();
            if(path.tween==null || path.tween.Elapsed(false)<0.1) return false;
            boundaryWalkElapsed=path.tween.Elapsed(false); boundaryWalkStamp=ReconnectStamp();
            NativeOffscreenCapture.Capture("qa-boundary-approach-before");
            BeginBoundaryLeave(room,1,now); return true;
        }
        if(boundaryStep==2 && room.Session.Shop.View.Phase==ShopDay.Settling)
        {
            Record("TRACE boundary-guest-settling","nativeDay="+StoreStation.Instance.dayCounter+" shopDay="+room.Session.Shop.View.Day);
            BeginBoundaryLeave(room,3,now); return true;
        }
        return false;
    }
    private void BeginBoundaryLeave(RoomSelector room,int step,double now)
    {
        boundaryCode=room.Code; boundaryOldWorld=NativeWorld.Guest;
        // Use the advertised join endpoint from the launcher, including its real
        // test relay; Room.Code may be the authority's unrelayed address.
        var args=Environment.GetCommandLineArgs(); int index=Array.IndexOf(args,"--probably-coop-join");
        if(index>=0 && index+1<args.Length) boundaryCode=args[index+1];
        boundaryWaitUntil=now+20; boundaryStep=step;
        Record("TRACE boundary-guest-leave","step="+step+" scene="+UnityEngine.SceneManagement.SceneManager.GetActiveScene().name);
        room.Leave();
    }
    private void VerifyBoundarySource()
    {
        if(!boundaryRecovery) return;
        Require(boundaryJoins==3 && boundaryDrops==2 && boundarySourceWalkDrop && boundarySourceDayDrop &&
            boundaryArrivals==1 && boundaryNights==1 && boundaryBegins==1 && boundarySaves==1,
            "source boundary effects/phase mismatch: joins="+boundaryJoins+" drops="+boundaryDrops+" walkDrop="+boundarySourceWalkDrop+
            " dayDrop="+boundarySourceDayDrop+" mentorArrive="+boundaryArrivals+" endNight="+boundaryNights+" beginDay2="+boundaryBegins+" saveDay2="+boundarySaves);
        Record("PASS source-boundary-exactly-once","three real connections, source observed disconnect inside approach and Settling; mentor arrival, native EndNight, BeginDay2 and SaveGame(day2) each once");
    }
    private void VerifyBoundaryGuest()
    {
        if(!boundaryRecovery) return;
        Require(boundaryStep==4,"fresh flow omitted required mid-walk or settling rejoin");
        Record("PASS boundary-native-guest","two real room reconnects rebuilt original frontends inside approach/settlement; first full day report and source/guest facts compared separately");
    }
    private static bool BoundarySourceOracle=>Enabled && IsHostProduct && boundarySource?.boundaryRecovery==true;
    [HarmonyPatch(typeof(StoreUIManager),nameof(StoreUIManager.OnNextClientArrived))]
    private static class BoundaryArriveOracle
    {
        private static void Postfix()
        { if(BoundarySourceOracle && PlayerStore.Instance.currentClientInstance?.GetClientBlueprint().identifier=="mentor") boundarySource.boundaryArrivals++; }
    }
    [HarmonyPatch(typeof(PlayerStore),nameof(PlayerStore.EndNight))]
    private static class BoundaryNightOracle
    { private static void Postfix() { if(BoundarySourceOracle) boundarySource.boundaryNights++; } }
    [HarmonyPatch(typeof(PlayerStore),nameof(PlayerStore.BeginDay))]
    private static class BoundaryBeginOracle
    { private static void Postfix() { if(BoundarySourceOracle && StoreStation.Instance.dayCounter==2) boundarySource.boundaryBegins++; } }
    [HarmonyPatch(typeof(PlayerStore),nameof(PlayerStore.SaveGame))]
    private static class BoundarySaveOracle
    { private static void Postfix() { if(BoundarySourceOracle && StoreStation.Instance.dayCounter==2) boundarySource.boundarySaves++; } }
}
