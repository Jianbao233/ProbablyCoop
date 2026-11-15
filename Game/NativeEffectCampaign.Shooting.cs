using System;
using System.Collections;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Sync;
using UnityEngine;
using Phase = ProbablyCoop.NativeWorld.ShootingPhase;

namespace ProbablyCoop;

internal sealed partial class NativeEffectCampaign
{
    private NativeActorActivities.Lease shotLease;
    private NativeActorRun.Slot shooter;
    private StoreClientInstance shotTarget;
    private Phase shotPhase;
    private ulong shotEpisode, shotRevision = 1, shotCustomer;
    private bool shotCommitted, shotRewarded, shotBody, shotUnlocked, wasInShotCamera;
    private bool presentingShot, enteredShot, displayedShot, leftShot, skipShotReplay;
    internal bool ShotActive => shotPhase is > Phase.Idle and < Phase.Complete;
    internal bool PresentingShot => presentingShot;

    private void SetShotPhase(Phase next)
    {
        if (shotPhase == next) return;
        shotPhase = next;
        shotRevision++;
        CommerceChanged();
    }

    private void ObserveShot()
    {
        var ui = StoreUIManager.Instance;
        if (ui == null) return;
        bool body = ui.body.activeSelf;
        bool unlocked = PlayerStore.Instance.isShootingUnlocked;
        if (body != shotBody || unlocked != shotUnlocked) CommerceChanged();
        shotBody = body;
        shotUnlocked = unlocked;
        if (!ShotActive) return;
        if (ui.isShootingMode) wasInShotCamera = true;
        if (shotPhase == Phase.Entering && ui.isShootingMode && ui.shootingUI.activeSelf)
            SetShotPhase(Phase.Aiming);
        if (shotPhase == Phase.Leaving && wasInShotCamera && !ui.isShootingMode)
            FinishShot();
        else if (!shotCommitted && shotPhase is Phase.Entering or Phase.Aiming &&
            !ReferenceEquals(shotTarget, PlayerStore.Instance.currentClientInstance))
        {
            FinishShot();
            NativeWorld.RestoreShootingCamera(ui);
        }
    }

    internal bool HandleShotInput(BusinessAction action)
    {
        if (host && (NativeShootingHooks.SourceInvocation || action == BusinessAction.Holster &&
            shotLease != null && NativeActorSession.Current?.Activities.Current == shotLease))
        {
            if (action == BusinessAction.Holster && shotPhase == Phase.Fired) SetShotPhase(Phase.Leaving);
            return true;
        }
        if (!host && presentingShot)
        {
            if (action == BusinessAction.Holster) leftShot = true;
            return true;
        }
        if (!NativeInventoryPilot.Interactive) return false;
        if (host) RunShot(action, NativeActorRun.Slot.Host);
        else if (action == BusinessAction.Aim ? CanTakeShotgun : CanFireShot) RequestBusiness(action);
        return false;
    }

    internal bool CanTakeShotgun => shotUnlocked && (!ShotActive || CanFireShot);

    internal bool CanFireShot => shotPhase == Phase.Aiming &&
        shooter == (host ? NativeActorRun.Slot.Host : NativeActorRun.Slot.Partner) &&
        StoreUIManager.Instance.shootingUI.activeSelf;

    private void RunShot(BusinessAction action, NativeActorRun.Slot actor)
    {
        ObserveCommerce();
        var actors = NativeActorSession.Current;
        var store = PlayerStore.Instance;
        var ui = StoreUIManager.Instance;
        if (actors == null || actors.Terminal || actors.Faulted || MapUIManager.Instance.IsOutside ||
            store.storeState != PlayerStore.StoreState.OPEN || !store.isClientArrived ||
            !shotUnlocked || store.currentClientInstance == null) return;
        if (action == BusinessAction.Aim)
        {
            if (ShotActive || ui.isShootingMode || ui.isMagnifyingMode ||
                !NativeShootingHooks.OriginalCanPickup(ui.shotgun.GetComponent<Shotgun>())) return;
        }
        else if (shotPhase != Phase.Aiming || shooter != actor || shotTarget != store.currentClientInstance) return;
        if (TouchesHand(EmporiumEntry.Instance.GetAllNonOwnedItem()) || !BeginAuthorityAction()) return;

        bool finished = false;
        try
        {
            if (action == BusinessAction.Aim)
            {
                shotEpisode++;
                shotCustomer = currentCustomer;
                shotTarget = store.currentClientInstance;
                shooter = actor;
                shotCommitted = shotRewarded = wasInShotCamera = false;
                SetShotPhase(Phase.Entering);
                shotLease = actors.Activities.Begin(this, "effect-shotgun", actor, () => ShotActive, blocksCommit: true);
            }
            else if (action == BusinessAction.Fire)
            {
                shotCommitted = true;
                SetShotPhase(Phase.Fired);
            }
            else SetShotPhase(Phase.Leaving);
            using (shotLease.EnterNative())
            using (NativeShootingHooks.InvokeSource())
            {
                if (action == BusinessAction.Aim) ui.EnterShootingMode();
                else if (action == BusinessAction.Fire) ui.Shoot();
                else ui.ExitShootingMode();
            }
            finished = true;
        }
        finally { EndAuthorityAction(finished); }
    }

    internal bool StartShotAftermath()
    {
        if (!host) return false;
        if (!ShotActive) return true;
        if (!shotCommitted || shotPhase == Phase.Police) return false;
        SetShotPhase(Phase.Police);
        return true;
    }

    internal bool MayRewardShot()
    {
        if (!host || !shotCommitted || shotRewarded || shotPhase != Phase.Police) return false;
        shotRewarded = true;
        return true;
    }

    internal IDisposable EnterShotActor() => host && ShotActive ? shotLease?.EnterNative() : null;
    internal bool MayDismissShotTarget() => shotTarget == PlayerStore.Instance.currentClientInstance;

    internal void FinishShot()
    {
        if (!ShotActive) return;
        SetShotPhase(Phase.Complete);
        shotLease?.Dispose();
        shotLease = null;
    }

    private JObject CaptureShot() => new JObject
    {
        ["episode"] = shotEpisode, ["revision"] = shotRevision, ["phase"] = (int)shotPhase, ["actor"] = (int)shooter,
        ["customer"] = shotCustomer, ["committed"] = shotCommitted, ["body"] = shotBody, ["unlocked"] = shotUnlocked,
    };

    private static bool ValidShot(JObject value)
    {
        if (value == null) return false;
        foreach (string key in new[] { "episode", "revision", "customer", "phase", "actor" })
            if (value[key]?.Type != JTokenType.Integer || !ulong.TryParse(value[key].ToString(), out _)) return false;
        foreach (string key in new[] { "committed", "body", "unlocked" })
            if (value[key]?.Type != JTokenType.Boolean) return false;
        return (ulong)value["phase"] <= (ulong)Phase.Complete && (ulong)value["actor"] <= 1 &&
            (ulong)value["revision"] > 0;
    }

    private void ApplyShot(JObject value, bool restoring)
    {
        ulong episode = (ulong)value["episode"];
        if (shotEpisode != episode || restoring)
        {
            if (enteredShot) NativeWorld.RestoreShootingCamera(StoreUIManager.Instance);
            enteredShot = displayedShot = leftShot = false;
        }
        shotEpisode = episode;
        shotRevision = (ulong)value["revision"];
        shotPhase = (Phase)(int)value["phase"];
        shooter = (NativeActorRun.Slot)(int)value["actor"];
        shotCustomer = (ulong)value["customer"];
        shotCommitted = (bool)value["committed"];
        shotBody = (bool)value["body"];
        shotUnlocked = (bool)value["unlocked"];
        if (restoring) skipShotReplay = shotCommitted;
        else if (shotPhase == Phase.Entering) skipShotReplay = false;
        if (PlayerStore.Instance.isShootingUnlocked != shotUnlocked)
        {
            PlayerStore.Instance.isShootingUnlocked = shotUnlocked;
            Shotgun.LoadShotgun();
        }
    }

    private void PresentShot()
    {
        var ui = StoreUIManager.Instance;
        if (ui == null || shotPhase == Phase.Idle) return;
        if (!enteredShot && !skipShotReplay && ShotActive && shotCustomer == currentCustomer)
        {
            enteredShot = true;
            PlayShotVisual(ui.EnterShootingMode);
        }
        if (enteredShot && !leftShot && ui.isShootingMode && ui.shootingUI.activeSelf)
        {
            if (shotCommitted && !displayedShot)
            {
                displayedShot = true;
                PlayShotVisual(ui.Shoot);
            }
            else if (!shotCommitted && shotPhase is Phase.Leaving or Phase.Complete)
                PlayShotVisual(ui.ExitShootingMode);
        }
        if (!enteredShot || leftShot && !ui.isShootingMode) ui.body.SetActive(shotBody);
        if (shotPhase == Phase.Complete && leftShot && !ui.isShootingMode)
        {
            ui.isShot = false;
            ui.shotClientInstance = null;
        }
    }

    private void PlayShotVisual(Action callback)
    {
        bool previous = presentingShot;
        presentingShot = true;
        try { callback(); }
        finally { presentingShot = previous; }
    }

    internal Action BindShotCallback(Action callback)
    {
        if (callback == null || !(host ? ShotActive : presentingShot)) return callback;
        if (host) return shotLease?.Wrap(callback) ?? callback;
        ulong episode = shotEpisode;
        return () => { if (run != Guid.Empty && episode == shotEpisode) PlayShotVisual(callback); };
    }

    internal IEnumerator BindShotAnimation(IEnumerator iterator)
    {
        if (host) return shotLease?.WrapEnumerator(iterator) ?? iterator;
        if (!presentingShot) return iterator;
        return PresentNativeShot(iterator, shotEpisode);
    }

    private IEnumerator PresentNativeShot(IEnumerator iterator, ulong episode)
    {
        while (run != Guid.Empty && episode == shotEpisode)
        {
            bool next = false;
            PlayShotVisual(() => next = iterator.MoveNext());
            if (!next) break;
            yield return iterator.Current;
        }
    }

    private void RetireShot()
    {
        shotLease?.Dispose();
        shotLease = null;
        if (!host && enteredShot && StoreUIManager.Instance != null)
            NativeWorld.RestoreShootingCamera(StoreUIManager.Instance);
    }
}
