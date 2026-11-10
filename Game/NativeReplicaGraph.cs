using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace ProbablyCoop;

// A Unity-thread publication boundary, not a second game simulation. Native
// constructors/setters may lay out their own subtree while it is being built.
// Our explicit invalidations and frontend bindings wait for complete ownership.
internal sealed class NativeReplicaGraph : IDisposable
{
    private static NativeReplicaGraph current;
    private readonly HashSet<PixelElement> dirty = new HashSet<PixelElement>();
    private readonly HashSet<GameSlotInventory> slots = new HashSet<GameSlotInventory>();
    private readonly HashSet<PixelElement> sizeChanges = new HashSet<PixelElement>();
    private readonly List<Action> bindings = new List<Action>();
    private readonly List<Action> windowLayouts = new List<Action>();
    private bool flushing;
    internal bool Readable { get; private set; }

    internal NativeReplicaGraph()
    {
        if(current!=null) throw new InvalidOperationException("Nested native graph publication");
        current=this;
    }

    internal static void Validate(PixelElement element)
    {
        if(element==null) return;
        if(current==null || current.flushing) { element.Validate(); return; }
        current.dirty.Add(element);
        // A reused item's shape can change without changing slot identity.
        if(element is GameItem item && item.parentInventory is GameSlotInventory slot)
            current.dirty.Add(slot);
    }

    internal static void NotifySizeChanged(PixelElement element)
    {
        if(current==null || current.flushing) MachineryContainerUtils.NotifySizeChanged(element);
        else current.sizeChanges.Add(element);
    }

    internal static bool DeferSlot(GameSlotInventory slot)
    {
        if(current==null || current.flushing) return false;
        current.slots.Add(slot);
        return true;
    }

    internal static void Bind(Action bind)
    {
        if(current==null || current.flushing) bind();
        else current.bindings.Add(bind);
    }

    internal static void WindowLayout(Action apply)
    {
        if(current==null || current.flushing) apply();
        else current.windowLayouts.Add(apply);
    }

    // Existing identities cannot change constructor-only attributes. Refusing
    // this before any live assignment is safer than writing readonly fields or
    // quietly rebuilding an actor's open window with stale parent references.
    internal static void ValidateWindowIdentities(NativeSnapshotIndex index,NativeModelContext model)
    {
        foreach(var pair in index.Nodes)
        {
            if(!model.Objects.TryGetValue(pair.Key,out var value) || value is not PixelWindow window) continue;
            JObject data=pair.Value;
            if(data["element"]==null || data["move"]?.Type!=JTokenType.Boolean ||
                data["style"]?.Type!=JTokenType.Integer || (bool)data["move"]!=window.canMove ||
                (int)data["style"]!=(int)window.style)
                throw new InvalidDataException("Native window identity changed immutable layout");
        }
    }

    internal void Flush()
    {
        if(flushing) throw new InvalidOperationException("Reentrant native graph layout");
        flushing=true;
        try
        {
            // Repair stale stolen-UI references before native Validate follows
            // them. Constructors/setters keep their normal local validation.
            foreach(var slot in slots)
                if(slot.handler?.gameObject!=null) NativeReplicaSlotPresentation.Refresh(slot);
            slots.Clear();
            foreach(var apply in windowLayouts) apply();
            windowLayouts.Clear();
            // Changed children first; do not Validate every retained entity.
            foreach(var element in dirty.OrderByDescending(RenderDepth).ToArray())
                if(element.handler?.gameObject!=null) element.Validate();
            dirty.Clear();
            foreach(var element in sizeChanges)
                if(element.handler?.gameObject!=null) MachineryContainerUtils.NotifySizeChanged(element);
            sizeChanges.Clear();
            // ApplyExtra binds local controls/tooltips. Keep its prepared-index
            // context alive, but never invoke it on a partly linked item graph.
            foreach(var bind in bindings) bind();
            bindings.Clear();
        }
        finally { flushing=false; }
    }

    private static int RenderDepth(PixelElement element)
    {
        int depth=0;
        for(var parent=element.handler?.rectTransform?.parent;parent!=null;parent=parent.parent) depth++;
        return depth;
    }

    internal void AllowReads() { Readable=true; }
    internal void EndBatch()
    {
        Flush();
        // Local prediction reconciliation follows the confirmed publication.
        // Its Attach/Detach must immediately update source/destination slots,
        // rather than leave invalidations queued after the last graph flush.
        if(ReferenceEquals(current,this)) current=null;
    }
    public void Dispose()
    {
        Readable=false;
        if(ReferenceEquals(current,this)) current=null;
        dirty.Clear(); slots.Clear(); sizeChanges.Clear(); bindings.Clear(); windowLayouts.Clear();
    }
}
