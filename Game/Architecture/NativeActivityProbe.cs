using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using ProbablyCoop.Core;
using UnityEngine;

namespace ProbablyCoop;

/// <summary>Restricted proof for fields that vanilla store/Jackson/phone paths share.
/// This adapter is QA-only; it is not a claim that arbitrary scene callbacks are isolated.</summary>
internal static class NativeActivityProbe
{
    private static ActivityScope<Context> currentScope;
    internal static void BindDelayedAction(ref Action action)
    {
        if (!NativeArchitectureQa.Active || action == null || currentScope?.Current == null) return;
        // Copy before replacing; never close over the ref argument.
        Action actionCopy = action;
        var captured = currentScope.Capture(currentScope.Current.Value, _ => actionCopy());
        action = () => captured();
    }
    private sealed class Context
    {
        internal StoreClientInstance Client;
        internal StorePhoneClient Phone;
        internal GameGridInventory Back, Front;
        internal GameItem Selected;
        internal PlayerStore.PlayerLocation Location;
        internal AfterhourLocation Destination;
        internal static Context Read() => new Context {
            Client = PlayerStore.Instance.currentClientInstance, Phone = PhoneUIManager.Instance.currentPhoneClient,
            Back = EmporiumEntry.Instance.backInvinvElement, Front = EmporiumEntry.Instance.frontInvinvElement,
            Selected = PlayerStore.Instance.currentNegociatedItem, Location = PlayerStore.Instance.playerLocation,
            Destination = MapUIManager.Instance.Current
        };
        internal void Bind()
        {
            PlayerStore.Instance.currentClientInstance = Client; PhoneUIManager.Instance.currentPhoneClient = Phone;
            AccessTools.Property(typeof(EmporiumEntry), nameof(EmporiumEntry.backInvinvElement)).SetValue(EmporiumEntry.Instance, Back);
            AccessTools.Property(typeof(EmporiumEntry), nameof(EmporiumEntry.frontInvinvElement)).SetValue(EmporiumEntry.Instance, Front);
            PlayerStore.Instance.currentNegociatedItem = Selected; PlayerStore.Instance.playerLocation = Location;
            AccessTools.Property(typeof(MapUIManager), nameof(MapUIManager.Current)).SetValue(MapUIManager.Instance, Destination);
        }
        internal void CaptureCurrent()
        {
            var current = Read(); Client = current.Client; Phone = current.Phone; Back = current.Back;
            Front = current.Front; Selected = current.Selected; Location = current.Location; Destination = current.Destination;
        }
    }
    private sealed class Binding : IDisposable
    {
        private readonly Context previous = Context.Read();
        private readonly Context next;
        internal Binding(Context next) { this.next = next; next.Bind(); }
        public void Dispose() { try { next.CaptureCurrent(); } finally { previous.Bind(); } }
    }
    internal static IEnumerator Run(List<object> results)
    {
        if (!NativeArchitectureQa.Active) throw new InvalidOperationException("QA only");
        var ambient = Context.Read();
        var shopClient = new StoreClient { identifier = "qa_store", useClientBudget = true };
        shopClient.OverrideBudget(82);
        var jacksonClient = StoreClientList.CreateInventor(); jacksonClient.OverrideBudget(300);
        var shop = new Context { Client = StoreClientInstance.CreateClientInstance(shopClient),
            Phone = new StorePhoneClient(), Back = new GameGridInventory(18,4), Front = new GameGridInventory(18,4),
            Location = PlayerStore.PlayerLocation.IN_STORE };
        var jackson = new Context { Client = StoreClientInstance.CreateClientInstance(jacksonClient),
            Phone = new StorePhoneClient(), Back = new GameGridInventory(18,4), Front = new GameGridInventory(18,4),
            Location = PlayerStore.PlayerLocation.INVENTOR, Destination = new DrJacksonLocation() };
        var scope = new ActivityScope<Context>(state => new Binding(state));
        currentScope = scope;
        var storeToken = scope.Register(1, shop); var jacksonToken = scope.Register(2, jackson);
        var delayedStore = scope.Capture(storeToken, _ => {
            Require(!MapUIManager.Instance.IsOutside && PlayerStore.Instance.currentClientInstance == shop.Client, "store context");
            Require(EmporiumEntry.Instance.backInvinvElement == shop.Back, "store counter context");
            PlayerStore.Instance.currentClientInstance.GetClientBlueprint().ModBudget(-5);
            PhoneUIManager.Instance.currentPhoneClient.dialedBefore = true;
        });
        var delayedJackson = scope.Capture(jacksonToken, _ => {
            Require(MapUIManager.Instance.Current.Id == AfterhourLocation.DR_JACKSON &&
                PlayerStore.Instance.currentClientInstance == jackson.Client, "Jackson context");
            Require(EmporiumEntry.Instance.backInvinvElement == jackson.Back, "Jackson counter context");
            PlayerStore.Instance.currentClientInstance.GetClientBlueprint().ModBudget(-7);
        });
        try
        {
            yield return null; // Work arrives on later actual Unity frames, after the originating scope is gone.
            Require(delayedJackson(), "Jackson callback accepted");
            yield return null;
            Require(delayedStore(), "store callback accepted");
            Require(shopClient.GetBudget() == 77 && jacksonClient.GetBudget() == 293 &&
                shop.Phone.dialedBefore && !jackson.Phone.dialedBefore, "native state crossed activities");
            Require(PlayerStore.Instance.currentClientInstance == ambient.Client &&
                EmporiumEntry.Instance.backInvinvElement == ambient.Back && PhoneUIManager.Instance.currentPhoneClient == ambient.Phone &&
                MapUIManager.Instance.Current == ambient.Destination, "local frontend context not restored");
            results.Add(new { scenario = "native store/Jackson/phone field isolation across delayed Unity frames", status = "PASS",
                storeBudget = shopClient.GetBudget(), jacksonBudget = jacksonClient.GetBudget(),
                evidence = "Real native data and accessors inside explicit activity scopes; no travel/arrive/dialogue coroutine integration claimed" });

            // These are the actual native scheduling methods and Unity coroutines, not
            // an immediate call to the business callback. Only MetaHelper.WaitAndDo is
            // adapted here; other scheduling paths stay explicitly outside this proof.
            int counterCallbacks = 0, phoneCallbacks = 0;
            var counterLine = new Dialogue().SetEndAction(() => {
                Require(PlayerStore.Instance.currentClientInstance == shop.Client, "native dialogue lost store activity");
                shopClient.ModBudget(-3); counterCallbacks++;
            });
            var phoneLine = new Dialogue().SetEndAction(() => {
                Require(PlayerStore.Instance.currentClientInstance == jackson.Client &&
                    PhoneUIManager.Instance.currentPhoneClient == jackson.Phone, "native phone lost activity");
                jacksonClient.ModBudget(-11); phoneCallbacks++;
            });
            scope.Execute(storeToken, _ => AccessTools.Method(typeof(DialogUIManager), "ScheduleFlow")
                .Invoke(DialogUIManager.Instance, new object[] { 0.05f, (Action)(() => {
                    counterLine.ExecuteEndAction(); counterLine.ExecuteEndAction();
                }) }));
            scope.Execute(jacksonToken, _ => AccessTools.Method(typeof(DialoguePhoneUIManager), "ScheduleFlow")
                .Invoke(DialoguePhoneUIManager.Instance, new object[] { 0.02f, (Action)(() => {
                    phoneLine.ExecuteEndAction(); phoneLine.ExecuteEndAction();
                }) }));
            double deadline = Time.realtimeSinceStartupAsDouble + 5;
            while (counterCallbacks == 0 || phoneCallbacks == 0)
            {
                if (Time.realtimeSinceStartupAsDouble > deadline) throw new TimeoutException("Native dialogue scheduler callbacks");
                yield return null;
            }
            Require(counterCallbacks == 1 && phoneCallbacks == 1 && shopClient.GetBudget() == 74 && jacksonClient.GetBudget() == 282,
                "native dialogue end consequence duplicated or crossed activity");
            Require(PlayerStore.Instance.currentClientInstance == ambient.Client && PhoneUIManager.Instance.currentPhoneClient == ambient.Phone,
                "native delayed execution leaked into local frontend");
            results.Add(new { scenario = "native counter and phone ScheduleFlow / WaitAndDo / Dialogue.ExecuteEndAction", status = "PASS",
                counterCallbacks, phoneCallbacks, storeBudget = shopClient.GetBudget(), jacksonBudget = jacksonClient.GetBudget(),
                evidence = "Actual Unity coroutine delays and native end-action idempotence; no real dialogue choice or network coverage" });

            int retiredCallbacks = 0, retainedCallbacks = 0;
            scope.Execute(jacksonToken, _ => DialoguePhoneUIManager.Instance.StartCoroutine(
                MetaHelper.WaitAndDo(0.05f, () => retiredCallbacks++)));
            scope.Execute(storeToken, _ => DialogUIManager.Instance.StartCoroutine(
                MetaHelper.WaitAndDo(0.08f, () => retainedCallbacks++)));
            scope.Close(jacksonToken); scope.Register(2, jackson);
            deadline = Time.realtimeSinceStartupAsDouble + 5;
            while (retainedCallbacks == 0)
            {
                if (Time.realtimeSinceStartupAsDouble > deadline) throw new TimeoutException("Native retired callback check");
                yield return null;
            }
            Require(retiredCallbacks == 0 && retainedCallbacks == 1, "retired native coroutine executed business consequence");
            Require(!delayedJackson() && jacksonClient.GetBudget() == 282, "retired activity accepted old callback");
            scope.NewEpoch(); Require(!delayedStore(), "retired session accepted old callback");
            results.Add(new { scenario = "stale activity and session callback rejection", status = "PASS" });
        }
        finally
        {
            scope.NewEpoch(); // Invalidate any delayed native work when QA is stopped/disabled.
            currentScope = null;
            ambient.Bind();
            foreach (var grid in new[] { shop.Back, shop.Front, jackson.Back, jackson.Front })
                UnityEngine.Object.Destroy(grid.handler.gameObject);
        }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}

[HarmonyPatch(typeof(MetaHelper), nameof(MetaHelper.WaitAndDo))]
internal static class ArchitectureDelayBindingPatch
{
    private static void Prefix(ref Action action) => NativeActivityProbe.BindDelayedAction(ref action);
}
