using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ProbablyCoop;

/// <summary>Session-scoped native identities. No network-supplied method/type names.</summary>
internal sealed class NativeModelContext
{
    private sealed class Identity { internal uint Id; }
    private readonly ConditionalWeakTable<object, Identity> ids = new ConditionalWeakTable<object, Identity>();
    private readonly ConditionalWeakTable<object, Identity> replicaIds = new ConditionalWeakTable<object, Identity>();
    private uint next;
    internal readonly Dictionary<uint, object> Objects = new Dictionary<uint, object>();
    internal readonly Dictionary<uint, JObject> Data = new Dictionary<uint, JObject>();
    internal readonly HashSet<uint> Seen = new HashSet<uint>();
    internal Action<object, JObject> Captured, Applied;
    internal Action<GameItem> CapturingItem;
    internal GameItem CaptureOwner;
    internal NativeSnapshotIndex Prepared { get; private set; }
    internal NativeDeferredFields Deferred { get; private set; }
    internal uint Id(object value) => value == null ? 0 : ids.GetValue(value, _ => new Identity { Id = ++next }).Id;
    internal uint ReplicaId(object value)
    {
        if(value==null) return 0;
        if(replicaIds.TryGetValue(value,out var cached) &&
            Objects.TryGetValue(cached.Id,out var current) && ReferenceEquals(current,value)) return cached.Id;
        // Validate cached identities against the live registry: rebuilding a
        // renderer or sweeping an entity must never resurrect its old identity.
        foreach (var pair in Objects) if (ReferenceEquals(pair.Value, value))
        {
            replicaIds.Remove(value); replicaIds.Add(value,new Identity { Id=pair.Key });
            return pair.Key;
        }
        return 0;
    }
    internal IDisposable Enter()
    {
        var previous = NativeMirrorProbe.Context; NativeMirrorProbe.Context = this;
        return new Scope(() => NativeMirrorProbe.Context = previous);
    }
    internal IDisposable Enter(NativeSnapshotIndex prepared)
    {
        var previous=NativeMirrorProbe.Context; var oldPrepared=Prepared;
        Prepared=prepared; NativeMirrorProbe.Context=this;
        foreach(uint id in prepared.Nodes.Keys) Seen.Add(id);
        return new Scope(()=> { Prepared=oldPrepared; NativeMirrorProbe.Context=previous; });
    }
    internal IDisposable Enter(NativeDeferredFields deferred)
    {
        var previous=Deferred; var scope=Enter(); Deferred=deferred;
        return new Scope(()=> { Deferred=previous; scope.Dispose(); });
    }
    internal void RetainCurrentProjection(NativeSnapshotIndex prepared)
    {
        // Refresh even unchanged nodes. Otherwise their Parent pointers pin old
        // complete snapshots until each individual item happens to change.
        foreach(var node in prepared.Nodes)
            if(Objects.ContainsKey(node.Key)) Data[node.Key]=node.Value;
    }
    private sealed class Scope : IDisposable
    { private Action restore; internal Scope(Action restore) { this.restore=restore; } public void Dispose() { restore?.Invoke(); restore=null; } }
    internal void Sweep(bool replica)
    {
        // A recreated host root can receive a new identity while this guest
        // reuses its native fixture renderer. Removing the old alias must not
        // destroy the same object retained under the new authoritative id.
        var retained=replica ? new HashSet<object>(Objects.Where(pair=>Seen.Contains(pair.Key)).Select(pair=>pair.Value)) : null;
        foreach (uint id in Objects.Keys.Where(id => !Seen.Contains(id)).ToArray())
        {
            if(replica && !retained.Contains(Objects[id])) DestroyReplicaObject(Objects[id]);
            Objects.Remove(id); Data.Remove(id);
        }
    }
    private static void DestroyReplicaObject(object value)
    {
        if(value is PixelWindow window)
        {
            if(window.handler?.gameObject!=null) window.Hide();
            WindowsHandler.current?.visibleWindows?.Remove(window);
            GraphHandler.windowGraphNodeRoots?.Remove(window);
        }
        if(value is PixelElement element && element.handler?.gameObject!=null)
            UnityEngine.Object.Destroy(element.handler.gameObject);
    }
    internal static void DestroyUntrackedReplica(GameItem item)
    {
        // Bootstrap fixtures were created before authoritative identities arrived.
        // Retire their render tree without running native Destroy business callbacks.
        foreach(var child in GraphUtils.FindAllChildrenType<GameItem>(item).ToArray())
        { DestroyReplicaObject(child.contentWindow); DestroyReplicaObject(child.examineWindow); DestroyReplicaObject(child); }
        DestroyReplicaObject(item.contentWindow); DestroyReplicaObject(item.examineWindow); DestroyReplicaObject(item);
    }
}

internal static partial class NativeMirrorProbe
{
    internal static NativeModelContext Context;
    private static JObject Annotate(object instance, JObject data)
    {
        if (Context == null) return data;
        uint id = Context.Id(instance); data["entity"] = id;
        Context.Objects[id] = instance; Context.Seen.Add(id); Context.Captured?.Invoke(instance, data);
        return data;
    }
    private static object FindExisting(JObject data) => Context != null && data["entity"] != null &&
        Context.Objects.TryGetValue((uint)data["entity"], out var value) ? value : null;
    internal static bool PropertyChanged(JObject data,string property)
    {
        if(Context==null || data["entity"]==null || FindExisting(data)==null) return true;
        uint id=(uint)data["entity"];
        if(Context.Prepared!=null) return Context.Prepared.PropertyChanged(id,property);
        return !Context.Data.TryGetValue(id,out var previous) ||
            !JToken.DeepEquals(previous[property],data[property]);
    }
    private static object FindUnchanged(JObject data)
    {
        if (Context == null || data["entity"] == null) return null;
        uint id = (uint)data["entity"];
        if(Context.Prepared!=null)
            return Context.Prepared.Unchanged.Contains(id) ? FindExisting(data) : null;
        foreach (var token in data.DescendantsAndSelf().OfType<JObject>())
            if (token["entity"] != null) Context.Seen.Add((uint)token["entity"]);
        return Context.Data.TryGetValue(id, out var previous) && JToken.DeepEquals(previous, data) ? FindExisting(data) : null;
    }
    private static void Remember(object value, JObject data)
    {
        if (Context == null || data["entity"] == null) return;
        uint id = (uint)data["entity"]; Context.Seen.Add(id);
        if (Context.Objects.TryGetValue(id, out var previous) && !ReferenceEquals(previous,value) && previous is PixelElement old)
            UnityEngine.Object.Destroy(old.handler.gameObject);
        // Standalone probes own detached nodes. Prepared runtime snapshots rotate
        // every cached node together in RetainCurrentProjection, so no unchanged
        // child keeps an older complete graph alive.
        Context.Objects[id]=value;
        Context.Data[id]=Context.Prepared==null ? (JObject)data.DeepClone() : data;
        var applied=Context.Applied;
        if(applied!=null) NativeReplicaGraph.Bind(()=>applied(value,data));
    }
    internal static void DetachReplica(GameItem item)
    {
        if (item.parentInventory is GameGridInventory grid)
            ((List<GameItem>)AccessTools.Field(typeof(GameGridInventory), "items").GetValue(grid)).Remove(item);
        if (item.parentInventory is GameSlotInventory slot && ReferenceEquals(slot.childItem,item))
        {
            NativeReplicaSlotPresentation.Release(slot);
            AccessTools.Field(typeof(GameSlotInventory), "currentItem").SetValue(slot, null);
            NativeReplicaGraph.Validate(slot);
        }
        if(item.parentInventory is GameGridScrollableInventory scroll)
            ((List<GameItem>)AccessTools.Field(typeof(GameGridScrollableInventory),"items").GetValue(scroll)).Remove(item);
        item.parentInventory=null;
    }
    internal static void AttachReplica(GameItemElement item, GameInventory target)
    {
        // Hydrate already validates changed item data. Reattaching an unchanged
        // item invalidates the native render graph despite no ownership move.
        if (ReferenceEquals(item.parentInventory,target))
        {
            if(target is GameSlotInventory existingSlot) NativeReplicaSlotPresentation.Refresh(existingSlot);
            return;
        }
        if (!ReferenceEquals(item.parentInventory,target))
        {
            DetachReplica(item);
            if (target is GameGridInventory grid) ((List<GameItem>)AccessTools.Field(typeof(GameGridInventory), "items").GetValue(grid)).Add(item);
            else if (target is GameSlotInventory slot) AccessTools.Field(typeof(GameSlotInventory), "currentItem").SetValue(slot,item);
            else if(target is GameGridScrollableInventory scroll) ((List<GameItem>)AccessTools.Field(typeof(GameGridScrollableInventory),"items").GetValue(scroll)).Add(item);
            else throw new NotSupportedException("Unknown replica inventory");
            item.parentInventory=target;
        }
        ((PixelElement)item).SetRenderingParent((PixelElement)target); NativeReplicaGraph.Validate(item);
        if(target is GameSlotInventory targetSlot) NativeReplicaSlotPresentation.Refresh(targetSlot);
    }
}
