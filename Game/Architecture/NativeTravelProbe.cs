using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;

namespace ProbablyCoop;

/// <summary>Native three-location baseline. Deliberately separate from activity
/// isolation: this runs vanilla Travel/Return and records the state they actually own.</summary>
internal static class NativeTravelProbe
{
    internal static IEnumerator Run()
    {
        if (!NativeArchitectureQa.Active) throw new InvalidOperationException("QA only");
        var rows = new List<object>();
        var failures = new List<string>();
        Application.LogCallback listener = (message, trace, type) => {
            if (type == LogType.Exception || type == LogType.Error) failures.Add(message + "\n" + trace);
        };
        Application.logMessageReceived += listener;
        try
        {
            // An explicit native baseline fixture, not an organically played evening.
            // Travel itself lacks the discovery/quota/phase checks from its UI caller.
            PlayerStore.Instance.visitLeftTonight = 3;
            foreach (var id in new[] { AfterhourLocation.DUMPING_GROUND, AfterhourLocation.COMMISSARY, AfterhourLocation.DR_JACKSON })
            {
                var location = AfterhourLocation.Get(id);
                if (location == null || !location.IsAvailable()) throw new InvalidOperationException("Fixture location unavailable: " + id);
                if (MapUIManager.Instance.IsOutside) throw new InvalidOperationException("Prior travel still active");
                var before = State(id);
                int quota = PlayerStore.Instance.visitLeftTonight;
                int visits = PlayerStore.Instance.GetAfterhourState(id).visits;
                int errorsBefore = failures.Count;
                MapUIManager.Instance.Travel(location);
                double deadline = Time.realtimeSinceStartupAsDouble + 12;
                while (MapUIManager.Instance.Current != location || Transit() || StoreUIManager.Instance.blackscreen.gameObject.activeSelf)
                {
                    if (Time.realtimeSinceStartupAsDouble > deadline) throw new TimeoutException("Native arrival: " + id);
                    yield return null;
                }
                if (PlayerStore.Instance.GetAfterhourState(id).visits != visits + 1)
                    throw new InvalidOperationException("Visit was not committed exactly once: " + id);
                var arrived = State(id);
                NativeArchitectureQa.CaptureFrame("travel-" + id + "-arrived");
                if (EmporiumEntry.Instance.afterhourInventory.childItems.Count != 0)
                    throw new InvalidOperationException("Baseline needs explicit ground-item confirmation: " + id);
                MapUIManager.Instance.Return();
                deadline = Time.realtimeSinceStartupAsDouble + 12;
                while (MapUIManager.Instance.IsOutside || Transit() || StoreUIManager.Instance.blackscreen.gameObject.activeSelf)
                {
                    if (Time.realtimeSinceStartupAsDouble > deadline) throw new TimeoutException("Native return: " + id);
                    yield return null;
                }
                if (PlayerStore.Instance.visitLeftTonight != quota - 1 ||
                    PlayerStore.Instance.playerLocation != PlayerStore.PlayerLocation.IN_STORE || failures.Count != errorsBefore)
                    throw new InvalidOperationException("Native return state/error assertion: " + id);
                rows.Add(new { id, status = "PASS", before, arrived, returned = State(id),
                    evidence = "Real native Travel/Return/Arrive/Leave and blackscreen coroutines; explicit quota fixture; no independent activity/network/input claim" });
                Write(rows, failures, "IN_PROGRESS");
            }
            Write(rows, failures, "BASELINE_TRAVEL_PASS");
        }
        finally
        {
            Application.logMessageReceived -= listener;
            Write(rows, failures, rows.Count == 3 && failures.Count == 0 ? "BASELINE_TRAVEL_PASS" : "INCOMPLETE");
        }
    }
    private static bool Transit() => (bool)AccessTools.Field(typeof(MapUIManager), "inTransit").GetValue(MapUIManager.Instance);
    private static object State(string id) => new {
        currentLocation = MapUIManager.Instance.Current?.Id, playerLocation = PlayerStore.Instance.playerLocation.ToString(),
        currentClient = PlayerStore.Instance.currentClientInstance?.GetClientBlueprint()?.identifier,
        PlayerStore.Instance.visitLeftTonight, visits = PlayerStore.Instance.GetAfterhourState(id).visits,
        stock = EmporiumEntry.Instance.invElement.childItems.Select(i => i.identifier).ToArray(),
        front = EmporiumEntry.Instance.frontInvinvElement.childItems.Select(i => i.identifier).ToArray(),
        back = EmporiumEntry.Instance.backInvinvElement.childItems.Select(i => i.identifier).ToArray(),
        temporaryCounter = EmporiumEntry.Instance.backInvinvElementCounter.childItems.Select(i => i.identifier).ToArray(),
        ground = EmporiumEntry.Instance.afterhourInventory.childItems.Select(i => i.identifier).ToArray(),
        storePhysicalActive = EmporiumEntry.Instance.storePhysical.activeSelf
    };
    private static void Write(List<object> rows, List<string> failures, string status) =>
        ModHelper.WriteModDataFileText("probably_coop", "architecture-travel-probe.json",
            JsonConvert.SerializeObject(new { status, cases = rows, nativeErrors = failures,
                independentActivities = "NOT_RUN; native callbacks still contain global UI and inventory effects" }, Formatting.Indented));
}
