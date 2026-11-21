using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using ProbablyCoop.Sync;

namespace ProbablyCoop;

internal static class NativeShootingHooks
{
    private static int sourceDepth, queryDepth;
    private static readonly FieldInfo PendingSurvivor=AccessTools.Field(typeof(StoreUIManager),"pendingSurvivedShot");
    internal static void ClearPendingSurvivor(StoreUIManager ui)=>PendingSurvivor.SetValue(ui,null);
    internal static NativeWorld Presentation { get; private set; }
    internal static NativeWorld ArrestCleanup { get; private set; }
    internal static NativeEffectCampaign EffectArrestCleanup { get; private set; }
    internal static bool SourceInvocation=>sourceDepth!=0;
    internal static IDisposable InvokeSource() {sourceDepth++;return new Scope(()=>sourceDepth--);}
    internal static IDisposable Present(NativeWorld world)
    {var previous=Presentation;Presentation=world;return new Scope(()=>Presentation=previous);}
    internal static IDisposable ProtectArrestCleanup(NativeWorld world)
    {var previous=ArrestCleanup;ArrestCleanup=world;return new Scope(()=>ArrestCleanup=previous);}
    internal static IDisposable ProtectArrestCleanup(NativeEffectCampaign campaign)
    {var previous=EffectArrestCleanup;EffectArrestCleanup=campaign;return new Scope(()=>EffectArrestCleanup=previous);}
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
        if (NativeInventoryPilot.ActiveCampaign is NativeEffectCampaign campaign)
            return campaign.HandleShotInput(__originalMethod.Name switch
            {
                nameof(StoreUIManager.EnterShootingMode) => BusinessAction.Aim,
                nameof(StoreUIManager.Shoot) => BusinessAction.Fire,
                _ => BusinessAction.Holster,
            });
        string action=__originalMethod.Name==nameof(StoreUIManager.EnterShootingMode) ? "enter" :
            __originalMethod.Name==nameof(StoreUIManager.Shoot) ? "fire" : "exit";
        if(NativeGuest.Active)
        {
            var world=NativeWorld.Guest;
            if(NativeShootingHooks.Presentation!=null)
            {if(action=="exit") world?.GuestBodyExit();return true;}
            if(action=="fire") world?.GuestFireClick();
            else if(action=="enter" || world?.CanUseShootingInput(false)==true) world?.RequestShooting(action);
            return false;
        }
        if(NativeShootingHooks.SourceInvocation || NativeWorld.Host==null) return true;
        var source=NativeWorld.Host;
        if(action=="exit" && source.IsNativeShotExit) {source.RecordNativeShotExit();return true;}
        source.RunShootingAction(action);return false;
    }
}

[HarmonyPatch(typeof(Shotgun),nameof(Shotgun.CanPickup))]
internal static class NativeShootingPickupQuery
{
    private static bool Prefix(ref bool __result)
    {
        if(NativeShootingHooks.OriginalQuery) return true;
        if (NativeInventoryPilot.ActiveCampaign is NativeEffectCampaign campaign && !campaign.CanTakeShotgun)
        { __result = false; return false; }
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
        if (NativeInventoryPilot.ActiveCampaign is NativeEffectCampaign campaign)
        {
            if (!campaign.PresentingShot) __result &= campaign.CanFireShot;
            return;
        }
        var world=NativeGuest.Active ? NativeWorld.Guest : NativeWorld.Host;
        if(world!=null) __result&=NativeGuest.Active ? world.CanAcceptGuestFireClick() : world.CanUseShootingInput(true);
    }
}

[HarmonyPatch(typeof(AudioManager), nameof(AudioManager.Play2))]
internal static class NativeShootingRestoreAudio
{
    private static bool Prefix(string identifier) =>
        identifier is not ("shotgun_fire" or "body_collapse_floor") ||
        NativeShootingHooks.Presentation?.RestoringFiredShot != true;
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
        yield return AccessTools.Method(typeof(StoreUIManager),"SurvivedShotRoutine");
    }
    private static void Postfix(MethodBase __originalMethod,ref IEnumerator __result)
    {
        bool survivor=__originalMethod.Name=="SurvivedShotRoutine";
        if (NativeInventoryPilot.ActiveCampaign is NativeEffectCampaign campaign)
        { __result = campaign.BindShotAnimation(__result,survivor); return; }
        var world=NativeGuest.Active ? NativeWorld.Guest : NativeWorld.Host;
        if(world!=null) __result=world.BindShootingAnimation(__result,survivor);
    }
}

[HarmonyPatch(typeof(StoreUIManager),"ShootSurvivor")]
internal static class NativeShootingSurvivorReplica
{
    private static readonly Action PresentationOnly=()=>{};

    private static void Postfix(ref Action ___pendingSurvivedShot)
    {
        // Keep the non-null guard used by native holstering. The host supplies
        // the actual dialogue and customer departure through their usual routes.
        if(NativeGuest.Active || NativeInventoryPilot.CampaignReplica)
            ___pendingSurvivedShot=PresentationOnly;
    }
}

[HarmonyPatch(typeof(DialogUIManager),nameof(DialogUIManager.InterruptDialog))]
internal static class NativeShootingReplicaDialogue
{
    private static bool Prefix()=>NativeShootingHooks.Presentation==null &&
        !(NativeInventoryPilot.CampaignReplica && NativeInventoryPilot.ActiveCampaign?.PresentingShot==true);
}

[HarmonyPatch(typeof(StoreUIManager),nameof(StoreUIManager.StartBlackscreenSequence))]
internal static class NativeShootingPresentationCallbacks
{
    private static void Prefix(ref Action actionDuringBlack,ref Action actionAfterBlack)
    {
        if (NativeInventoryPilot.ActiveCampaign is NativeEffectCampaign campaign)
        {
            actionDuringBlack = campaign.BindShotCallback(actionDuringBlack);
            actionAfterBlack = campaign.BindShotCallback(actionAfterBlack);
            return;
        }
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
    private static bool Prefix()=>!NativeGuest.Active && !NativeInventoryPilot.CampaignReplica;
}
[HarmonyPatch]
internal static class NativeShootingNoReplicaReaction
{
    // StartDialogue already rejects replica business. Avoid even drawing its
    // random line on the replica; native UI receives the authority's dialogue.
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(AugHelper), nameof(AugHelper.GetRandomClientReactionUnholster));
        yield return AccessTools.Method(typeof(AugHelper), nameof(AugHelper.GetClientReactionUnholster));
    }
    private static bool Prefix(MethodBase __originalMethod, ref Dialogue __result)
    {
        if (!NativeInventoryPilot.CampaignReplica &&
            (!NativeGuest.Active || __originalMethod.Name == nameof(AugHelper.GetClientReactionUnholster))) return true;
        __result = null;
        return false;
    }
}
[HarmonyPatch(typeof(StoreUIManager),nameof(StoreUIManager.StartShootoutSequence))]
internal static class NativeShootingAuthorityAftermath
{
    private static bool Prefix()=>!NativeGuest.Active &&
        (NativeInventoryPilot.ActiveCampaign?.StartShotAftermath() ?? NativeWorld.Host?.BeginNativeShootout() ?? true);
}
[HarmonyPatch(typeof(AugHelper), nameof(AugHelper.OnGoodKill))]
internal static class NativeEffectAugReward
{
    private static bool Prefix(out (NativeEffectCampaign campaign, IDisposable actor, bool publish) __state)
    {
        __state = default;
        var campaign = NativeInventoryPilot.ActiveCampaign;
        if (campaign == null) return true;
        if (!campaign.MayRewardShot()) return false;
        __state = (campaign, campaign.EnterShotActor(), campaign.BeginAuthorityAction());
        return true;
    }

    private static Exception Finalizer(Exception __exception, (NativeEffectCampaign campaign, IDisposable actor, bool publish) __state)
    {
        __state.actor?.Dispose();
        if (__state.publish) __state.campaign.EndAuthorityAction(__exception == null);
        return __exception;
    }
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
    {__state=!NativeGuest.Active && id=="SEC" ?
        NativeInventoryPilot.ActiveCampaign?.EnterShotActor() ?? NativeInventoryPilot.ActiveCampaign?.EnterReportingActor() ??
        NativeWorld.Host?.EnterShootingContinuation() : null;}
    private static void Postfix(string id,MethodBase __originalMethod)
    {
        if (NativeGuest.Active || __originalMethod.Name != nameof(StoreUIManager.OnGenericLeft)) return;
        if (id == "SEC" && NativeInventoryPilot.AuthoritativeCampaign)
        {
            NativeInventoryPilot.ActiveCampaign.FinishShot();
            NativeInventoryPilot.ActiveCampaign.FinishReport();
        }
        NativeWorld.Host?.ShootingOfficerLeft(id);
    }
    private static Exception Finalizer(Exception __exception,IDisposable __state)
    {__state?.Dispose();return __exception;}
}

[HarmonyPatch(typeof(DialogUIManager),"ContinueConversation")]
internal static class NativeShootingDialogueActor
{
    // Either peer may advance the shared words; doing so does not transfer the
    // original shooter's perk/reward identity to whoever pressed advance last.
    private static void Prefix(out IDisposable __state)
    {__state=!NativeGuest.Active ?
        NativeInventoryPilot.ActiveCampaign?.EnterShotActor() ?? NativeWorld.Host?.EnterShootingContinuation() : null;}
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
        var campaign=NativeInventoryPilot.ActiveCampaign;
        if (campaign?.ShotActive == true && NativeInventoryPilot.AuthoritativeCampaign)
        { __state=NativeShootingHooks.ProtectArrestCleanup(campaign); return; }
        __state=!NativeGuest.Active && world?.IsNativeShootingCleanup==true ?
            NativeShootingHooks.ProtectArrestCleanup(world) : null;
    }
    private static Exception Finalizer(Exception __exception,IDisposable __state)
    {__state?.Dispose();return __exception;}
}
[HarmonyPatch(typeof(PlayerStore),nameof(PlayerStore.DismissCurrentClient))]
internal static class NativeShootingDismissOnlyShotTarget
{
    private static bool Prefix(PlayerStore __instance)=>NativeShootingHooks.EffectArrestCleanup?.MayDismissShotTarget() ??
        NativeShootingHooks.ArrestCleanup?.MayDismissShotCustomer(__instance) ?? true;
}
