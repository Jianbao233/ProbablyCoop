using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace ProbablyCoop;

// Ownership-only, immutable view of one confirmed graph. IDs are session IDs
// from NativeModelContext, never GameItem.uniqueId or render/window identities.
// This component creates no native objects, performs no transfers, and owns no
// lease clock. Existing hand/proof adapters supply their live reservations.
internal sealed class NativeActorInventory
{
    internal enum ActorSlot : byte { Host = 0, Partner = 1 }
    internal const ActorSlot Host = ActorSlot.Host, Partner = ActorSlot.Partner;
    internal const int MaximumRoots = 26, MaximumEntities = 500001, MaximumOwnershipDepth = 64;
    internal const int MaximumHeldItems = 32;
    internal enum CarrySlot : byte { LeftPocket = 4, RightPocket = 5, Backpack = 8 }
    internal enum Decision { Allowed, InvalidActor, UnknownEntity, ForeignActorRoot, InvalidSources,
        InvalidDestination, ContainmentCycle, InvalidHands, StaleHand, HandConflict }

    internal readonly struct RootKey : IEquatable<RootKey>
    {
        internal readonly ActorSlot? Owner;
        internal readonly int Place;
        internal bool Personal => Owner.HasValue;
        private RootKey(ActorSlot? owner, int place) { Owner = owner; Place = place; }
        internal static RootKey Shared(int place)
        {
            if (place < 1 || place > 23 || IsCarry(place)) throw new ArgumentOutOfRangeException(nameof(place));
            return new RootKey(null, place);
        }
        internal static RootKey Carried(ActorSlot actor, CarrySlot slot)
        {
            RequireActor(actor);
            if (!IsCarry((int)slot)) throw new ArgumentOutOfRangeException(nameof(slot));
            return new RootKey(actor, (int)slot);
        }
        // Explicit local/operation actor is required even when resolving the
        // old native fixture numbers. Places 4/5/8 are never shared aliases.
        internal static RootKey NativeFixture(int place, ActorSlot actor)
        { RequireActor(actor); return IsCarry(place) ? Carried(actor, (CarrySlot)place) : Shared(place); }
        internal bool Valid => !Owner.HasValue ? Place >= 1 && Place <= 23 && !IsCarry(Place) :
            ValidActor(Owner.Value) && IsCarry(Place);
        public bool Equals(RootKey other) => Owner == other.Owner && Place == other.Place;
        public override bool Equals(object obj) => obj is RootKey other && Equals(other);
        public override int GetHashCode() => (Owner.HasValue ? (int)Owner.Value + 1 : 0) * 31 + Place;
        public override string ToString()
        {
            if (!Valid) throw new InvalidOperationException("Uninitialized actor inventory root");
            return !Owner.HasValue ? "world/" + Place.ToString(CultureInfo.InvariantCulture) :
                "actor/" + ((byte)Owner.Value).ToString(CultureInfo.InvariantCulture) + "/" +
                (Place == 4 ? "pocket-left" : Place == 5 ? "pocket-right" : "backpack");
        }
        internal static RootKey Parse(string text)
        {
            if (text == null) throw Invalid("Missing actor inventory root key");
            foreach (var actor in new[] { Host, Partner })
                foreach (var slot in CarrySlots)
                { var key = Carried(actor, slot); if (text == key.ToString()) return key; }
            if (text.StartsWith("world/", StringComparison.Ordinal) &&
                int.TryParse(text.Substring(6), NumberStyles.None, CultureInfo.InvariantCulture, out int place) &&
                place >= 1 && place <= 23 && !IsCarry(place))
            { var key = Shared(place); if (text == key.ToString()) return key; }
            throw Invalid("Unknown actor inventory root key");
        }
    }

    internal readonly struct RootBinding
    {
        internal readonly RootKey Key;
        internal readonly uint Inventory;
        internal readonly bool Slot;
        internal RootBinding(RootKey key, uint inventory, bool slot)
        { Key = key; Inventory = inventory; Slot = slot; }
    }
    internal readonly struct OwnedInventory
    {
        internal readonly uint Inventory, OwnerItem;
        internal readonly bool Slot;
        // OwnerItem is the actual container owner, after collapsing native
        // storage-only intermediate nodes. An examine/stolen UI alias is not
        // another owner. The native capture adapter must prove this relation.
        internal OwnedInventory(uint inventory, uint ownerItem, bool slot)
        { Inventory = inventory; OwnerItem = ownerItem; Slot = slot; }
    }
    internal readonly struct PlacedItem
    {
        internal readonly uint Item, Inventory;
        internal PlacedItem(uint item, uint inventory) { Item = item; Inventory = inventory; }
    }
    internal sealed class HandView
    {
        internal readonly ActorSlot Actor;
        internal readonly object Identity;
        private readonly uint[] items;
        internal IReadOnlyList<uint> Items { get; }
        // Identity is a trusted adapter's existing lease/gesture object, not a
        // network value or a new lock. This view adds no acquire/release state.
        internal HandView(ActorSlot actor, object identity, IEnumerable<uint> held)
        {
            Actor = actor; Identity = identity;
            if (held == null) throw new ArgumentNullException(nameof(held));
            items = held.Take(MaximumHeldItems + 1).ToArray(); Items = Array.AsReadOnly(items);
        }
    }
    private static readonly CarrySlot[] CarrySlots = { CarrySlot.LeftPocket, CarrySlot.RightPocket, CarrySlot.Backpack };
    private sealed class Node
    {
        internal readonly bool Inventory, Slot;
        internal readonly uint Parent;
        internal RootKey Root;
        internal bool RootKnown;
        internal int Children, Depth;
        internal Node(bool inventory, bool slot, uint parent) { Inventory = inventory; Slot = slot; Parent = parent; }
    }
    private readonly Dictionary<RootKey, uint> roots = new Dictionary<RootKey, uint>();
    private readonly Dictionary<uint, Node> nodes = new Dictionary<uint, Node>();
    internal IReadOnlyList<RootBinding> Roots { get; private set; }
    internal int PlacedItemCount { get; private set; }
    private NativeActorInventory() { }

    internal static NativeActorInventory Build(IEnumerable<RootBinding> rootBindings,
        IEnumerable<OwnedInventory> ownedInventories, IEnumerable<PlacedItem> placedItems)
    {
        if (rootBindings == null || ownedInventories == null || placedItems == null) throw new ArgumentNullException();
        var graph = new NativeActorInventory();
        var rootRows = new List<RootBinding>();
        foreach (var root in rootBindings)
        {
            if (!root.Key.Valid || root.Key.Personal && !root.Slot || rootRows.Count >= MaximumRoots ||
                graph.roots.ContainsKey(root.Key)) throw Invalid("Invalid or duplicate actor inventory root");
            graph.Add(root.Inventory, new Node(true, root.Slot, 0) { Root = root.Key, RootKnown = true });
            graph.roots.Add(root.Key, root.Inventory); rootRows.Add(root);
        }
        // Empty pockets are explicit roots too. Omission must not silently
        // resolve a guest slot to the host fixture at the same legacy place.
        foreach (var actor in new[] { Host, Partner })
            foreach (var slot in CarrySlots)
                if (!graph.roots.ContainsKey(RootKey.Carried(actor, slot))) throw Invalid("Missing actor carry root");
        foreach (var inventory in ownedInventories)
        {
            if (inventory.OwnerItem == 0) throw Invalid("Nested inventory has no owner item");
            graph.Add(inventory.Inventory, new Node(true, inventory.Slot, inventory.OwnerItem));
        }
        foreach (var item in placedItems)
        {
            if (item.Inventory == 0) throw Invalid("Owned item is detached from its inventory");
            graph.Add(item.Item, new Node(false, false, item.Inventory));
            graph.PlacedItemCount++;
        }
        foreach (var pair in graph.nodes)
        {
            var node = pair.Value;
            if (node.Parent == 0) continue;
            if (!graph.nodes.TryGetValue(node.Parent, out var parent) || node.Inventory == parent.Inventory)
                throw Invalid("Missing or incorrectly typed native ownership parent");
            if (!node.Inventory && ++parent.Children > 1 && parent.Slot) throw Invalid("Native slot owns multiple items");
        }
        foreach (uint id in graph.nodes.Keys) graph.ResolveRoot(id);
        graph.Roots = rootRows.AsReadOnly();
        return graph;
    }
    private void Add(uint id, Node node)
    {
        if (id == 0 || nodes.Count >= MaximumEntities || nodes.ContainsKey(id))
            throw Invalid("Duplicate, zero or excessive native ownership entity");
        nodes.Add(id, node);
    }
    private void ResolveRoot(uint id)
    {
        var path = new List<uint>(); var visited = new HashSet<uint>(); uint cursor = id;
        while (!nodes[cursor].RootKnown)
        {
            if (path.Count >= MaximumOwnershipDepth || !visited.Add(cursor)) throw Invalid("Cyclic or excessive native ownership depth");
            path.Add(cursor); cursor = nodes[cursor].Parent;
        }
        var ancestor = nodes[cursor];
        if (path.Count + ancestor.Depth > MaximumOwnershipDepth) throw Invalid("Excessive native ownership depth");
        int depth = ancestor.Depth;
        for (int i = path.Count - 1; i >= 0; i--)
        { var member = nodes[path[i]]; member.Root = ancestor.Root; member.RootKnown = true; member.Depth = ++depth; }
    }
    internal uint RootEntity(RootKey key) => roots.TryGetValue(key, out uint id) ? id : throw Invalid("Unbound native ownership root");
    internal bool TryRoot(uint entity, out RootKey root)
    {
        if (nodes.TryGetValue(entity, out var node)) { root = node.Root; return true; }
        root = default; return false;
    }
    internal uint Parent(uint entity) => nodes.TryGetValue(entity, out var node) ? node.Parent : throw Invalid("Unknown ownership entity");
    private bool IsAncestor(uint ancestor, uint descendant)
    {
        while (descendant != 0)
        { if (ancestor == descendant) return true; descendant = nodes[descendant].Parent; }
        return false;
    }
    private bool Related(uint first, uint second) => IsAncestor(first, second) || IsAncestor(second, first);
    private bool Accessible(ActorSlot actor, uint entity) => !nodes[entity].Root.Owner.HasValue || nodes[entity].Root.Owner == actor;

    // Permission/conflict preflight only. Native MayRemove, slot acceptance,
    // quantity, shape, economy and MoveProof checks must still run on commit.
    // target may be an item, or an inventory for a whole-content operation
    // such as sorting. A destination alone does not lock its sibling items.
    internal Decision CheckOperation(ActorSlot actor, IEnumerable<uint> sources, uint destination,
        uint target, IEnumerable<HandView> liveHands, object consumingHand = null)
    {
        if (!ValidActor(actor)) return Decision.InvalidActor;
        if (sources == null || liveHands == null) return Decision.InvalidSources;
        var sourceItems = sources.Take(MaximumHeldItems + 1).ToArray();
        if (sourceItems.Length > MaximumHeldItems || sourceItems.Distinct().Count() != sourceItems.Length ||
            sourceItems.Length == 0 && target == 0) return Decision.InvalidSources;
        foreach (uint item in sourceItems)
        {
            if (!nodes.TryGetValue(item, out var node)) return Decision.UnknownEntity;
            if (node.Inventory) return Decision.InvalidSources;
            if (!Accessible(actor, item)) return Decision.ForeignActorRoot;
        }
        for (int i = 0; i < sourceItems.Length; i++)
            for (int j = i + 1; j < sourceItems.Length; j++)
                if (Related(sourceItems[i], sourceItems[j])) return Decision.InvalidSources;
        if (destination != 0)
        {
            if (!nodes.TryGetValue(destination, out var node) || !node.Inventory) return Decision.InvalidDestination;
            if (!Accessible(actor, destination)) return Decision.ForeignActorRoot;
            foreach (uint item in sourceItems) if (IsAncestor(item, destination)) return Decision.ContainmentCycle;
        }
        if (target != 0)
        {
            if (!nodes.ContainsKey(target)) return Decision.UnknownEntity;
            if (!Accessible(actor, target)) return Decision.ForeignActorRoot;
        }
        var hands = liveHands.Take(3).ToArray();
        var validity = CheckHands(hands);
        if (validity != Decision.Allowed) return validity;
        if (consumingHand != null)
        {
            var hand = hands.SingleOrDefault(candidate => candidate.Actor == actor && ReferenceEquals(candidate.Identity, consumingHand));
            if (hand == null || sourceItems.Length != hand.Items.Count || sourceItems.Any(item => !hand.Items.Contains(item)))
                return Decision.StaleHand;
        }
        foreach (var hand in hands)
        {
            if (hand.Actor == actor && ReferenceEquals(hand.Identity, consumingHand)) continue;
            foreach (uint held in hand.Items)
            {
                if (sourceItems.Any(item => Related(held, item)) || target != 0 && Related(held, target) ||
                    destination != 0 && IsAncestor(held, destination)) return Decision.HandConflict;
            }
        }
        return Decision.Allowed;
    }
    internal Decision CheckHands(IEnumerable<HandView> liveHands)
    {
        if (liveHands == null) return Decision.InvalidHands;
        var hands = liveHands.Take(3).ToArray();
        if (hands.Length > 2) return Decision.InvalidHands;
        var actors = new HashSet<ActorSlot>(); var items = new List<uint>();
        foreach (var hand in hands)
        {
            if (hand == null || !ValidActor(hand.Actor) || !actors.Add(hand.Actor) || hand.Identity == null ||
                hand.Items.Count == 0 || hand.Items.Count > MaximumHeldItems || hand.Items.Distinct().Count() != hand.Items.Count)
                return Decision.InvalidHands;
            foreach (uint item in hand.Items)
            {
                if (!nodes.TryGetValue(item, out var node) || node.Inventory || !Accessible(hand.Actor, item)) return Decision.StaleHand;
                foreach (uint earlier in items) if (Related(item, earlier)) return Decision.HandConflict;
                items.Add(item);
            }
        }
        return Decision.Allowed;
    }
    // Model/root-key actors are fixed slots 0/1, like NativeActorRun.Slot.
    // The OLD request protocol's actor numbers 1/2 are translated only at this
    // boundary; readiness masks must not be an internal actor argument.
    internal static ActorSlot FromProtocolActor(byte actor) => actor switch
    { 1 => Host, 2 => Partner, _ => throw new ArgumentOutOfRangeException(nameof(actor)) };
    internal static byte ToProtocolActor(ActorSlot actor)
    { RequireActor(actor); return actor == Host ? (byte)1 : (byte)2; }
    private static bool ValidActor(ActorSlot actor) => actor == Host || actor == Partner;
    private static void RequireActor(ActorSlot actor)
    { if (!ValidActor(actor)) throw new ArgumentOutOfRangeException(nameof(actor)); }
    private static bool IsCarry(int place) => place is 4 or 5 or 8;
    private static InvalidDataException Invalid(string message) => new InvalidDataException(message);
}
