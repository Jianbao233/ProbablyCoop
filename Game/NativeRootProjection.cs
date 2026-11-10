using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using RootKey = ProbablyCoop.NativeActorInventory.RootKey;

namespace ProbablyCoop;

// Validates the ownership envelope before the Unity thread mutates any native
// object. The snapshot index intentionally permits equal aliases in a render
// graph; an item appearing twice as an owned root child is a different case.
internal sealed class NativeRootProjection
{
    internal const int NativePlaces = 23;
    internal const int MaximumRoots = NativeActorInventory.MaximumRoots;
    internal readonly Root[] Roots;

    internal sealed class Root
    {
        internal readonly RootKey Key;
        internal int Place => Key.Place;
        internal readonly uint Entity;
        internal readonly JObject Data;
        internal readonly Item[] Items;
        internal Root(RootKey key, uint entity, JObject data, Item[] items)
        { Key = key; Entity = entity; Data = data; Items = items; }
    }

    internal sealed class Item
    {
        internal readonly uint Entity, LegacyId;
        internal readonly JObject Data;
        internal Item(uint entity, uint legacyId, JObject data)
        { Entity = entity; LegacyId = legacyId; Data = data; }
    }

    private NativeRootProjection(Root[] roots) { Roots = roots; }

    internal static NativeRootProjection Read(JArray roots, NativeSnapshotIndex index)
    {
        if (roots == null || roots.Count > MaximumRoots || index == null)
            throw new InvalidDataException("Invalid native ownership envelope");
        var places = new HashSet<RootKey>();
        var inventoryIds = new HashSet<uint>();
        var itemIds = new HashSet<uint>();
        var legacyIds = new HashSet<uint>();
        var result = new Root[roots.Count];
        for (int i = 0; i < roots.Count; i++)
        {
            if (roots[i] is not JObject root || root["items"] is not JArray items ||
                root["inventoryState"] is not JObject state ||
                state["fields"] is not JObject || state["tags"] is not JObject)
                throw new InvalidDataException("Incomplete native inventory root");
            var key = ReadKey(root,index.ActorRoots);
            uint entity = Identity(root["entity"]);
            if (!places.Add(key) || !inventoryIds.Add(entity) || index.Nodes.ContainsKey(entity))
                throw new InvalidDataException("Duplicate or conflicting native root identity");
            if (root["shape"] is JObject)
            {
                if (!index.RootGrids.ContainsKey(key))
                    throw new InvalidDataException("Missing prepared native root geometry");
            }
            else if (root["shape"]?.Type != JTokenType.Null || index.RootGrids.ContainsKey(key))
                throw new InvalidDataException("Invalid native root geometry");

            // Slot mode is part of the inventory state, not the item renderer.
            // The runtime additionally checks the resolved native fixture type.
            if (state["steal"] != null || state["down"] != null || state["hidden"] != null)
            {
                if (state["steal"]?.Type != JTokenType.Boolean ||
                    state["down"]?.Type != JTokenType.Boolean ||
                    state["hidden"]?.Type != JTokenType.Boolean || items.Count > 1)
                    throw new InvalidDataException("Invalid native slot state");
            }
            if(index.ActorRoots && key.Personal && state["steal"]==null)
                throw new InvalidDataException("Actor carry root must be a native slot");
            var owned = new Item[items.Count];
            for (int j = 0; j < items.Count; j++)
            {
                if (items[j] is not JObject row)
                    throw new InvalidDataException("Invalid native owned item row");
                uint item = Identity(row["data"]), legacy = Identity(row["legacyId"]);
                if (!itemIds.Add(item) || !legacyIds.Add(legacy) ||
                    !index.Nodes.TryGetValue(item, out var node) || node["fields"] is not JObject fields ||
                    fields["<identifier>k__BackingField"]?.Type is not (JTokenType.String or JTokenType.Null))
                    throw new InvalidDataException("Invalid or multiply owned native root item");
                owned[j] = new Item(item, legacy, node);
            }
            result[i] = new Root(key, entity, root, owned);
        }
        if(index.ActorRoots)
            foreach(var actor in new[]{NativeActorInventory.Host,NativeActorInventory.Partner})
                foreach(var slot in new[]{NativeActorInventory.CarrySlot.LeftPocket,NativeActorInventory.CarrySlot.RightPocket,NativeActorInventory.CarrySlot.Backpack})
                    if(!places.Contains(RootKey.Carried(actor,slot)))
                        throw new InvalidDataException("Missing actor carry root");
        return new NativeRootProjection(result);
    }

    internal static RootKey ReadKey(JObject root,bool actors)
    {
        int place=NativeSnapshotIndex.RootGrid.ReadInteger(root["place"],1,NativePlaces);
        if(actors)
        {
            if(root["key"]?.Type!=JTokenType.String) throw new InvalidDataException("Missing actor-qualified root address");
            var key=RootKey.Parse((string)root["key"]);
            if(key.Place!=place) throw new InvalidDataException("Root address and native fixture differ");
            return key;
        }
        var legacy=RootKey.NativeFixture(place,NativeActorInventory.Host);
        if(root["key"]!=null && (root["key"].Type!=JTokenType.String || (string)root["key"]!=legacy.ToString()))
            throw new InvalidDataException("Actor root in a legacy projection");
        return legacy;
    }

    private static uint Identity(JToken value)
    {
        if (value?.Type != JTokenType.Integer || !uint.TryParse(value.ToString(), out uint id) || id == 0)
            throw new InvalidDataException("Invalid native projection identity");
        return id;
    }
}
