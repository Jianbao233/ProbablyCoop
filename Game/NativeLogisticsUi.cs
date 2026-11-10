using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UI;

namespace ProbablyCoop;

internal static class NativeLogisticsUi
{
    private static Type Helper(NativeWorld.FrontendKind kind)=>kind switch {
        NativeWorld.FrontendKind.Hopper=>typeof(HopperHelper),
        NativeWorld.FrontendKind.StorageCoordinator=>typeof(MachineStorageCoordinator),
        NativeWorld.FrontendKind.MindReprogrammer=>typeof(MindReprogrammerHelper),
        _=>throw new ArgumentOutOfRangeException(nameof(kind)) };
    private static string WindowId(NativeWorld.FrontendKind kind)=>kind switch {
        NativeWorld.FrontendKind.Hopper=>"intake_hopper",
        NativeWorld.FrontendKind.StorageCoordinator=>"storage_coordinator",
        NativeWorld.FrontendKind.MindReprogrammer=>"mind_reprogrammer",
        _=>throw new ArgumentOutOfRangeException(nameof(kind)) };
    internal static GameItem OpenItem(NativeWorld.FrontendKind kind)=>(GameItem)AccessTools.Field(Helper(kind),"openItem").GetValue(null);
    internal static bool Visible(NativeWorld.FrontendKind kind,GameItem target)=>ReferenceEquals(OpenItem(kind),target) &&
        CustomUIManager.Instance?.GetWindow(WindowId(kind))?.IsAlive==true;
    internal static void Close(NativeWorld.FrontendKind kind,GameItem target)
    { if(Visible(kind,target)) CustomUIManager.Instance.CloseWindow(WindowId(kind)); }
    internal static void Refresh(NativeWorld.FrontendKind kind,GameItem target,bool resetDraft)
    {
        if(!Visible(kind,target)) return;
        var type=Helper(kind);
        if(kind==NativeWorld.FrontendKind.Hopper && resetDraft)
        {
            // Refresh existing native controls without re-creating a window,
            // moving it, firing callbacks or resetting an active local picker.
            var window=CustomUIManager.Instance.GetWindow(WindowId(kind));
            var sliders=window.root.GetComponentsInChildren<Slider>(true);
            var toggles=window.root.GetComponentsInChildren<Toggle>(true);
            if(sliders.Length!=2 || toggles.Length!=5) throw new InvalidOperationException("Native hopper control layout changed.");
            sliders[0].SetValueWithoutNotify(HopperHelper.GetOrder(target));
            sliders[1].SetValueWithoutNotify(HopperHelper.GetKeep(target));
            toggles[0].SetIsOnWithoutNotify(HopperHelper.IsOnAdd(target));
            toggles[1].SetIsOnWithoutNotify(HopperHelper.IsOnMove(target));
            for(int i=0;i<3;i++) window.Get("filter_"+i).toggle.SetIsOnWithoutNotify(HopperHelper.GetFilterMode(target)==i);
            window.Get("order").SetText((string)AccessTools.Method(type,"OrderText").Invoke(null,new object[]{target}));
            window.Get("keep").SetText((string)AccessTools.Method(type,"KeepText").Invoke(null,new object[]{target}));
        }
        if(kind==NativeWorld.FrontendKind.StorageCoordinator && resetDraft)
        {
            AccessTools.Method(type,"StopPick").Invoke(null,null);
            AccessTools.Method(type,"StopCapture").Invoke(null,new object[]{true});
            AccessTools.Field(type,"presets").SetValue(null,MachineStorageCoordinator.LoadPresets(target));
        }
        if(kind==NativeWorld.FrontendKind.Hopper) AccessTools.Method(type,"RefreshLink").Invoke(null,null);
        AccessTools.Method(type,"RebuildList").Invoke(null,null);
    }
    internal static JObject SpriteData(Sprite sprite)
    {
        if(sprite==null) return null;
        var cache=(Dictionary<string,Dictionary<string,Sprite>>)AccessTools.Field(typeof(RenderHandler),"atlasCache").GetValue(null);
        foreach(var atlas in cache) foreach(var entry in atlas.Value)
            if(entry.Value==sprite) return new JObject { ["atlas"]=atlas.Key,["key"]=entry.Key };
        throw new InvalidOperationException("Native logistics description lacks an atlas sprite identity: "+sprite.name);
    }
    internal static Sprite ReadSprite(JObject value)
    {
        if(value==null) return null;
        if(value.Count!=2 || value["atlas"]?.Type!=JTokenType.String || value["key"]?.Type!=JTokenType.String ||
            ((string)value["atlas"]).Length>512 || ((string)value["key"]).Length>512) throw new InvalidOperationException("Invalid logistics sprite identity.");
        return RenderHandler.LoadFromAtlas((string)value["atlas"],(string)value["key"]);
    }
}

[HarmonyPatch(typeof(MindReprogrammerHelper),nameof(MindReprogrammerHelper.InitMindReprogrammer))]
internal static class NativeLogisticsMindCapability
{ private static void Postfix(GameItem item)=>NativeFrontendCapabilities.Bind(item,NativeWorld.FrontendKind.MindReprogrammer); }

[HarmonyPatch(typeof(GameItem),nameof(GameItem.ModifyTag),new[]{typeof(string),typeof(Action<TagState>),typeof(bool)})]
internal static class NativeLogisticsConfigWrite
{
    private static bool Prefix(GameItem __instance,string tagName,Action<TagState> action,ref GameItem __result)
    {
        if(!NativeGuest.Active && NativeWorld.Host?.RejectLogisticsConfiguration(__instance,tagName)==true)
        { __result=__instance; return false; }
        if(!NativeGuest.Active || NativeWorld.Guest?.EditLogisticsTag(__instance,tagName,action)!=true) return true;
        __result=__instance; return false;
    }
}
[HarmonyPatch(typeof(GameItem),nameof(GameItem.GetTagReadonly),new[]{typeof(string)})]
internal static class NativeLogisticsDraftRead
{
    private static bool Prefix(GameItem __instance,string tagName,ref TagState __result)
    {
        if(!NativeGuest.Active || NativeWorld.Guest==null || !NativeWorld.Guest.ReadLogisticsTag(__instance,tagName,out var tag)) return true;
        __result=tag; return false;
    }
}
[HarmonyPatch(typeof(HopperHelper),"Watch")]
internal static class NativeLogisticsNoReplicaWatch
{ private static bool Prefix()=>!NativeGuest.Active; }
[HarmonyPatch(typeof(HopperHelper),"EnsureLabel")]
internal static class NativeLogisticsNoReplicaLabelAllocation
{ private static bool Prefix()=>!NativeGuest.Active; }
[HarmonyPatch(typeof(HopperHelper),"Describe")]
internal static class NativeLogisticsNoReplicaDescriptionFactory
{
    private static bool Prefix(string identifier,ref ValueTuple<string,Sprite> __result)
    {
        if(!NativeGuest.Active) return true;
        __result=NativeWorld.Guest?.DescribeLogisticsItem(identifier) ?? (identifier,null); return false;
    }
}
[HarmonyPatch(typeof(HopperHelper),nameof(HopperHelper.RunAndRecord))]
internal static class NativeLogisticsTransfer
{
    private static bool Prefix(GameItem intake,ref int __result,out IDisposable __state)
    {
        __state=null;
        if(!NativeGuest.Active)
        {
            if((NativeLogisticsAutomatic.IsRunning || NativeActorSession.Current?.NightActive==true) && NativeWorld.Host is NativeWorld source)
            {
                __state=source.EnterAutomaticLogistics(intake,out bool available);
                if(!available) { __result=0; return false; }
            }
            return NativeWorld.Host?.LogisticsHeldTransfer(intake)!=true;
        }
        NativeWorld.Guest?.RequestLogisticsRun(intake); __result=0; return false;
    }
    private static Exception Finalizer(Exception __exception,IDisposable __state)
    {
        try
        {
            if(__exception!=null && !NativeGuest.Active) NativeActorSession.Current?.MarkFault(__exception);
        }
        finally { __state?.Dispose(); }
        return __exception;
    }
}

// The native queue yields before doing any work. Scope each MoveNext, never
// the lifetime of its IEnumerator, then choose each intake's physical owner.
// Explicit local/remote button operations keep their caller's actor instead.
[HarmonyPatch(typeof(HopperHelper),"RunPending")]
internal static class NativeLogisticsAutomatic
{
    [ThreadStatic] private static int depth;
    internal static bool IsRunning=>depth>0;
    private static void Postfix(ref IEnumerator __result)
    { if(!NativeGuest.Active && NativeActorSession.Current!=null) __result=Scoped(__result); }
    private static IEnumerator Scoped(IEnumerator original)
    {
        try
        {
            while(true)
            {
                bool next; object current;
                depth++;
                try { next=original.MoveNext(); current=next ? original.Current : null; }
                finally { depth--; }
                if(!next) yield break;
                yield return current;
            }
        }
        finally { (original as IDisposable)?.Dispose(); }
    }
}
[HarmonyPatch(typeof(MindReprogrammerHelper),nameof(MindReprogrammerHelper.Apply))]
internal static class NativeLogisticsMindCommit
{
    private static bool Prefix(GameItem item,StartingPerk definition)
    {
        if(!NativeGuest.Active) return NativeWorld.Host?.RejectLogisticsAction(item)!=true && NativeWorld.Host?.RejectHeldInput(item)!=true;
        NativeWorld.Guest?.RequestMindApply(item,definition); return false;
    }
}
[HarmonyPatch(typeof(MindReprogrammerHelper),"OnPerkRemoved")]
internal static class NativeLogisticsMindWorldEffect
{
    private static bool Prefix(string perkId)
    {
        var session=NativeActorSession.Current;
        // Partner's selection never applied the shared starting stock/shape.
        // Removing it must not undo the Host's actual shop restriction.
        return session==null || session.Run.CurrentActor!=NativeActorRun.Slot.Partner || perkId is not ("minimalist" or "ascetic");
    }
}

internal sealed partial class NativeWorld
{
    internal void CloseInaccessibleLogistics()
    {
        if(!host || !HasActorProjection) return;
        foreach(var kind in new[]{FrontendKind.Hopper,FrontendKind.StorageCoordinator,FrontendKind.MindReprogrammer})
        {
            var target=NativeLogisticsUi.OpenItem(kind);
            if(target!=null && (target.parentInventory==null || !ActorCanAccess(target.parentInventory,out _)))
                NativeLogisticsUi.Close(kind,target);
        }
    }
    internal bool RejectLogisticsAction(GameItem item)
    {
        if(!host || !HasActorProjection || item?.parentInventory==null || ActorCanAccess(item.parentInventory,out _)) return false;
        CloseInaccessibleLogistics(); CommandNotice("设备已移入另一位合伙人的随身物品。"); return true;
    }
    internal bool RejectLogisticsConfiguration(GameItem item,string key)
    {
        bool config=HopperHelper.IsIntake(item) && HopperConfigKeys.Contains(key) ||
            item?.IsTag(MachineStorageCoordinator.STORAGE_COORDINATOR_TAG)==true && key==MachineStorageCoordinator.STORAGE_COORDINATOR_PRESETS_STRING;
        return config && RejectLogisticsAction(item);
    }
    internal IDisposable EnterAutomaticLogistics(GameItem intake,out bool available)
    {
        available=false;
        var session=NativeActorSession.Current;
        if(session==null || session.Hydrating || session.Faulted || session.Terminal ||
            !ReferenceEquals(session.Store,PlayerStore.Instance) || intake?.parentInventory==null ||
            !TryPhysicalRoot(intake.parentInventory,out var root)) return null;
        var actor=root.Personal ? (NativeActorRun.Slot)(byte)root.Owner.Value : NativeActorRun.Slot.Host;
        var scope=session.Enter(actor);
        try
        {
            var destination=HopperHelper.FindOutlet(intake)?.parentInventory;
            // Even native links retained from earlier ownership cannot reach
            // the other person's carry. Shared containers remain accessible.
            available=destination!=null && ActorCanAccess(destination,out _);
            return scope;
        }
        catch { scope.Dispose(); throw; }
    }

    internal bool LogisticsHeldTransfer(GameItem intake)
    {
        if(RejectLogisticsAction(intake)) return true;
        var hands=CaptureHostHand(true);
        var destination=HopperHelper.FindOutlet(intake)?.parentInventory;
        if(intake?.parentInventory==null || destination==null) return false;
        bool blocked=hands.TouchesContents(intake.parentInventory) || hands.TouchesContents(destination);
        if(blocked) CommandNotice("请先放下漏斗转移范围内的物品。");
        return blocked;
    }
}
