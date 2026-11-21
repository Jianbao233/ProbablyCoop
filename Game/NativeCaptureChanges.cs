using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Core;
using UnityEngine;

namespace ProbablyCoop;

internal sealed partial class NativeWorld
{
    private sealed class CapturedRoot
    {
        internal ulong Version;
        internal bool Confirmed;
        internal GameInventory Parent;
        internal GameItem Root;
        internal NativeActorInventory.RootKey Key;
        internal long OwnAudit,NextAudit;
        internal uint[] Nodes;
        internal TooltipTicket[] Tooltips;
    }
    private readonly CaptureChangeJournal<GameItem> captureChanges=new CaptureChangeJournal<GameItem>();
    private readonly Dictionary<GameItem,CapturedRoot> captureRoots=new Dictionary<GameItem,CapturedRoot>();
    private readonly HashSet<GameItem> capturedRootsThisPass=new HashSet<GameItem>();
    private readonly HashSet<GameItem> untrackedCaptureRoots=new HashSet<GameItem>();
    private sealed class CaptureScope
    {
        internal GameItem Item;
        internal readonly List<uint> Nodes=new List<uint>();
        internal long NextAudit=long.MaxValue;
    }
    private CaptureScope captureScope;
    private bool capturingChanges;
    private long capturePass;
    private object[] captureQueryValues;
    private JObject captureQueryActors,captureQueryClient;
    private object captureQueryScene;
    private bool incrementalCapture;
    private static readonly bool FullCaptureRequested=Array.IndexOf(Environment.GetCommandLineArgs(),"--probably-coop-full-capture")>=0;
    private static readonly Lazy<FieldInfo[]> CaptureQueryFields=new Lazy<FieldInfo[]>(()=>
        WorldFields.Where(f=>f.Name is not ("totalPlayTimeSeconds" or "runPlayTimeSeconds")).ToArray());
    private Dictionary<uint,(GameItem Item,ulong Version)> pendingCaptureVersions;
    private long captureReadCount,captureReuseCount;
    private double nextCaptureChangeStats;

    internal bool TracksCaptureSource(object source)=>host && !closed && !capturingChanges && captureChanges.Tracks(source);
    internal void NoticeCaptureChange(object source)
    {
        if(host && !closed && !capturingChanges) captureChanges.Changed(source);
    }
    private void ResetCaptureChanges()
    {
        captureChanges.Clear(); captureRoots.Clear(); untrackedCaptureRoots.Clear(); pendingCaptureVersions=null;
        captureQueryValues=null; captureQueryActors=captureQueryClient=null; captureQueryScene=null;
    }
    private void BeginChangedCapture(JObject actors,JObject client)
    {
        if(FullCaptureRequested) return;
        // Shared/person-dependent queries (ownership, price, permissions and
        // tooltips) cannot inherit a result from a different business context.
        var store=PlayerStore.Instance;
        var values=CaptureQueryFields.Value.Select(f=>f.GetValue(store)).ToArray();
        object scene=MapUIManager.Instance.Current;
        if(captureQueryValues==null || !values.SequenceEqual(captureQueryValues) ||
            !JToken.DeepEquals(actors,captureQueryActors) || !JToken.DeepEquals(client,captureQueryClient) ||
            !ReferenceEquals(scene,captureQueryScene)) captureChanges.InvalidateAll();
        captureQueryValues=values; captureQueryActors=actors; captureQueryClient=(JObject)client?.DeepClone(); captureQueryScene=scene;
        // Independent rotating verification catches direct field/list writes by
        // content mods. Mutations through native setters do not wait for it.
        ++capturePass;
        capturedRootsThisPass.Clear();
        pendingCaptureVersions=new Dictionary<uint,(GameItem,ulong)>();
        incrementalCapture=!FullCaptureRequested && NativeCaptureComparison.Current==null && authorityBasis!=null &&
            !link.NeedsFull && link.Revision-authorityBaseRevision<96;
    }
    private JObject CaptureChangedRoot(GameItem item,NativeDeferredFields fields)
    {
        if(FullCaptureRequested) return NativeMirrorProbe.CaptureUncached(item);
        model.CaptureItem=CaptureChangedItem;
        try { return CaptureChangedItem(item); }
        finally { model.CaptureItem=null; }
    }
    private JObject CaptureChangedItem(GameItem item)
    {
        var fields=model.Deferred;
        var parentScope=captureScope;
        if(parentScope!=null) captureChanges.Observe(parentScope.Item,item);
        capturedRootsThisPass.Add(item);
        uint id=model.Id(item);
        captureRoots.TryGetValue(item,out var previous);
        bool unchanged=incrementalCapture && !untrackedCaptureRoots.Contains(item) && previous!=null && previous.Confirmed &&
            captureChanges.IsCurrent(item,previous.Version) && ReferenceEquals(previous.Parent,item.parentInventory) &&
            ReferenceEquals(previous.Root,captureRoot) && previous.Key.Equals(captureTooltipKey) && authorityBasis.Contains(id);
        if(unchanged && capturePass<previous.NextAudit && !previous.Nodes.Contains(remoteHover) && !previous.Nodes.Any(node=>remoteViews.Contains(node)))
        {
            foreach(uint node in previous.Nodes)
            {
                model.Seen.Add(node);
                if(model.Objects.TryGetValue(node,out var child) && child is GameItem nested) capturedRootsThisPass.Add(nested);
            }
            parentScope?.Nodes.AddRange(previous.Nodes);
            if(parentScope!=null) parentScope.NextAudit=Math.Min(parentScope.NextAudit,previous.NextAudit);
            tooltipTickets.AddRange(previous.Tooltips);
            pendingCaptureVersions[id]=(item,previous.Version);
            captureReuseCount++;
            return fields.BorrowRecord(authorityBasis,id,nested:parentScope!=null);
        }
        if(unchanged) fields.AuditedRecords.Add(id);
        captureReadCount++;
        captureChanges.Forget(item); captureChanges.Version(item);
        var scope=new CaptureScope {Item=item}; captureScope=scope;
        int firstTooltip=tooltipTickets.Count;
        bool wasCapturing=capturingChanges; capturingChanges=true;
        JObject result;
        try { result=NativeMirrorProbe.CaptureUncached(item); }
        finally { capturingChanges=wasCapturing; captureScope=parentScope; }
        parentScope?.Nodes.AddRange(scope.Nodes);
        long audit=previous==null ? capturePass+1+id%8 : previous.OwnAudit<=capturePass ? capturePass+8 : previous.OwnAudit;
        var entry=new CapturedRoot { Version=captureChanges.Version(item),Parent=item.parentInventory,Root=captureRoot,Key=captureTooltipKey,
            OwnAudit=audit,NextAudit=Math.Min(audit,scope.NextAudit),
            Nodes=scope.Nodes.ToArray(),Tooltips=tooltipTickets.Skip(firstTooltip).ToArray() };
        if(parentScope!=null) parentScope.NextAudit=Math.Min(parentScope.NextAudit,entry.NextAudit);
        captureRoots[item]=entry; pendingCaptureVersions[id]=(item,entry.Version);
        return result;
    }
    private void ObserveCaptureSource(object source)
    {
        if(capturingChanges && captureScope!=null) captureChanges.Observe(captureScope.Item,source);
    }
    private void ObserveCaptureIdentity(object source,uint id)
    {
        if(!capturingChanges || captureScope==null) return;
        ObserveCaptureSource(source); captureScope.Nodes.Add(id);
    }
    private void ObserveCapturedItem(GameItem item)
    {
        ObserveCaptureSource(item); ObserveCaptureSource(item.state); ObserveCaptureSource(item.modifiedState);
        ObserveCaptureSource(item.shape); ObserveCaptureSource(item.modifiedShape);
        foreach(var tag in item.state.dict.Values) ObserveCaptureSource(tag);
        foreach(var tag in item.modifiedState.dict.Values) ObserveCaptureSource(tag);
        foreach(var feature in item.itemFeatures)
        { ObserveCaptureSource(feature); ObserveCaptureSource(feature.realCondition); ObserveCaptureSource(feature.fakeCondition); }
    }
    private void EndChangedCapture()
    {
        if(FullCaptureRequested) return;
        foreach(var item in captureRoots.Keys.Where(item=>!capturedRootsThisPass.Contains(item)).ToArray())
        { captureRoots.Remove(item); captureChanges.Forget(item); untrackedCaptureRoots.Remove(item); }
        if((NativeBackgroundQa.Enabled || NativePerformance.Detailed) && Time.realtimeSinceStartupAsDouble>=nextCaptureChangeStats)
        {
            Debug.Log($"[probably_coop PERF] change_capture read={captureReadCount} reused={captureReuseCount} roots={captureRoots.Count} fallback={untrackedCaptureRoots.Count} sources={captureChanges.SourceCount}");
            nextCaptureChangeStats=Time.realtimeSinceStartupAsDouble+10;
            captureReadCount=captureReuseCount=0;
        }
    }
    private void ConfirmCapturedRecords(EncodedSnapshot packed)
    {
        if(pendingCaptureVersions==null) return;
        foreach(var pair in pendingCaptureVersions)
        {
            if(packed.UntrackedRecords.Contains(pair.Key) && untrackedCaptureRoots.Add(pair.Value.Item))
                Debug.Log($"[probably_coop] capture audit retained native refresh for entity={pair.Key}");
            if(captureRoots.TryGetValue(pair.Value.Item,out var entry) && entry.Version==pair.Value.Version &&
                captureChanges.IsCurrent(pair.Value.Item,pair.Value.Version))
                entry.Confirmed=packed.Checkpoint!=null || packed.ConfirmedRecords.Contains(pair.Key);
        }
        pendingCaptureVersions=null;
    }
}

// Native mutation families, not item identifiers or mod-defined callback names.
// These hooks only mark affected records; they never replay game rules.
[HarmonyPatch]
internal static class NativeCaptureMutationHooks
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        var types=new[] {typeof(GameItem),typeof(GameItemElement),typeof(GameGridInventory),typeof(GameSlotInventory),
            typeof(GameGridScrollableInventory),typeof(GridShapeBuilder),typeof(PixelWindow),typeof(MachineryButton),
            typeof(MachineryRadioList),typeof(MachineryRadioButton),typeof(MachinerySlider),typeof(MachineryRangeSlider),
            typeof(MachineryText),typeof(MachineryLabel),typeof(MachineryPanel),typeof(MachineryPanelRow),typeof(MachineryTabs),
            typeof(MachineryStack),typeof(MachineryIntake),typeof(GridPixelElement),typeof(IndicatorElement),typeof(TagElement),
            typeof(PushButtonElement),typeof(AdvButtonElement),typeof(ItemFeature),typeof(ItemCondition),typeof(TagSystem)};
        foreach(var type in types)
        foreach(var method in type.GetMethods(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.DeclaredOnly))
        {
            if(method.IsAbstract || method.ContainsGenericParameters) continue;
            var body=method.GetMethodBody()?.GetILAsByteArray();
            // Several native interface implementations consist solely of
            // throw new NotImplementedException. They cannot change anything;
            // Mono's Harmony wrapper cannot attach a postfix to these stubs.
            if(body==null || body.Length==0 || body.Length<=8 && body[body.Length-1]==0x7a) continue;
            string name=method.Name;
            if(name.StartsWith("set_",StringComparison.Ordinal) || name.StartsWith("Set",StringComparison.Ordinal) ||
                name.StartsWith("Add",StringComparison.Ordinal) || name.StartsWith("Remove",StringComparison.Ordinal) ||
                name.StartsWith("Attach",StringComparison.Ordinal) || name.StartsWith("Detach",StringComparison.Ordinal) ||
                name.StartsWith("Resize",StringComparison.Ordinal) || name is "Validate" or "ValidateShapeState" or "SyncModifiedState" or
                "Clear" or "Destroy" or "EnableTag" or "DisableTag" or "ToggleTag") yield return method;
        }
    }
    private static void Postfix(object __instance)=>NativeWorld.Host?.NoticeCaptureChange(__instance);
}

[HarmonyPatch]
internal static class NativeCaptureTagMutations
{
    private static IEnumerable<MethodBase> TargetMethods()=>typeof(TagState).GetMethods(BindingFlags.Public|BindingFlags.Instance|BindingFlags.DeclaredOnly)
        .Where(m=>!m.IsSpecialName && m.GetParameters().Length<=1 && m.Name is not ("Clone" or "IsEnabled" or "GetString" or "GetInt" or "GetFloat" or "GetLong" or "GetDouble" or "GetBool"));
    private static NativeTagRow Read(TagState tag)=>new NativeTagRow(tag.valueEnabled,tag.valueString,tag.valueInt,tag.valueFloat,
        tag.valueLong,tag.valueDouble,tag.valueBool,tag.identifier,tag.identifierName);
    private static void Prefix(TagState __instance,out (bool Tracked,NativeTagRow Row) __state)
    { bool tracked=NativeWorld.Host?.TracksCaptureSource(__instance)==true; __state=(tracked,tracked ? Read(__instance) : default); }
    private static void Postfix(TagState __instance,(bool Tracked,NativeTagRow Row) __state)
    { if(__state.Tracked && !__state.Row.SameValues(Read(__instance))) NativeWorld.Host?.NoticeCaptureChange(__instance); }
}

[HarmonyPatch]
internal static class NativeCaptureNewTag
{
    private static IEnumerable<MethodBase> TargetMethods()=>typeof(TagSystem).GetMethods(BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.DeclaredOnly)
        .Where(m=>m.Name.StartsWith("InitTag",StringComparison.Ordinal) && m.ReturnType==typeof(bool) && !m.ContainsGenericParameters);
    private static void Postfix(TagSystem __instance,bool __result)
    { if(__result) NativeWorld.Host?.NoticeCaptureChange(__instance); }
}
