using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace ProbablyCoop;

/// <summary>Opt-in architecture audit. Runs only in the disposable QA Host profile.
/// Never participates in a network session, never saves, never injects OS input.</summary>
internal sealed class NativeArchitectureQa
{
    internal static bool Active { get; private set; }
    private readonly double started = Time.realtimeSinceStartupAsDouble;
    private bool loaded, finished, checkedPlatformIsolation;
    private double sceneReady;
    private IEnumerator uiProbe;
    private RuntimeInventory runtimeInventory;
    private readonly List<object> negotiationResults = new List<object>();
    private readonly List<object> activityResults = new List<object>();
    private bool activityStarted, tooltipPassed;
    private static bool CatalogRequested => Array.IndexOf(Environment.GetCommandLineArgs(), "--probably-coop-qa-catalog") >= 0;
    private static bool TravelRequested => Array.IndexOf(Environment.GetCommandLineArgs(), "--probably-coop-qa-travel") >= 0;

    internal static NativeArchitectureQa Create()
    {
        var args = Environment.GetCommandLineArgs();
        if (Array.IndexOf(args, "--probably-coop-architecture-qa") < 0) return null;
        if (!Application.isBatchMode || !NativeBackgroundQa.IsHostProduct ||
            Array.IndexOf(args, "--probably-coop-local") < 0)
        {
            Debug.LogError("[probably_coop architecture] refused: isolated QA Host and batch mode required");
            return null;
        }
        Active = true;
        Application.runInBackground = true;
        Application.targetFrameRate = 30;
        return new NativeArchitectureQa();
    }

    internal void Tick()
    {
        if (finished) return;
        try
        {
            if(!checkedPlatformIsolation)
            {
                if(AchievementTracker.Instance==null) return;
                NativeQaPlatformWrites.Verify(); checkedPlatformIsolation=true;
                Debug.Log("[probably_coop architecture] PASS qa-platform-write-isolation");
            }
            if (Time.realtimeSinceStartupAsDouble - started > 180) throw new TimeoutException("architecture QA load");
            if (uiProbe != null)
            {
                if (uiProbe.MoveNext()) return;
                if (TravelRequested)
                {
                    Write("architecture-results.json", new { status = "BASELINE_TRAVEL_PASS", independentActivities = "NOT_RUN", fullPlaythrough = "NOT_RUN" });
                    Finish(0); return;
                }
                if (CatalogRequested)
                {
                    Write("architecture-results.json", new { status = "CATALOG_COMPLETE", release = "NOT_READY",
                        scope = "Discovery completed; inspect individual unsupported graphs and tooltips, not a gameplay pass" });
                    Finish(0); return;
                }
                if (!activityStarted)
                {
                    Write("architecture-negotiation-probe.json", negotiationResults);
                    activityStarted = true;
                    uiProbe = NativeActivityProbe.Run(activityResults);
                    return;
                }
                Write("architecture-activity-probe.json", activityResults);
                bool passed = tooltipPassed && negotiationResults.Count == NativeNegotiationProbe.ExpectedCases && activityResults.Count == 3 &&
                    negotiationResults.Concat(activityResults).All(r => (string)JObject.FromObject(r)["status"] == "PASS");
                Write("architecture-results.json", new {
                    status = passed ? "PROBES_PASS" : "PROBES_FAILED", product = Application.productName,
                    profile = Application.persistentDataPath, scene = SceneManager.GetActiveScene().name,
                    componentCount = runtimeInventory.components.Count, eventCount = runtimeInventory.events.Count,
                    visualEvidence = GraphicsRequested ? "CAPTURE_REQUESTED: inspect PNGs; no pixel parity claim" : "NOT_RUN: nographics; native semantic output only",
                    fullPlaythrough = "NOT_RUN: architecture gate, not a release acceptance"
                });
                Finish(passed ? 0 : 1);
                return;
            }
            if (!loaded && SceneManager.GetActiveScene().name == "EmporiumMenu" && MainMenuUIController.Instance != null)
            {
                var checkpoints = CoopRun.ListCheckpoints();
                if (checkpoints.Count != 1 || !CoopRun.CanResume(checkpoints[0]))
                    throw new InvalidOperationException("expected one dedicated registered checkpoint");
                loaded = true;
                NewGameData.HardReset();
                NewGameData.Instance.saveSlot = checkpoints[0].Slot;
                MainMenuUIController.Instance.StartNewGameClick();
                return;
            }
            if (!loaded || EmporiumEntry.Instance?.invElement == null || !PlayerStore.IsInstanceExist()) return;
            if (sceneReady == 0) { sceneReady = Time.realtimeSinceStartupAsDouble; return; }
            if (Time.realtimeSinceStartupAsDouble - sceneReady < 3) return;
            if (!CoopRun.IsRegistered(PlayerStore.Instance)) throw new InvalidOperationException("not a cooperative QA slot");
            if (!TransitUI.EnsureCreated(MapUIManager.Instance.transitPrefab))
                throw new InvalidOperationException("Native transit prefab failed to instantiate");
            runtimeInventory = CaptureScene();
            Write("architecture-runtime-inventory.json", runtimeInventory);
            Write("architecture-runtime-registry.json", new {
                itemDirectories = DirectoryMaster.GetDictionaryClassNameList<GameItem>(),
                items = DirectoryMaster.GetIdentifierList<GameItem>().Select(id => {
                    var method = DirectoryMaster.GetFactory<GameItem>(id).Method;
                    return new { id, factoryType = method.DeclaringType.FullName, method = method.Name,
                        assembly = method.Module.Assembly.GetName().Name, token = method.MetadataToken.ToString("X8") };
                }).ToArray()
            });
            if (CatalogRequested) { uiProbe = NativeCatalogProbe.Run(); return; }
            if (TravelRequested) { uiProbe = NativeTravelProbe.Run(); return; }
            var probe = NativeMirrorProbe.Run();
            Write("architecture-ui-probe.json", probe);
            tooltipPassed = JObject.FromObject(probe)["cases"].All(r => (string)r["status"] == "PASS");
            uiProbe = NativeNegotiationProbe.Run(negotiationResults);
        }
        catch (Exception ex)
        {
            Debug.LogError("[probably_coop architecture] " + ex);
            (uiProbe as IDisposable)?.Dispose();
            Write("architecture-results.json", new { status = "FAIL", error = ex.ToString() });
            Finish(1);
        }
    }

    private static void Write(string name, object data) => ModHelper.WriteModDataFileText("probably_coop", name,
        JsonConvert.SerializeObject(data, Formatting.Indented, new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.None }));
    private void Finish(int code) { finished = true; Application.Quit(code); }
    internal void Stop()
    {
        finished = true;
        (uiProbe as IDisposable)?.Dispose();
        uiProbe = null;
        Active = false;
    }
    internal static bool GraphicsRequested => Array.IndexOf(Environment.GetCommandLineArgs(), "--probably-coop-qa-graphics") >= 0;
    internal static void CaptureFrame(string name)
    {
        if (!Active || !GraphicsRequested) return;
        if (UnityEngine.SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
            throw new InvalidOperationException("Graphics QA requested without a graphics device");
        NativeOffscreenCapture.Capture(name);
    }

    private static string PathOf(Transform t) => t.parent == null ? t.name : PathOf(t.parent) + "/" + t.name;
    private sealed class RuntimeInventory
    {
        public readonly List<object> components = new List<object>();
        public readonly List<object> events = new List<object>();
    }
    private static RuntimeInventory CaptureScene()
    {
        var result = new RuntimeInventory();
        foreach (var component in Resources.FindObjectsOfTypeAll<MonoBehaviour>())
        {
            if (component == null || !component.gameObject.scene.IsValid()) continue;
            var type = component.GetType();
            var path = PathOf(component.transform);
            var fields = new List<object>();
            // Restrict field reflection to the three native layout decode gaps.
            var layoutFields = component is TransitFrame ? typeof(TransitFrame).GetFields(BindingFlags.Instance | BindingFlags.Public)
                : component is DropdownLocalizer ? typeof(DropdownLocalizer).GetFields(BindingFlags.Instance | BindingFlags.Public)
                : Array.Empty<FieldInfo>();
            foreach (var field in layoutFields)
            {
                if (typeof(UnityEngine.Object).IsAssignableFrom(field.FieldType))
                {
                    var obj = field.GetValue(component) as UnityEngine.Object;
                    fields.Add(new { field = field.Name, type = field.FieldType.FullName, target = obj?.GetInstanceID(), targetName = obj?.name });
                }
                if (typeof(UnityEventBase).IsAssignableFrom(field.FieldType))
                    ReadEvent(result, component, path, field.Name, field.GetValue(component) as UnityEventBase);
            }
            // Read only known event accessors; never enumerate arbitrary gameplay getters.
            if (component is Button b) ReadEvent(result, component, path, "onClick", b.onClick);
            if (component is Toggle t) ReadEvent(result, component, path, "onValueChanged", t.onValueChanged);
            if (component is Slider s) ReadEvent(result, component, path, "onValueChanged", s.onValueChanged);
            if (component is TMP_Dropdown d) ReadEvent(result, component, path, "onValueChanged", d.onValueChanged);
            if (component is TMP_InputField input)
            {
                ReadEvent(result, component, path, "onValueChanged", input.onValueChanged);
                ReadEvent(result, component, path, "onEndEdit", input.onEndEdit);
                ReadEvent(result, component, path, "onSubmit", input.onSubmit);
            }
            result.components.Add(new { instance = component.GetInstanceID(), type = type.FullName,
                assembly = type.Assembly.GetName().Name, path, active = component.gameObject.activeInHierarchy,
                component.enabled, fields });
        }
        return result;
    }

    private static void ReadEvent(RuntimeInventory result, Component c, string path, string name, UnityEventBase e)
    {
        if (e == null) return;
        var calls = new List<object>();
        for (int i = 0; i < e.GetPersistentEventCount(); i++)
        {
            var target = e.GetPersistentTarget(i);
            calls.Add(new { target = target?.GetInstanceID(), type = target?.GetType().FullName,
                method = e.GetPersistentMethodName(i), state = e.GetPersistentListenerState(i).ToString() });
        }
        result.events.Add(new { instance = c.GetInstanceID(), path, name, persistent = calls,
            dynamicListeners = "see IL registration graph; runtime private listener list not inspected" });
    }

    [HarmonyPatch(typeof(PlayerStore), nameof(PlayerStore.SaveGame))]
    private static class NeverSaveAudit
    {
        private static bool Prefix()
        {
            if (!Active) return true;
            throw new InvalidOperationException("Architecture QA is read-only on the dedicated checkpoint");
        }
    }
}
