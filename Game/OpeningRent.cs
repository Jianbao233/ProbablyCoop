using System;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Core;

namespace ProbablyCoop;

internal static class OpeningRent
{
    private const string SaveKey = "probably_coop.opening.rent.v1";
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<PlayerStore, RentAccount> Accounts = new();
    private sealed class RentAccount
    {
        internal string Serialized;
        internal OpeningRentPlan Plan;
        internal int Arrears, LastDay;
    }

    // The actual balance includes native career/perk effects, including the
    // saving perk, before the game's first write. The fee table is a quote.
    internal static void Initialize(PlayerStore store, CoopOpening opening)
    {
        var plan = OpeningRentPlan.Create(store.playerCash < 0 ? checked(-store.playerCash) : 0, opening.RentWeeks);
        store.playerCash = Math.Max(0, store.playerCash);
        if (plan.Complete) return;
        store.temporaryRent = checked(store.temporaryRent + plan.Active);
        Write(store, plan, 0, -1);
    }

    private static (OpeningRentPlan Plan, int Arrears, int LastDay) Read(PlayerStore store)
    {
        if (NativeActorSession.Current?.Store != store || !store.modData.TryGetValue(SaveKey, out var json))
            return default;
        var cached = Accounts.GetValue(store, _ => new RentAccount());
        if (cached.Serialized == json) return (cached.Plan, cached.Arrears, cached.LastDay);
        var record = JObject.Parse(json);
        if (record.Count != 4 || record["amounts"] is not JArray rows || rows.Count > OpeningRentPlan.MaximumWeeks ||
            rows.Any(row => row.Type != JTokenType.Integer) || record["index"]?.Type != JTokenType.Integer ||
            record["arrears"]?.Type != JTokenType.Integer || record["lastDay"]?.Type != JTokenType.Integer)
            throw new InvalidOperationException("Malformed cooperative rent installments.");
        var plan = OpeningRentPlan.Restore(rows.Select(row => (int)row), (int)record["index"]);
        int arrears = (int)record["arrears"];
        int lastDay = (int)record["lastDay"];
        if (arrears < 0 || arrears > plan.Amounts.Take(plan.Index).Sum() || lastDay < -1 ||
            plan.Active + arrears > store.temporaryRent)
            throw new InvalidOperationException("Cooperative rent ledger does not match native rent.");
        cached.Serialized = json;
        cached.Plan = plan;
        cached.Arrears = arrears;
        cached.LastDay = lastDay;
        return (plan, arrears, lastDay);
    }

    private static void Write(PlayerStore store, OpeningRentPlan plan, int arrears, int lastDay)
    {
        store.modData[SaveKey] = new JObject
        {
            ["amounts"] = new JArray(plan.Amounts),
            ["index"] = plan.Index,
            ["arrears"] = arrears,
            ["lastDay"] = lastDay
        }.ToString(Formatting.None);
    }

    internal static (int Active, int Balance) Describe(PlayerStore store)
    {
        if (NativeGuest.Active && NativeWorld.Guest is NativeWorld guest)
            return (guest.OpeningRentActive, guest.OpeningRentBalance);
        var account = Read(store);
        return account.Plan == null ? (0, 0) :
            (account.Plan.Active, account.Plan.Active + account.Plan.Future + account.Arrears);
    }

    [HarmonyPatch]
    private static class WeeklyCharge
    {
        private static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(PlayerStore), nameof(PlayerStore.OnRentPaid));
            yield return AccessTools.Method(typeof(PlayerStore), nameof(PlayerStore.OnRentNotPaid));
        }

        private static bool Prefix(PlayerStore __instance, out (OpeningRentPlan Plan, int Arrears, int Day) __state)
        {
            __state = default;
            var account = Read(__instance);
            if (account.Plan == null) return true;
            int today = StoreStation.Instance.dayCounter;
            if (account.LastDay == today) return false;
            __state = (account.Plan, account.Arrears, today);
            return true;
        }

        private static void Postfix(PlayerStore __instance, System.Reflection.MethodBase __originalMethod,
            (OpeningRentPlan Plan, int Arrears, int Day) __state, bool __runOriginal)
        {
            if (__state.Plan == null || !__runOriginal) return;
            bool missed = __originalMethod.Name == nameof(PlayerStore.OnRentNotPaid);
            var next = __state.Plan.Advance();
            int arrears = missed ? checked(__state.Arrears + __state.Plan.Active) : 0;
            __instance.temporaryRent = checked(__instance.temporaryRent + next.Active);
            Write(__instance, next, arrears, __state.Day);
        }
    }

    [HarmonyPatch(typeof(PlayerStore), nameof(PlayerStore.GetStoreBuyPrice))]
    private static class IncludeDebtInBuyout
    {
        private static void Postfix(PlayerStore __instance, bool applyDownPayment, ref int __result)
        {
            if (applyDownPayment || __instance.IsPropertyPaid || __instance.isPayingMortgage) return;
            __result = checked(__result + Describe(__instance).Balance);
        }
    }

    [HarmonyPatch(typeof(AdvCalendarUIManager), nameof(AdvCalendarUIManager.OnCalendarButtonClicked))]
    private static class Calendar
    {
        private static void Postfix(AdvCalendarUIManager __instance)
        {
            var store = PlayerStore.Instance;
            int active = Describe(store).Active;
            if (active == 0 || store.IsPropertyPaid || store.isPayingMortgage) return;
            var text = __instance.landlordTMP;
            int arrears = Math.Max(0, store.temporaryRent - active);
            text.text = ModHelper.GetLocalized("probably_coop", "opening_rent_calendar",
                store.GetRentValue(), active, arrears, store.dayUntilRent);
        }
    }

    [HarmonyPatch]
    private static class RetireDebtOnPropertyPurchase
    {
        private static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(PlayerStore), nameof(PlayerStore.BuyOutStore));
            yield return AccessTools.Method(typeof(PlayerStore), nameof(PlayerStore.TakeMortgage));
        }

        private static void Prefix(PlayerStore __instance, out int __state)
        {
            var account = Read(__instance);
            __state = account.Plan == null ? -1 : account.Plan.Active + account.Arrears;
        }

        private static void Postfix(PlayerStore __instance, int __state)
        {
            if (__state < 0 || !(__instance.IsPropertyPaid || __instance.isPayingMortgage)) return;
            __instance.temporaryRent = Math.Max(0, __instance.temporaryRent - __state);
            __instance.modData.Remove(SaveKey);
        }
    }
}
