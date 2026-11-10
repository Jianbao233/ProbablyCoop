using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ProbablyCoop;

/// <summary>Exhaustive registered-factory discovery, not a gameplay acceptance test.
/// Runs alone in a disposable QA process. Unknown graph layouts remain failures.</summary>
internal static class NativeCatalogProbe
{
    private static readonly FieldInfo[] Callbacks = typeof(GameItemFunc)
        .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
        .Where(f => typeof(Delegate).IsAssignableFrom(f.FieldType)).ToArray();

    internal static IEnumerator Run()
    {
        if (!NativeArchitectureQa.Active) throw new InvalidOperationException("QA only");
        var identifiers = DirectoryMaster.GetIdentifierList<GameItem>().OrderBy(x => x).ToArray();
        var rows = new List<object>();
        foreach (var id in identifiers)
        {
            GameItem source = null; GameItemElement mirror = null;
            var row = new JObject { ["id"] = id, ["construction"] = "NOT_RUN", ["graphRoundtrip"] = "NOT_RUN",
                ["normalTooltip"] = "NOT_RUN", ["advancedTooltip"] = "NOT_RUN", ["nativeInput"] = "NOT_RUN" };
            try
            {
                source = DirectoryMaster.Item(id);
                if (source == null) throw new InvalidOperationException("factory returned null");
                row["construction"] = "PASS";
                row["type"] = source.GetType().FullName;
                row["itemTypes"] = JArray.FromObject(source.itemTypes);
                row["contentLayout"] = source.contentWindow?.childElement?.GetType().FullName;
                row["examineLayout"] = source.examineWindow?.childElement?.GetType().FullName;
                var callbacks = new JArray();
                foreach (var field in Callbacks)
                    if (field.GetValue(source) is Delegate callback)
                        foreach (var part in callback.GetInvocationList())
                            callbacks.Add(new JObject { ["contract"] = field.Name, ["owner"] = part.Method.DeclaringType.FullName,
                                ["method"] = part.Method.Name, ["token"] = part.Method.MetadataToken.ToString("X8"),
                                ["assembly"] = part.Method.Module.Assembly.GetName().Name });
                row["callbacks"] = callbacks;
                // Source factories are exercised only in this throwaway authority.
                // Mirror construction still uses data only, never a guest factory.
                GeneralHelper.SetItemOwned(source, true);
                var beforeQueries = NativeMirrorProbe.CaptureValues(source);
                // Vanilla tooltip helpers can lazily allocate identifiers/names.
                // These effects belong to the authority before publishing a view.
                var sourceNormal = JToken.FromObject(NativeMirrorProbe.Tooltip(((GameItemElement)source).GetTooltip()));
                var sourceAdvanced = JToken.FromObject(NativeMirrorProbe.Tooltip(((GameItemElement)source).GetTooltipAdvanced()));
                row["sourceValuesMutatedByQueries"] = !JToken.DeepEquals(beforeQueries, NativeMirrorProbe.CaptureValues(source));
                var queryChanges = new List<string>();
                NativeMirrorProbe.Diff(beforeQueries, NativeMirrorProbe.CaptureValues(source), "item", queryChanges);
                row["sourceQueryChanges"] = JArray.FromObject(queryChanges);
                // Re-read the view after native lazy initialization. Record rather than
                // disguise this write; it disqualifies blindly running queries on guests.
                sourceNormal = JToken.FromObject(NativeMirrorProbe.Tooltip(((GameItemElement)source).GetTooltip()));
                var snapshot = NativeMirrorProbe.Capture(source);
                mirror = NativeMirrorProbe.Hydrate(JObject.Parse(snapshot.ToString(Formatting.None)));
                var differences = new List<string>();
                NativeMirrorProbe.Diff(snapshot, NativeMirrorProbe.Capture(mirror), "item", differences);
                row["graphRoundtrip"] = differences.Count == 0 ? "PASS" : "FAIL";
                row["differences"] = JArray.FromObject(differences);
                row["normalTooltip"] = JToken.DeepEquals(sourceNormal,
                    JToken.FromObject(NativeMirrorProbe.Tooltip(mirror.GetTooltip()))) ? "PASS" : "FAIL";
                row["advancedTooltip"] = JToken.DeepEquals(sourceAdvanced,
                    JToken.FromObject(NativeMirrorProbe.Tooltip(mirror.GetTooltipAdvanced()))) ? "PASS" : "FAIL";
                if ((string)row["advancedTooltip"] == "FAIL")
                    row["advancedDifference"] = new JObject { ["source"] = JToken.FromObject(NativeMirrorProbe.Tooltip(((GameItemElement)source).GetTooltipAdvanced())),
                        ["mirror"] = JToken.FromObject(NativeMirrorProbe.Tooltip(mirror.GetTooltipAdvanced())) };
            }
            catch (Exception e) { row["error"] = e.GetType().Name + ": " + e.Message; }
            finally
            {
                try { mirror?.Destroy(); source?.Destroy(); }
                catch (Exception e) { row["cleanupError"] = e.GetType().Name + ": " + e.Message; }
            }
            rows.Add(row);
            // Keep progress durable even if a later native factory fails the process.
            ModHelper.WriteModDataFileText("probably_coop", "architecture-catalog-probe.json",
                JsonConvert.SerializeObject(new { totalRegistered = identifiers.Length, visited = rows.Count, cases = rows,
                    scope = "Registered native factories, initial state only; no activation, network, input or gameplay coverage",
                    release = "NOT_READY" }, Formatting.Indented));
            yield return null;
        }
    }
}
