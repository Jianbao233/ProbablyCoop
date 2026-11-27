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
    private readonly Queue<GameItem> auditQueue=new Queue<GameItem>();
    private readonly HashSet<GameItem> queuedForAudit=new HashSet<GameItem>();
    private double nextStockAudit;
    private object[] captureQueryValues;
    private JObject captureQueryActors,captureQueryClient;
    private object captureQueryScene;
    private bool borrowCapturedRecords;
    internal static readonly bool ForceFullCaptureCadence=
        Array.IndexOf(Environment.GetCommandLineArgs(),"--probably-coop-full-capture")>=0;
    private bool nativeFullCapture;
    private bool FullCaptureRequested=>ForceFullCaptureCadence || nativeFullCapture;
    private static readonly Lazy<FieldInfo[]> CaptureQueryFields=new Lazy<FieldInfo[]>(()=>
        WorldFields.Where(f=>f.Name is not ("totalPlayTimeSeconds" or "runPlayTimeSeconds")).ToArray());
    private Dictionary<uint,(GameItem Item,ulong Version)> pendingCaptureVersions;
    private long captureReadCount,captureReuseCount;
    private double nextCaptureChangeStats;

    internal bool TracksCaptureSource(object source)=>host && !closed &&
        ((!capturingChanges && captureChanges.Tracks(source)) || checkpointPreparation?.Changes.Tracks(source)==true);
    internal void NoticeCaptureChange(object source,bool wake=true)
    {
        if(!host || closed) return;
        checkpointPreparation?.Changes.Changed(source);
        if(capturingChanges) return;
        bool observed=captureChanges.Changed(source);
        bool inventory=source is GameInventory && model.IsCaptured(source);
        if(wake && (observed || inventory) && (!InNativeItemTick || inventory)) captureWake=true;
    }
    // Verify ordinary fields and tag dictionaries in short slices on the
    // Unity thread. An unhooked write dirties the owning branch, including
    // parents. Keep the full rotating audit for features and opaque UI state.
    private void VerifyStockSlice(double now)
    {
        if(now<nextStockAudit || !denseTooltips || authorityBasis==null) return;
#if PROBABLY_COOP_GRID_QA
        using var auditTiming = NativeFrameAttribution.Measure(NativeFrameAttribution.Stage.StockAudit);
#endif
        nextStockAudit=now+0.04;
        long deadline=System.Diagnostics.Stopwatch.GetTimestamp()+System.Diagnostics.Stopwatch.Frequency/2500;
        int count=Math.Min(24,auditQueue.Count);
        for(int i=0;i<count;i++)
        {
            var item=auditQueue.Dequeue();
            if(captureRoots.TryGetValue(item,out var root))
            {
                auditQueue.Enqueue(item);
                if(root.Confirmed && captureChanges.IsCurrent(item,root.Version) &&
                    !NativeMirrorProbe.MatchesStockSample(item,frozenStock))
                {
                    untrackedCaptureRoots.Add(item);
                    NoticeCaptureChange(item);
                }
            }
            else queuedForAudit.Remove(item);
            if(System.Diagnostics.Stopwatch.GetTimestamp()>=deadline) break;
        }
    }

    private void ResetCaptureChanges()
    {
        captureChanges.Clear(); captureRoots.Clear(); untrackedCaptureRoots.Clear(); pendingCaptureVersions=null;
        captureQueryValues=null; captureQueryActors=captureQueryClient=null; captureQueryScene=null;
        capturedItemNames.Clear(); refreshItemNames=true; captureWake=true;
        auditQueue.Clear(); queuedForAudit.Clear(); nextStockAudit=0; externalCaptureWriters=null; capturedViewInterest=null;
        checkpointPreparation=null; checkpointPreparationDone=false;
    }
    private void BeginChangedCapture(JObject actors,JObject client)
    {
        // Mods can write native fields directly on Android. An unaudited
        // Windows build also needs full capture if no hooks were installed.
        if(!FullCaptureRequested && (NativePlatform.IsAndroid
            ? !NativePlatform.AndroidCaptureReady || HasEnabledCaptureWriters()
            : !NativeCaptureMutationHooks.Ready))
        {
            nativeFullCapture=true;
            borrowCapturedRecords=false;
            if(captureRoots.Count!=0) ResetCaptureChanges();
            Debug.Log("[probably_coop] Inventory uses full capture");
        }
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
        // A periodic full checkpoint can expand locally verified records on
        // the worker. Initial joins and explicit repair keep a fresh capture.
        borrowCapturedRecords=NativeCaptureComparison.Current==null && authorityBasis!=null && !link.NeedsFull;
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
        bool unchanged=borrowCapturedRecords && !untrackedCaptureRoots.Contains(item) && previous!=null && previous.Confirmed &&
            captureChanges.IsCurrent(item,previous.Version) && ReferenceEquals(previous.Parent,item.parentInventory) &&
            ReferenceEquals(previous.Root,captureRoot) && previous.Key.Equals(captureTooltipKey) && authorityBasis.Contains(id);
        if(unchanged && capturePass<previous.NextAudit && (remoteHover==0 || !previous.Nodes.Contains(remoteHover)) && (remoteViews.Count==0 || !previous.Nodes.Any(remoteViews.Contains)))
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
        int auditPasses=denseTooltips && !HasExternalCaptureWriters ? 32 : 8;
        if(denseTooltips && queuedForAudit.Add(item)) auditQueue.Enqueue(item);
        long audit=previous==null ? capturePass+1+id%(uint)auditPasses : previous.OwnAudit<=capturePass ? capturePass+auditPasses : previous.OwnAudit;
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
        { captureRoots.Remove(item); captureChanges.Forget(item); untrackedCaptureRoots.Remove(item); refreshItemNames|=capturedItemNames.Remove(item); }
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
    private static readonly Lazy<MethodBase[]> Targets = new(ResolveWindowsTargets);
    internal static bool Ready => NativeInventoryCaptureContract.MatchesWindowsBuild && Targets.Value.Length != 0;

    [HarmonyPrepare]
    private static bool Prepare() => !NativePlatform.IsAndroid && Ready;

    private static IEnumerable<MethodBase> TargetMethods() => Targets.Value;

    private static MethodBase[] ResolveWindowsTargets()
    {
        try { return NativeInventoryCaptureContract.ResolveMethods(); }
        catch (Exception error)
        {
            Debug.LogWarning("[probably_coop] Windows inventory hooks unavailable; using full capture: " + error.Message);
            return Array.Empty<MethodBase>();
        }
    }
    internal static void Postfix(object __instance,MethodBase __originalMethod)
    {
        // Validate is also called for visual redraws. Still invalidate the
        // captured record, without scheduling an entire inventory on a redraw.
        string name=__originalMethod.Name;
        NativeWorld.Host?.NoticeCaptureChange(__instance,name is not ("Validate" or "ValidateShapeState" or "SyncModifiedState"));
    }
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
