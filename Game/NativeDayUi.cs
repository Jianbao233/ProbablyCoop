using HarmonyLib;
using ProbablyCoop.Core;

namespace ProbablyCoop;

[HarmonyPatch(typeof(EndOfDayUIManager),nameof(EndOfDayUIManager.OpenUI))]
internal static class CloseDepartureForNight
{
    private static bool Prefix() => !NativeGuest.Active;

    private static void Postfix(EndOfDayUIManager __instance)
    {
        if(!__instance.IsOpen) return;
        NativeInventoryPilot.ActiveCampaign?.CancelDepartureForNight();
        NativeWorld.Host?.CancelDepartureForSleep();
    }
}

internal sealed class NativeDayUi
{
    private int reportDay = -1;
    private static string Loc(string key) => ModHelper.GetLocalized("probably_coop", key);

    internal static string ReadinessText(byte mask, bool guest) =>
        string.Format(Loc("party_ready_status"),
            Loc((mask & (guest ? 2 : 1)) != 0 ? "party_ready" : "party_pending"),
            Loc((mask & (guest ? 1 : 2)) != 0 ? "party_ready" : "party_pending"));

    internal void Tick(ShopLink link)
    {
        var view = link?.View;
        if (!NativeGuest.Active || view?.Active != true || !PlayerStore.IsInstanceExist()) return;
        var tablet = EndOfDayUIManager.Instance;
        if (tablet?.IsOpen == true) tablet.CloseUI();
        if (StoreUIManager.Instance?.savingTMP != null)
            StoreUIManager.Instance.savingTMP.gameObject.SetActive(view.Phase == ShopDay.Settling);

        var world = NativeWorld.Guest;
        if (view.Phase != ShopDay.Saved || world?.View?.Day != view.Day ||
            world.MorningReportDay != view.Day || reportDay == view.Day) return;
        var report = StartOfDayUIManager.Instance;
        if (report?.UIPanel == null) return;

        PaperUIManager.Instance?.RestorePaperUIClosedForSleep(morning: true);
        var store = PlayerStore.Instance;
        var authoritativeLogs = store.nightLogs;
        try
        {
            store.nightLogs = new System.Collections.Generic.List<string>(world.MorningReport);
            report.ShowMorningReport();
            reportDay = view.Day;
        }
        finally { store.nightLogs = authoritativeLogs; }
    }

    internal void Reset() => reportDay = -1;
}
