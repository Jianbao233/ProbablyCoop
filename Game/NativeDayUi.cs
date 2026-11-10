using System;
using HarmonyLib;
using ProbablyCoop.Core;
using UnityEngine;

namespace ProbablyCoop;

[HarmonyPatch(typeof(EndOfDayUIManager),nameof(EndOfDayUIManager.OpenUI))]
internal static class CloseDepartureForNight
{
    private static void Postfix(EndOfDayUIManager __instance)
    {
        if(!__instance.IsOpen) return;
        var world=NativeGuest.Active ? NativeWorld.Guest : NativeWorld.Host;
        world?.CancelDepartureForSleep();
    }
}

internal sealed class NativeDayUi
{
    private static ShopLink current;
    private ShopView lastPlan;
    private int reportDay = -1;
    private ShopDay lastPhase;
    private bool heldForPeer;

    private static string Loc(string key) => ModHelper.GetLocalized("probably_coop", key);

    internal static string ReadinessText(byte mask, bool guest) =>
        string.Format(Loc("party_ready_status"),
            Loc((mask & (guest ? 2 : 1)) != 0 ? "party_ready" : "party_pending"),
            Loc((mask & (guest ? 1 : 2)) != 0 ? "party_ready" : "party_pending"));

    internal void Tick(ShopLink link)
    {
        current = link;
        var view = link?.View;
        var tablet = EndOfDayUIManager.Instance;
        if (tablet?.UI == null) return;
        if (view?.Active != true)
        {
            if (!NativeGuest.Active && tablet.IsOpen)
            {
                var original = tablet.UI.GetComponent<EndOfDayTabletFrame>();
                var originalButton = original?.buttonHost.GetComponentInChildren<TabletButton>();
                if (NativeShop.Gate != null)
                {
                    if (originalButton?.Interactable == true) originalButton.SetInteractable(false);
                    if (original?.infoLine != null) original.infoLine.text = Loc("day_peer_waiting");
                    heldForPeer = true;
                }
                else if (heldForPeer)
                {
                    tablet.RefreshUI();
                    originalButton?.SetLabel(LocHelper.GetLocalizedUI("ui_end_day"));
                    if (original?.infoLine != null) original.infoLine.text = LocHelper.GetLocalizedUI("ui_endofday_info");
                    heldForPeer = false;
                }
            }
            return;
        }
        if (!NativeGuest.Active && tablet.IsOpen) heldForPeer = true;

        if (NativeGuest.Active)
        {
            if (!PlayerStore.IsInstanceExist()) return;
            if (view.Phase == ShopDay.Ready)
            {
                if (tablet.IsOpen && (lastPlan == null || lastPlan.Day != view.Day || lastPlan.NightServices != view.NightServices ||
                    !lastPlan.ServiceCosts.AsSpan().SequenceEqual(view.ServiceCosts)))
                    tablet.InitUI();
                lastPlan = view;
            }
            else
            {
                lastPlan = null;
                if (tablet.IsOpen) tablet.CloseUI(restorePaperUI: view.Phase == ShopDay.Playing);
            }

            if (StoreUIManager.Instance?.savingTMP != null)
                StoreUIManager.Instance.savingTMP.gameObject.SetActive(view.Phase == ShopDay.Settling);

            if (view.Phase == ShopDay.SaveUnverified && lastPhase != view.Phase)
                StoreUIManager.Instance?.Notify(Loc("phase_SaveUnverified"));
            lastPhase = view.Phase;
            if (view.Phase == ShopDay.Saved && NativeWorld.Guest?.View?.Day==view.Day && NativeWorld.Guest.MorningReportDay==view.Day && reportDay != view.Day)
            {
                var report = StartOfDayUIManager.Instance;
                if (report?.UIPanel != null)
                {
                    reportDay = view.Day;
                    PaperUIManager.Instance?.RestorePaperUIClosedForSleep(morning: true);
                    var store=PlayerStore.Instance; var authoritativeLogs=store.nightLogs;
                    try
                    {
                        store.nightLogs=new System.Collections.Generic.List<string>(NativeWorld.Guest.MorningReport);
                        report.ShowMorningReport();
                    }
                    finally { store.nightLogs=authoritativeLogs; }
                }
            }
        }

        if (!tablet.IsOpen) return;
        var frame = tablet.UI.GetComponent<EndOfDayTabletFrame>();
        var button = frame?.buttonHost.GetComponentInChildren<TabletButton>();
        if (button == null) return;
        bool guest = NativeGuest.Active;
        if (frame.infoLine != null)
        {
            string text = view.GuestOutside ? Loc("day_peer_outside") :
                view.Phase == ShopDay.Ready ? ReadinessText(view.ReadyMask, guest) : Loc("phase_" + view.Phase);
            if (!guest && view.Phase == ShopDay.Ready && view.ReadyMask == 3)
                text += " · " + Loc("party_host_finish");
            if (frame.infoLine.text != text) frame.infoLine.text = text;
        }
        bool ready = (view.ReadyMask & (guest ? 2 : 1)) != 0;
        string caption = Loc(ready && !guest && view.ReadyMask == 3 ? "finish_day" : ready ? "unready" : "ready");
        if (button.label != null && button.label.text != caption) button.SetLabel(caption);
        bool canPress = view.Phase == ShopDay.Ready && !link.Waiting &&
            (!NativeGuest.Active || NativeWorld.Guest?.HasPendingWork==false);
        if (button.Interactable != canPress) button.SetInteractable(canPress);
    }

    internal static void Submit()
    {
        var link = current;
        if (link?.View.Phase != ShopDay.Ready || link.Waiting) return;
        if (NativeGuest.Active && NativeWorld.Guest?.HasPendingWork!=false) return;
        var action = !NativeGuest.Active && link.View.ReadyMask == 3 ? ShopAction.FinishDay : ShopAction.Ready;
        link.Submit(action, 0, Time.realtimeSinceStartupAsDouble);
    }

    internal static void ShowGuestPlan()
    {
        var plan = current?.View;
        var tablet = EndOfDayUIManager.Instance;
        if (!NativeGuest.Active || plan?.Phase != ShopDay.Ready || tablet?.UI == null || tablet.IsOpen) return;
        if (NativeWorld.Guest?.Ready != true)
        {
            var services = PlayerStore.Instance.storeService;
            if (services.Count > 64 || services.Count < 64 && (plan.NightServices >> services.Count) != 0) return;

            int row = 0;
            for (int i = 0; i < services.Count; i++)
            {
                var service = services[i];
                bool charged = (plan.NightServices & (1UL << i)) != 0;
                service.unlocked = service.isUsing = charged;
                service.isMandatory = true;
                service.currentCooldown = 0;
                if (charged) service.cost = plan.ServiceCosts[row++];
            }
        }
        PaperUIManager.Instance?.CloseAllPaperUIForSleep();
        tablet.OpenUI();
    }

    internal void Reset()
    {
        current = null;
        lastPlan = null;
        reportDay = -1;
        lastPhase = ShopDay.Playing;
        heldForPeer = false;
    }
}
