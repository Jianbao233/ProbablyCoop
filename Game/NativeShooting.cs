using System;
using System.Collections;
using System.IO;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Core;
using UnityEngine;

namespace ProbablyCoop;

// One shared incident, with native presentation on both peers. A phase revision
// is also a command proof: a fresh request ID cannot repeat a consumed action.
internal sealed partial class NativeWorld
{
    internal enum ShootingPhase { Idle, Entering, Aiming, Fired, Leaving, Police, Complete }
    private ulong shootingEpisode, shootingRevision;
    private ShootingPhase shootingPhase;
    private NativeActorRun.Slot shootingActor;
    private StoreClientInstance shootingCustomer;
    private uint shootingCustomerId;
    private bool shootingFired, shootingPoliceStarted;
    private NativeActorActivities.Lease shootingActivity;
    private JObject shootingReplica;
    private ulong presentedShootingEpisode;
    private bool guestShootingEntered, guestShootingFired, guestShootingLeaving;
    private int shootingPresentationGeneration;
    internal bool ShootingOwnsCustomer => host && shootingPhase is > ShootingPhase.Idle and < ShootingPhase.Complete;
    internal ShootingPhase ShootingState => host ? shootingPhase : shootingReplica==null ? ShootingPhase.Idle : (ShootingPhase)(int)shootingReplica["phase"];
    internal ulong ShootingEpisode => host ? shootingEpisode : (ulong?)shootingReplica?["episode"] ?? 0;
    private NativeActorRun.Slot ShootingCaller => ReferenceEquals(RemoteUiExecution,this)
        ? NativeActorRun.Slot.Partner : NativeActorSession.Current?.Run.CurrentActor ?? NativeActorRun.Slot.Host;

    private void ChangeShooting(ShootingPhase phase)
    { shootingPhase=phase; shootingRevision=checked(shootingRevision+1); nextCapture=0; }
    private void CompleteShooting()
    {
        if(!ShootingOwnsCustomer) return;
        ChangeShooting(ShootingPhase.Complete);
        shootingActivity?.Dispose(); shootingActivity=null;
    }
    private bool ShootingActivityAlive(ulong episode)
    {
        if(closed || shootingEpisode!=episode || !ShootingOwnsCustomer) return false;
        if(shootingFired || ReferenceEquals(PlayerStore.Instance.currentClientInstance,shootingCustomer)) return true;
        // Check at EACH delayed invocation, not just the next network tick.
        // Native Exit's blackscreen callback dismisses the current customer;
        // an incident for a replaced target must never reach that callback.
        CompleteShooting();
        var ui=StoreUIManager.Instance;
        if(ui!=null && ui.isShootingMode) RestoreShootingCamera(ui);
        return false;
    }
    private void ObserveShooting()
    {
        if(!host || !ShootingOwnsCustomer) return;
        if(!ShootingActivityAlive(shootingEpisode)) return;
        var ui=StoreUIManager.Instance;
        if(shootingPhase==ShootingPhase.Entering && ui.isShootingMode && ui.shootingUI.activeSelf)
            ChangeShooting(ShootingPhase.Aiming);
    }
    private void TickShootingAuthority()=>ShootingConnectionChanged(peerConnected);
    internal void ShootingConnectionChanged(bool connected)
    {
        if(!host || closed) return;
        ObserveShooting();
        // A departed partner must not strand the host in the aiming camera.
        // A fired incident, however, finishes its original police consequences.
        if(ShootingOwnsCustomer && shootingActor==NativeActorRun.Slot.Partner && !connected &&
            shootingPhase==ShootingPhase.Aiming)
        {
            using(NativeActorSession.Current?.Enter(NativeActorRun.Slot.Partner)) RunShootingAction("exit");
        }
    }
    private JObject CaptureShooting()
    {
        ObserveShooting();
        var ui=StoreUIManager.Instance;
        return new JObject { ["episode"]=shootingEpisode,["revision"]=shootingRevision,["phase"]=(int)shootingPhase,
            ["actor"]=(int)shootingActor,["customer"]=shootingCustomerId,["fired"]=shootingFired,
            ["body"]=ui.body.activeSelf,["unlocked"]=PlayerStore.Instance.isShootingUnlocked,
            ["canPickup"]=NativeShootingHooks.OriginalCanPickup(ui.shotgun.GetComponent<Shotgun>()) };
    }
    internal JObject ShootingCommand(string action)
    {
        if(host || !Ready || shootingReplica==null || action is not ("enter" or "exit" or "fire")) return null;
        var result=new JObject { ["kind"]="shooting",["action"]=action,["episode"]=shootingReplica["episode"],
            ["revision"]=shootingReplica["revision"],["client"]=replicaCustomerId };
        SealOperation(result); return result;
    }
    internal bool RequestShooting(string action)
    {
        var command=ShootingCommand(action);
        return command!=null && Submit(command);
    }
    private ushort ExecuteShooting(JObject command)
    {
        if(command["episode"]?.Type!=JTokenType.Integer || !ulong.TryParse(command["episode"].ToString(),out ulong episode) ||
            command["revision"]?.Type!=JTokenType.Integer || !ulong.TryParse(command["revision"].ToString(),out ulong revision) ||
            command["client"]?.Type!=JTokenType.Integer || !uint.TryParse(command["client"].ToString(),out uint client) ||
            command["action"]?.Type!=JTokenType.String) return (ushort)ShopResult.Unavailable;
        ObserveShooting();
        if(episode!=shootingEpisode || revision!=shootingRevision || client==0 ||
            client!=model.Id(PlayerStore.Instance.currentClientInstance)) return (ushort)ShopResult.Stale;
        return RunShootingAction((string)command["action"]);
    }
    internal ushort RunShootingAction(string action)
    {
        if(!host || closed || action is not ("enter" or "exit" or "fire")) return (ushort)ShopResult.Unavailable;
        ObserveShooting();
        var store=PlayerStore.Instance; var ui=StoreUIManager.Instance; var session=NativeActorSession.Current;
        if(shop?.Settling==true || EndingActive || session?.Faulted==true || session?.Terminal==true ||
            MapUIManager.Instance.IsOutside || store.storeState!=PlayerStore.StoreState.OPEN ||
            (bool)AccessTools.Field(typeof(MapUIManager),"inTransit").GetValue(MapUIManager.Instance)) return (ushort)ShopResult.Busy;
        if(store.currentClientInstance==null || !store.isClientArrived || !store.isShootingUnlocked) return (ushort)ShopResult.Unavailable;
        if(action=="enter")
        {
            if(ShootingOwnsCustomer || ui.isShootingMode || ui.isMagnifyingMode || store.isHandlingShootout) return (ushort)ShopResult.Busy;
            if(!NativeShootingHooks.OriginalCanPickup(ui.shotgun.GetComponent<Shotgun>())) return (ushort)ShopResult.Unavailable;
        }
        else if(!ShootingOwnsCustomer || !ReferenceEquals(shootingCustomer,store.currentClientInstance) ||
            shootingPhase!=ShootingPhase.Aiming || shootingActor!=ShootingCaller) return (ushort)ShopResult.Stale;
        // Native Exit and Shoot destroy non-owned counter graphs. Both local
        // hands, including nested parts/containers, must have released them.
        var held=CaptureHostHand(true);
        if(EmporiumEntry.Instance.GetAllNonOwnedItem().Any(held.TouchesItem)) return (ushort)ShopResult.Busy;
        if(action=="enter")
        {
            shootingEpisode=checked(shootingEpisode+1); shootingActor=ShootingCaller;
            shootingCustomer=store.currentClientInstance; shootingCustomerId=model.Id(shootingCustomer);
            shootingFired=shootingPoliceStarted=false; ChangeShooting(ShootingPhase.Entering);
            ulong episode=shootingEpisode;
            shootingActivity=session?.Activities.Begin(this,"shotgun-incident",shootingActor,
                ()=>ShootingActivityAlive(episode),true);
        }
        else if(action=="fire") { shootingFired=true; ChangeShooting(ShootingPhase.Fired); }
        else ChangeShooting(ShootingPhase.Leaving);
        try
        {
            using(shootingActivity?.EnterNative())
            using(NativeShootingHooks.InvokeSource())
            {
                if(action=="enter") ui.EnterShootingMode();
                else if(action=="fire") ui.Shoot();
                else ui.ExitShootingMode();
            }
        }
        catch(Exception error) { session?.MarkFault(error); throw; }
        return (ushort)ShopResult.Applied;
    }
    internal bool CanUseShootingInput(bool fire)
    {
        if(host) return ShootingOwnsCustomer && shootingActor==ShootingCaller && shootingPhase==ShootingPhase.Aiming;
        return Ready && shootingReplica!=null && (int)shootingReplica["actor"]==(int)NativeActorRun.Slot.Partner &&
            ShootingState==ShootingPhase.Aiming && !guestShootingFired && (fire ? guestShootingEntered : true);
    }
    internal bool CanPickupShooting()
    {
        if(host) return !ShootingOwnsCustomer || CanUseShootingInput(false);
        return Ready && shootingReplica!=null && (bool)shootingReplica["unlocked"] &&
            (ShootingState is ShootingPhase.Idle or ShootingPhase.Complete ? (bool)shootingReplica["canPickup"] : CanUseShootingInput(false));
    }
    internal IDisposable EnterShootingContinuation() => ShootingOwnsCustomer ? shootingActivity?.EnterNative() : null;
    internal bool BeginNativeShootout()
    {
        if(!ShootingOwnsCustomer) return true; // unmodified native single-player source
        if(!shootingFired || shootingPoliceStarted) return false;
        shootingPoliceStarted=true; ChangeShooting(ShootingPhase.Police); return true;
    }
    internal bool IsNativeBodyExit => ShootingOwnsCustomer && shootingFired &&
        shootingActivity!=null && NativeActorSession.Current?.Activities.Current==shootingActivity;
    internal bool IsNativeShootingCleanup=>host && !closed && ShootingOwnsCustomer && shootingFired && shootingPoliceStarted;
    internal bool MayDismissShotCustomer(PlayerStore store)=>IsNativeShootingCleanup &&
        ReferenceEquals(store,NativeActorSession.Current?.Store) && ReferenceEquals(store.currentClientInstance,shootingCustomer);
    internal void RecordNativeBodyExit() { if(shootingPhase==ShootingPhase.Fired) ChangeShooting(ShootingPhase.Leaving); }
    internal void ShootingOfficerLeft(string id) { if(id=="SEC" && shootingPhase==ShootingPhase.Police) CompleteShooting(); }
    internal IEnumerator BindShootingAnimation(IEnumerator iterator)
    {
        if(host) return shootingActivity?.WrapEnumerator(iterator) ?? iterator;
        return new ShootingAnimation(this,iterator,shootingPresentationGeneration);
    }
    private sealed class ShootingAnimation : IEnumerator
    {
        private readonly NativeWorld owner; private readonly IEnumerator native; private readonly int generation;
        public object Current=>native.Current;
        internal ShootingAnimation(NativeWorld owner,IEnumerator native,int generation) {this.owner=owner;this.native=native;this.generation=generation;}
        public bool MoveNext()
        {
            if(owner.closed || owner.shootingPresentationGeneration!=generation) return false;
            using(NativeShootingHooks.Present(owner)) return native.MoveNext();
        }
        public void Reset()=>throw new NotSupportedException();
    }
    internal Action BindShootingPresentation(Action callback)
    {
        if(callback==null) return null;
        int generation=shootingPresentationGeneration;
        return ()=> { if(closed || generation!=shootingPresentationGeneration) return; using(NativeShootingHooks.Present(this)) callback(); };
    }
    private void ApplyShooting(JObject data)
    {
        if(data==null || data["phase"]?.Type!=JTokenType.Integer || !Enum.IsDefined(typeof(ShootingPhase),(int)data["phase"]) ||
            data["actor"]?.Type!=JTokenType.Integer || (int)data["actor"] is <0 or >1 ||
            data["episode"]?.Type!=JTokenType.Integer || data["revision"]?.Type!=JTokenType.Integer ||
            data["customer"]?.Type!=JTokenType.Integer || data["fired"]?.Type!=JTokenType.Boolean ||
            data["body"]?.Type!=JTokenType.Boolean || data["unlocked"]?.Type!=JTokenType.Boolean || data["canPickup"]?.Type!=JTokenType.Boolean)
            throw new InvalidDataException("Invalid shared shooting incident");
        _=(ulong)data["episode"]; _=(ulong)data["revision"]; _=(uint)data["customer"];
        shootingReplica=data;
        PlayerStore.Instance.isShootingUnlocked=(bool)data["unlocked"];
        Shotgun.LoadShotgun();
        TickShootingPresentation();
    }
    private void TickShootingPresentation()
    {
        if(host || closed || shootingReplica==null || StoreUIManager.Instance==null) return;
        var ui=StoreUIManager.Instance; var phase=ShootingState; ulong episode=ShootingEpisode;
        if(episode!=presentedShootingEpisode)
        {
            ResetShootingPresentation(); presentedShootingEpisode=episode;
            // Reconnecting after an incident must not replay a completed kill.
            if(phase is ShootingPhase.Idle or ShootingPhase.Complete) return;
        }
        if(phase==ShootingPhase.Idle) return;
        if(!guestShootingEntered && phase!=ShootingPhase.Complete && PlayerStore.Instance.currentClientInstance!=null &&
            replicaCustomerId==(uint)shootingReplica["customer"])
        {
            guestShootingEntered=true;
            using(NativeShootingHooks.Present(this)) ui.EnterShootingMode();
        }
        if(guestShootingEntered && ui.isShootingMode && ui.shootingUI.activeSelf && !guestShootingFired && !guestShootingLeaving)
        {
            if((bool)shootingReplica["fired"])
            { guestShootingFired=true; using(NativeShootingHooks.Present(this)) ui.Shoot(); }
            else if(phase is ShootingPhase.Leaving or ShootingPhase.Complete)
            { guestShootingLeaving=true; using(NativeShootingHooks.Present(this)) ui.ExitShootingMode(); }
        }
        // Native wound/body routines own their frames; the source only decides
        // whether the corpse remains after local animation has finished.
        if(!guestShootingFired || guestShootingLeaving && !ui.isShootingMode)
            ui.body.SetActive((bool)shootingReplica["body"]);
        if(phase==ShootingPhase.Complete && guestShootingLeaving && !ui.isShootingMode)
        {ui.isShot=false;ui.shotClientInstance=null;}
    }
    internal void GuestBodyExit() => guestShootingLeaving=true;
    private void ResetShootingPresentation()
    {
        shootingPresentationGeneration++;
        var ui=StoreUIManager.Instance;
        if(ui!=null && guestShootingEntered)
            RestoreShootingCamera(ui);
        guestShootingEntered=guestShootingFired=guestShootingLeaving=false;
    }
    private static void RestoreShootingCamera(StoreUIManager ui)
    {
        ui.isShootingMode=false; ui.isShot=false; ui.shotClientInstance=null;
        ui.mainCamera.SetActive(true); ui.shootingCamera.SetActive(false); ui.arm.SetActive(false);
        ui.inventoryCanvas.SetActive(true); UIFamilyLayer.SetRaisedCanvasActive(true); ui.worldInteractCanvas.SetActive(true);
        ui.paperUICanvasObject.gameObject.SetActive(true); ui.shootingUI.SetActive(false);
        ui.shootingWoundRenderer.gameObject.SetActive(false); ui.shootingBodyRenderer.gameObject.SetActive(false);
        DialogUIManager.Instance.SetNormalMode();
    }
    private void CloseShooting()
    {
        shootingActivity?.Dispose(); shootingActivity=null;
        if(!host) ResetShootingPresentation();
    }
}
