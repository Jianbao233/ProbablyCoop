using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace ProbablyCoop;

internal static class NativeShootingHooks
{
    private static int sourceDepth, queryDepth;
    internal static NativeWorld Presentation { get; private set; }
    internal static NativeWorld ArrestCleanup { get; private set; }
    internal static bool SourceInvocation=>sourceDepth!=0;
    internal static IDisposable InvokeSource() {sourceDepth++;return new Scope(()=>sourceDepth--);}
    internal static IDisposable Present(NativeWorld world)
    {var previous=Presentation;Presentation=world;return new Scope(()=>Presentation=previous);}
    internal static IDisposable ProtectArrestCleanup(NativeWorld world)
    {var previous=ArrestCleanup;ArrestCleanup=world;return new Scope(()=>ArrestCleanup=previous);}
    internal static bool OriginalCanPickup(Shotgun gun)
    {queryDepth++;try{return gun!=null && gun.CanPickup();}finally{queryDepth--;}}
    internal static bool OriginalQuery=>queryDepth!=0;
    private sealed class Scope:IDisposable
    {private Action close;internal Scope(Action close)=>this.close=close;public void Dispose(){var action=close;close=null;action?.Invoke();}}
}

[HarmonyPatch]
internal static class NativeShootingInput
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(StoreUIManager),nameof(StoreUIManager.EnterShootingMode));
        yield return AccessTools.Method(typeof(StoreUIManager),nameof(StoreUIManager.ExitShootingMode));
        yield return AccessTools.Method(typeof(StoreUIManager),nameof(StoreUIManager.Shoot));
    }
    private static bool Prefix(MethodBase __originalMethod)
    {
        string action=__originalMethod.Name==nameof(StoreUIManager.EnterShootingMode) ? "enter" :
            __originalMethod.Name==nameof(StoreUIManager.Shoot) ? "fire" : "exit";
        if(NativeGuest.Active)
        {
            var world=NativeWorld.Guest;
            if(NativeShootingHooks.Presentation!=null)
            {if(action=="exit") world?.GuestBodyExit();return true;}
            if(action=="enter" || world?.CanUseShootingInput(action=="fire")==true) world?.RequestShooting(action);
            return false;
        }
        if(NativeShootingHooks.SourceInvocation || NativeWorld.Host==null) return true;
        var source=NativeWorld.Host;
        if(action=="exit" && source.IsNativeBodyExit) {source.RecordNativeBodyExit();return true;}
        source.RunShootingAction(action);return false;
    }
}

[HarmonyPatch(typeof(Shotgun),nameof(Shotgun.CanPickup))]
internal static class NativeShootingPickupQuery
{
    private static bool Prefix(ref bool __result)
    {
        if(NativeShootingHooks.OriginalQuery) return true;
        if(NativeGuest.Active) {__result=NativeWorld.Guest?.CanPickupShooting()==true;return false;}
        if(NativeWorld.Host?.CanPickupShooting()==false) {__result=false;return false;}
        return true;
    }
}
[HarmonyPatch(typeof(StoreUIManager),nameof(StoreUIManager.CanShoot))]
internal static class NativeShootingAimQuery
{
    private static void Postfix(ref bool __result)
    {
        if(NativeShootingHooks.Presentation!=null || NativeShootingHooks.SourceInvocation) return;
        var world=NativeGuest.Active ? NativeWorld.Guest : NativeWorld.Host;
        if(world!=null) __result&=world.CanUseShootingInput(true);
    }
}

// Preserve the native coroutines and all local frame timing. Only their execution
// context is restored per MoveNext; no actor scope remains open across a yield.
[HarmonyPatch]
internal static class NativeShootingAnimationBinding
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(StoreUIManager),"WoundAnimationRoutine");
        yield return AccessTools.Method(typeof(StoreUIManager),"BodyFallRoutine");
    }
    private static void Postfix(ref IEnumerator __result)
    {
        var world=NativeGuest.Active ? NativeWorld.Guest : NativeWorld.Host;
        if(world!=null) __result=world.BindShootingAnimation(__result);
    }
}
[HarmonyPatch(typeof(StoreUIManager),nameof(StoreUIManager.StartBlackscreenSequence))]
internal static class NativeShootingPresentationCallbacks
{
    private static void Prefix(ref Action actionDuringBlack,ref Action actionAfterBlack)
    {
        var world=NativeShootingHooks.Presentation;
        if(!NativeGuest.Active || world==null) return;
        actionDuringBlack=world.BindShootingPresentation(actionDuringBlack);
        actionAfterBlack=world.BindShootingPresentation(actionAfterBlack);
    }
}

[HarmonyPatch]
internal static class NativeShootingNoReplicaBusiness
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(PlayerStore),nameof(PlayerStore.RemoveNotOwnedItemFromTable));
        yield return AccessTools.Method(typeof(ChemicalFeedbackHelper),nameof(ChemicalFeedbackHelper.CancelFeedbackForShotClient));
        yield return AccessTools.Method(typeof(AchievementTracker),nameof(AchievementTracker.CheckAugHunter));
    }
    private static bool Prefix()=>!NativeGuest.Active;
}
[HarmonyPatch(typeof(AugHelper),nameof(AugHelper.GetRandomClientReactionUnholster))]
internal static class NativeShootingNoReplicaReaction
{
    // StartDialogue already rejects replica business. Avoid even drawing its
    // random line on the replica; native UI receives the authority's dialogue.
    private static bool Prefix(ref Dialogue __result)
    {if(!NativeGuest.Active)return true;__result=null;return false;}
}
[HarmonyPatch(typeof(StoreUIManager),nameof(StoreUIManager.StartShootoutSequence))]
internal static class NativeShootingAuthorityAftermath
{
    private static bool Prefix()=>!NativeGuest.Active && (NativeWorld.Host?.BeginNativeShootout() ?? true);
}
[HarmonyPatch]
internal static class NativeShootingOfficerActor
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(StoreUIManager),nameof(StoreUIManager.OnGenericArrived));
        yield return AccessTools.Method(typeof(StoreUIManager),nameof(StoreUIManager.OnGenericLeft));
    }
    private static void Prefix(string id,out IDisposable __state)
    {__state=!NativeGuest.Active && id=="SEC" ? NativeWorld.Host?.EnterShootingContinuation() : null;}
    private static void Postfix(string id,MethodBase __originalMethod)
    {if(!NativeGuest.Active && __originalMethod.Name==nameof(StoreUIManager.OnGenericLeft)) NativeWorld.Host?.ShootingOfficerLeft(id);}
    private static Exception Finalizer(Exception __exception,IDisposable __state)
    {__state?.Dispose();return __exception;}
}

[HarmonyPatch(typeof(DialogUIManager),"ContinueConversation")]
internal static class NativeShootingDialogueActor
{
    // Either peer may advance the shared words; doing so does not transfer the
    // original shooter's perk/reward identity to whoever pressed advance last.
    private static void Prefix(out IDisposable __state)
    {__state=!NativeGuest.Active ? NativeWorld.Host?.EnterShootingContinuation() : null;}
    private static Exception Finalizer(Exception __exception,IDisposable __state)
    {__state?.Dispose();return __exception;}
}
[HarmonyPatch]
internal static class NativeShootingLocalCustomerLock
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(StoreBell),nameof(StoreBell.RingBell));
        yield return AccessTools.Method(typeof(StoreDismissButton),nameof(StoreDismissButton.OnClick));
    }
    private static bool Prefix()=>NativeGuest.Active || NativeWorld.Host?.ShootingOwnsCustomer!=true;
}

// A committed shooting keeps its native reward/police aftermath even if a
// forced event replaced the customer. Only its old target may be dismissed;
// the original StartLeaveSec and both native ClientFlagDown calls still run.
[HarmonyPatch(typeof(StoreUIManager),nameof(StoreUIManager.OnArrestDialoguePlayed))]
internal static class NativeShootingArrestCleanupTarget
{
    private static void Prefix(out IDisposable __state)
    {
        var world=NativeWorld.Host;
        __state=!NativeGuest.Active && world?.IsNativeShootingCleanup==true ?
            NativeShootingHooks.ProtectArrestCleanup(world) : null;
    }
    private static Exception Finalizer(Exception __exception,IDisposable __state)
    {__state?.Dispose();return __exception;}
}
[HarmonyPatch(typeof(PlayerStore),nameof(PlayerStore.DismissCurrentClient))]
internal static class NativeShootingDismissOnlyShotTarget
{
    private static bool Prefix(PlayerStore __instance)=>NativeShootingHooks.ArrestCleanup?.MayDismissShotCustomer(__instance) ?? true;
}
