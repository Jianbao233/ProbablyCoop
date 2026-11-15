using System;
using System.Collections.Generic;
using System.Linq;
using ProbablyCoop.Net;
using ProbablyCoop.Sync;
using UnityEngine;

namespace ProbablyCoop;

// Original objects remain in the local scene. The ledger records accepted facts
// and speculative changes separately, including objects removed by a merge.
internal sealed class NativeInventoryGrid : ISyncWorld, ISyncWholeItemMove
{
    private const int HostIdStart = EffectItemIds.HostStart, GuestIdStart = EffectItemIds.GuestStart, GuestIdEnd = EffectItemIds.End;
    private static readonly Vector2 Anywhere = new Vector2(float.PositiveInfinity, float.PositiveInfinity);

    private readonly bool host;
    private readonly InventoryLayout layout;
    private readonly Dictionary<InventoryAddress, GameInventory> grids = new Dictionary<InventoryAddress, GameInventory>();
    private readonly Dictionary<GameInventory, InventoryAddress> addresses = new Dictionary<GameInventory, InventoryAddress>();
    private readonly Dictionary<int, GameItem> items = new Dictionary<int, GameItem>();
    private readonly Dictionary<int, byte[]> renderedFacts = new Dictionary<int, byte[]>();
    private readonly Dictionary<int, (byte[] encoded, NativeInventoryFacts value)> capturedFacts =
        new Dictionary<int, (byte[], NativeInventoryFacts)>();
    private NativeActorCarry carry;
    private bool ownsCarry;
    private bool ready;
    private bool retirementPending;
    private int lastLocalId;
    private int lastGuestId = GuestIdStart - 1;

    internal NativeInventoryGrid(bool host)
    {
        this.host = host;
        lastLocalId = (host ? HostIdStart : GuestIdStart) - 1;
        layout = new InventoryLayout(Fits);
    }

    internal bool Bound => grids.Count != 0;
    internal bool Ready => ready;
    internal bool Applying { get; private set; }
    internal GameInventory Grid(InventoryAddress address) => grids[address];

    internal NativeActorCarry Carry => carry;
    internal int GuestWatermark => lastGuestId;

    internal void RestoreIdentityWatermarks(PlayerStore store)
    {
        if (store.modData.TryGetValue(EffectItemIds.SaveKey, out string data))
        {
            if (!EffectItemIds.TryRead(data, out var saved))
                throw new InvalidOperationException("Invalid cooperative item identity watermarks.");
            lastLocalId = Math.Max(lastLocalId, saved.Host);
            lastGuestId = Math.Max(lastGuestId, saved.Guest);
        }
        ObserveSavedIdentities(store);
    }

    internal void StoreIdentityWatermarks(PlayerStore store)
    {
        ObserveSavedIdentities(store);
        store.modData[EffectItemIds.SaveKey] = new EffectItemIds(lastLocalId, lastGuestId).Encode();
    }

    internal void SeedGuestAllocator(int id) => lastLocalId = Math.Max(lastLocalId, id);

    private void ObserveSavedIdentities(PlayerStore store)
    {
        var shop = EmporiumEntry.Instance;
        var all = shop.GetAllItems().Concat(carry.GetAllCarriedItems())
            .Concat(GraphUtils.FindAllChildrenType<GameItem>(shop.soldElement))
            .Concat(GraphUtils.FindAllChildrenType<GameItem>(shop.hirelingInv))
            .Concat(GraphUtils.FindAllChildrenType<GameItem>(shop.cassettePlayerElement));
        foreach (int id in all.Select(item => item.uniqueId).Concat(store.soldItemsTracker.Select(sold => sold.itemUniqueId)))
        {
            if (IsAllocated(id, SyncHost.HostPeer)) lastLocalId = Math.Max(lastLocalId, id);
            else if (IsAllocated(id, NetSession.GuestPeer)) lastGuestId = Math.Max(lastGuestId, id);
            else if (id > 0 && id < HostIdStart) store.currentUniqueId = Math.Max(store.currentUniqueId, id);
        }
        if (store.currentUniqueId >= HostIdStart)
            throw new InvalidOperationException("The native item ID allocator crossed the cooperative ranges.");
    }

    internal void Bind(EmporiumEntry shop, NativeActorCarry actorCarry = null)
    {
        Bind(new InventoryAddress(InventoryRoot.Main), shop.invElement);
        Bind(new InventoryAddress(InventoryRoot.FrontCounter), shop.frontInvinvElement);
        Bind(new InventoryAddress(InventoryRoot.BackCounter), shop.backInvinvElement);
        Bind(new InventoryAddress(InventoryRoot.ParkedCounter), shop.backInvinvElementCounter);
        Bind(new InventoryAddress(InventoryRoot.Vending), shop.vendingMachineElement);
        Bind(new InventoryAddress(InventoryRoot.Fountain), shop.vendingFountainElement);
        Bind(new InventoryAddress(InventoryRoot.HiddenStock), shop.hiddenElement);
        if (shop.dossier?.contentWindow?.child is GameInventory dossier)
            Bind(new InventoryAddress(InventoryRoot.Dossier), dossier);
        if (shop.cassettePlayerElement.childItem is GameItem cassette)
        {
            // The fixed player stays native on both machines. Only the
            // physical tapes belong to the shared inventory.
            Bind(new InventoryAddress(InventoryRoot.Cassette), MachineHelper.GetSlot(cassette, 0));
            Bind(new InventoryAddress(InventoryRoot.Cassette, slot: 1), MachineHelper.GetSlot(cassette, 1));
        }
        Bind(new InventoryAddress(InventoryRoot.Showcase), shop.showcaseElement);
        Bind(new InventoryAddress(InventoryRoot.SceneGround), shop.afterhourInventory);
        var localActor = host ? NativeActorRun.Slot.Host : NativeActorRun.Slot.Partner;
        ownsCarry = actorCarry == null;
        carry = actorCarry ?? new NativeActorCarry(shop, () => localActor, localActor);
        BindCarry(InventoryRoot.HostCarry, NativeActorRun.Slot.Host);
        BindCarry(InventoryRoot.PartnerCarry, NativeActorRun.Slot.Partner);
    }

    private void BindCarry(InventoryRoot root, NativeActorRun.Slot actor)
    {
        Bind(new InventoryAddress(root, slot: 0), carry.Get(actor, NativeActorInventory.CarrySlot.LeftPocket));
        Bind(new InventoryAddress(root, slot: 1), carry.Get(actor, NativeActorInventory.CarrySlot.RightPocket));
        Bind(new InventoryAddress(root, slot: 2), carry.Get(actor, NativeActorInventory.CarrySlot.Backpack));
    }

    private void Bind(InventoryAddress address, GameInventory inventory)
    {
        grids.Add(address, inventory);
        addresses.Add(inventory, address);
    }

    internal void Reset()
    {
        if (ownsCarry) carry?.DisposeForSceneUnload();
        carry = null;
        ownsCarry = retirementPending = false;
        ready = false;
        items.Clear();
        grids.Clear();
        addresses.Clear();
        renderedFacts.Clear();
        capturedFacts.Clear();
        layout.Load(Array.Empty<InventoryEntry>(), _ => true);
    }

    internal byte[] Capture() => Ready ? InventoryCheckpoint.Encode(layout.Confirmed.Values) : null;

    // Native world transitions can create, consume or relocate stock outside
    // a player's gesture. Enroll the settled native graph at that boundary.
    internal bool AdoptHostInventory()
    {
        if (!ReadHostInventory(out var entries) || !layout.Load(entries, _ => true)) return false;
        ready = true;
        Prune();
        return true;
    }

    internal bool CaptureHostChanges(out InventoryChange[] changes)
    {
        changes = null;
        if (!Ready || !ReadHostInventory(out var entries)) return false;
        var current = entries.ToDictionary(row => row.Id);
        var updates = entries.Where(row => !layout.Confirmed.TryGetValue(row.Id, out var prior) || !prior.Equals(row))
            .Select(row => new InventoryChange(row.Id, row));
        var removed = layout.Confirmed.Keys.Where(id => !current.ContainsKey(id)).Select(id => new InventoryChange(id, null));
        changes = updates.Concat(removed).OrderBy(change => change.Id).ToArray();
        return true;
    }

    private bool ReadHostInventory(out List<InventoryEntry> entries)
    {
        entries = new List<InventoryEntry>();
        if (!host || !Bound || Applying) return false;
        var seen = new HashSet<int>();
        foreach (var root in grids.Where(pair => pair.Key.Root != InventoryRoot.Nested).ToArray())
            if (!ReadNative(root.Value, entries, seen)) return false;
        return true;
    }

    private bool ReadNative(GameInventory inventory, List<InventoryEntry> entries, HashSet<int> seen)
    {
        foreach (var item in inventory.childItems)
        {
            if (entries.Count >= InventoryCheckpoint.MaxItems || item?.parentInventory != inventory) return false;
            if (!Tracks(item))
            {
                int id = item.uniqueId;
                if (id <= 0 || id >= GuestIdEnd || items.ContainsKey(id))
                {
                    if (lastLocalId >= GuestIdStart - 1) return false;
                    item.uniqueId = ++lastLocalId;
                }
                else if (IsAllocated(id, SyncHost.HostPeer)) lastLocalId = Math.Max(lastLocalId, id);
                else if (IsAllocated(id, NetSession.GuestPeer)) lastGuestId = Math.Max(lastGuestId, id);
                if (!Register(item, adoptContents: true)) return false;
            }
            if (!seen.Add(item.uniqueId) || !TryPlacement(inventory, item.modifiedShape, out var place)) return false;
            if (!TryCaptureNativeFacts(item, out byte[] facts))
            {
                Debug.LogWarning($"[probably_coop] cannot represent original {item.identifier} ({item.uniqueId})");
                return false;
            }
            renderedFacts[item.uniqueId] = facts;
            entries.Add(new InventoryEntry(item.uniqueId, item.identifier, item.unitCount, place, facts));
            if (item.contentWindow != null || item.examineWindow != null)
                for (int slot = 0; slot <= byte.MaxValue && grids.TryGetValue(
                    new InventoryAddress(InventoryRoot.Nested, item.uniqueId, (byte)slot), out var child); slot++)
                    if (!ReadNative(child, entries, seen)) return false;
        }
        return true;
    }

    private bool TryCaptureNativeFacts(GameItem item, out byte[] data)
    {
        int id = item.uniqueId;
        if (capturedFacts.TryGetValue(id, out var prior) && prior.value.MatchesNative(item))
        {
            data = prior.encoded;
            return true;
        }
        data = NativeInventoryFacts.Capture(item);
        if (!NativeInventoryFacts.TryDecode(data, out var facts)) return false;
        capturedFacts[id] = (data, facts);
        return true;
    }

    internal bool Load(InventoryEntry[] entries)
    {
        if (!Bound || entries == null || entries.Length > InventoryCheckpoint.MaxItems) return false;
        var materialized = new InventoryEntry[entries.Length];
        var created = new List<int>();
        for (int i = 0; i < entries.Length; i++)
        {
            var row = entries[i];
            if (!TryMaterialize(row, created, out var item))
            {
                Debug.LogWarning($"[probably_coop] inventory checkpoint factory refused {row.Id} ({row.CatalogId})");
                return ForgetCreated(created);
            }
            byte[] facts = row.Facts.Length == 0 && host && !Ready ? NativeInventoryFacts.Capture(item) : row.Facts;
            if (!NativeInventoryFacts.TryDecode(facts, out _))
            {
                Debug.LogWarning($"[probably_coop] inventory checkpoint facts refused {row.Id} ({row.CatalogId})");
                return ForgetCreated(created);
            }
            materialized[i] = new InventoryEntry(row.Id, row.CatalogId, row.Units, row.Placement, facts);
        }
        if (!layout.Load(materialized, Project))
        {
            var graph = materialized.GroupBy(row => row.Id).ToDictionary(group => group.Key, group => group.First());
            var invalid = materialized.Where(row => !row.Placement.Address.TryResolveRoot(row.Id, graph, out _) ||
                !Fits(row, graph)).Select(row =>
                {
                    var address = row.Placement.Address;
                    var owner = graph.TryGetValue(address.OwnerItem, out var parent) ? parent.CatalogId : address.Root.ToString();
                    return row.Id + "/" + row.CatalogId + "@" + owner + "/" + address.Slot;
                });
            Debug.LogWarning("[probably_coop] inventory checkpoint placement or projection refused. Invalid rows: " +
                string.Join(", ", invalid));
            return ForgetCreated(created);
        }
        ready = true;
        foreach (var row in materialized)
        {
            if (IsAllocated(row.Id, host ? SyncHost.HostPeer : NetSession.GuestPeer)) lastLocalId = Math.Max(lastLocalId, row.Id);
            if (IsAllocated(row.Id, NetSession.GuestPeer)) lastGuestId = Math.Max(lastGuestId, row.Id);
        }
        Prune();
        return true;
    }

    internal bool Tracks(GameItem item) => item != null && items.TryGetValue(item.uniqueId, out var tracked) &&
        ReferenceEquals(tracked, item);
    internal bool Contains(GameItem item) => Tracks(item) && item.parentInventory != null && addresses.ContainsKey(item.parentInventory);
    internal bool TryFind(int id, out GameItem item) => items.TryGetValue(id, out item) && item.parentInventory != null;
    internal bool IsBound(GameInventory inventory) => inventory != null && addresses.ContainsKey(inventory);
    internal bool Retains(GameItem item) => Tracks(item) && layout.Retains(item.uniqueId);

    internal bool TrackSpawn(GameItem item)
    {
        if (Tracks(item)) return true;
        if (!IsBound(item.parentInventory) || lastLocalId >= (host ? GuestIdStart : GuestIdEnd) - 1) return false;
        int next = ++lastLocalId;
        item.uniqueId = next;
        return Register(item);
    }

    internal bool TryPlacement(GameInventory inventory, GridShape shape, out InventoryPlacement placement)
    {
        placement = default;
        if (inventory == null || !addresses.TryGetValue(inventory, out var address) || !TryPose(shape, out var pose)) return false;
        placement = new InventoryPlacement(address, pose);
        return true;
    }

    internal bool CanInteract(GameItem item) => Contains(item) && layout.Visible.TryGetValue(item.uniqueId, out var row) &&
        Accessible(row, layout.Visible, host ? SyncHost.HostPeer : NetSession.GuestPeer);

    internal bool TryFindAccessible(int id, byte actor, out GameItem item) =>
        items.TryGetValue(id, out item) && layout.Confirmed.TryGetValue(id, out var row) &&
        Contains(item) && Accessible(row, layout.Confirmed, actor);

    internal bool TryFindControl(PixelElement control, out GameItem owner, out int index)
    {
        var node = control.rectTransform;
        foreach (var item in items.Values)
        {
            var window = item.contentWindow;
            if (window?.rectTransform == null || !node.IsChildOf(window.rectTransform)) continue;
            index = NativeMachineControls.Read(item).IndexOf(control);
            if (index < 0) continue;
            owner = item;
            return true;
        }
        owner = null;
        index = -1;
        return false;
    }

    internal ulong ControlProof(GameItem owner)
    {
        var state = layout.Confirmed;
        if (!state.TryGetValue(owner.uniqueId, out var row)) return 0;
        var proofData = new SyncWriter();
        void WriteOccupancy(InventoryEntry entry)
        {
            proofData.UInt((uint)entry.Id);
            proofData.UInt((uint)entry.Units);
            entry.Placement.Write(proofData);
        }
        WriteOccupancy(row);
        // A machine operation depends on its control settings and on which
        // items occupy its slots. Ambient temperature and other ticking
        // measurements do not invalidate an unchanged operator selection.
        if (!NativeInventoryFacts.TryDecode(row.Facts, out var facts)) return 0;
        facts.WriteControlProof(proofData);
        foreach (var child in GraphUtils.FindAllChildrenType<GameItem>(owner).OrderBy(item => item.uniqueId))
            if (state.TryGetValue(child.uniqueId, out var known)) WriteOccupancy(known);
        ulong proof = 14695981039346656037UL;
        foreach (byte value in proofData.ToArray()) proof = unchecked((proof ^ value) * 1099511628211UL);
        return proof == 0 ? 1 : proof;
    }

    internal void RestoreControlView(GameItem owner)
    {
        if (layout.Visible.TryGetValue(owner.uniqueId, out var row) &&
            NativeInventoryFacts.TryDecode(row.Facts, out var facts))
            facts.ApplyControls(owner);
    }

    internal bool CanDestination(GameItem item, InventoryPlacement placement) =>
        placement.Address.TryResolveRoot(item.uniqueId, layout.Visible, out var root) &&
        CanAccess(root, host ? SyncHost.HostPeer : NetSession.GuestPeer);

    internal bool TryObserve(IEnumerable<GameItem> touched, InventoryAction action, out InventoryChange[] changes)
    {
        changes = null;
        var observed = new List<InventoryChange>();
        var dependencies = new List<InventoryChange>();
        foreach (var item in touched)
        {
            if (!Tracks(item)) continue;
            int id = item.uniqueId;
            bool existed = layout.Visible.TryGetValue(id, out var before);
            if (item.parentInventory == null || item.unitCount <= 0)
            {
                if (existed) observed.Add(new InventoryChange(id, null));
                continue;
            }
            if (!TryPlacement(item.parentInventory, item.modifiedShape, out var placement))
            {
                Debug.LogWarning($"[probably_coop] original action placed {id}/{item.identifier} outside the enrolled inventory");
                return false;
            }
            byte[] facts = NativeInventoryFacts.Capture(item);
            renderedFacts[id] = facts;
            var after = new InventoryEntry(id, item.identifier, item.unitCount, placement, facts);
            if (!after.Valid || !NativeInventoryFacts.TryDecode(facts, out _))
            {
                Debug.LogWarning($"[probably_coop] original action has unsupported facts for {id}/{item.identifier}");
                return false;
            }
            if (!existed || !after.Equals(before)) observed.Add(new InventoryChange(id, after));
            else if (action.Kind == InventoryActionKind.Target && (id == action.Source || id == action.Target) ||
                     action.Kind == InventoryActionKind.Transfer && id == action.Destination.Address.OwnerItem)
                dependencies.Add(new InventoryChange(id, after));
        }
        if (observed.Count == 0) { changes = Array.Empty<InventoryChange>(); return true; }
        observed.AddRange(dependencies);
        changes = observed.OrderBy(change => change.Id).ToArray();
        if (layout.CanPredict(changes)) return true;
        Debug.LogWarning($"[probably_coop] original {action.Kind} for {action.Source}/{action.Target} does not fit the inventory ledger");
        return false;
    }

    internal InventoryAction DescribeMove(InventoryAction action, InventoryChange[] changes)
    {
        if (action.Kind != InventoryActionKind.Transfer || action.Target != 0 ||
            !layout.Visible.TryGetValue(action.Source, out var source) || source.Units != action.Amount ||
            source.Placement.Equals(action.Destination)) return action;
        foreach (var change in changes)
            if (change.Id == source.Id && change.After is InventoryEntry moved &&
                moved.Units == source.Units && source.Facts.AsSpan().SequenceEqual(moved.Facts))
                return new InventoryAction(action.Source, 0, action.Amount, action.Destination, source.Placement);
        return action;
    }

    internal bool RecordLocal(InventoryChange[] changes, ulong serial = 0, int preservedSource = 0)
    {
        if (!layout.RecordLocal(changes, serial, preservedSource)) return false;
        retirementPending |= serial == 0 && changes.Any(change => !change.After.HasValue);
        return true;
    }

    internal void CollectRetiredItems()
    {
        if (retirementPending) Prune();
    }

    internal bool RestoreVisible()
    {
        // A failed local operation may already have written a tag.
        renderedFacts.Clear();
        if (!Project(layout.Visible)) return false;
        Prune();
        return true;
    }

    public bool TryApply(SyncTx tx) => Commit(tx, 0);
    public bool TryConfirm(SyncTx tx) => Commit(tx, tx.Serial);

    public void Revert(SyncTx tx)
    {
        if (layout.Reject(tx.Serial, Project)) Prune();
        else throw new InvalidOperationException("The native inventory could not roll back the rejected action.");
    }

    public byte[] Read(EntityKey key) => IsItemKey(key) &&
        layout.Confirmed.TryGetValue((int)key.Id, out var row) ? row.Encode() : null;

    public bool CanHold(byte actor, EntityKey key) => IsItemKey(key) &&
        layout.Confirmed.TryGetValue((int)key.Id, out var row) && Accessible(row, layout.Confirmed, actor);

    public bool CanCarryUpdatedState(SyncTx tx, SyncOp op) =>
        host && tx.Origin == NetSession.GuestPeer && op.Kind == SyncOpKind.Put &&
        InventoryAction.TryRead(tx.Context, out var action) && op.Key.Equals(EntityKey.Item(action.Source)) &&
        SourceStillInPlace(action, out _);

    private bool SourceStillInPlace(InventoryAction action, out InventoryEntry source)
    {
        source = default;
        return action.UnchangedSource.HasValue && layout.Confirmed.TryGetValue(action.Source, out source) &&
            source.Units == action.Amount && source.Placement.Equals(action.UnchangedSource.Value);
    }

    private bool Commit(SyncTx tx, ulong ownSerial)
    {
        if (!Ready || tx.Ops.Count == 0 || tx.Ops.Count > InventoryLayout.MaxChanges) return false;
        var changes = new InventoryChange[tx.Ops.Count];
        for (int i = 0; i < changes.Length; i++)
            if (!InventoryChange.TryRead(tx.Ops[i], out changes[i])) return false;

        int guestId = lastGuestId;
        int canonicalSource = -1;
        if (host)
        {
            if (tx.Origin != NetSession.GuestPeer || !InventoryAction.TryRead(tx.Context, out var action))
                return RefusedInventory("native intent");
            if (action.UnchangedSource.HasValue)
            {
                canonicalSource = Array.FindIndex(changes, change => change.Id == action.Source);
                if (canonicalSource < 0 || changes[canonicalSource].After is not InventoryEntry proposed ||
                    !SourceStillInPlace(action, out var source) || !Current(source, out var original) ||
                    !TryCaptureNativeFacts(original, out var facts)) return RefusedInventory("source of whole-item move");
                // The request changed placement only. Keep the exact latest
                // native state, including ticks received since the user picked it up.
                changes[canonicalSource] = new InventoryChange(source.Id, proposed.WithFacts(facts));
            }
            if (!ValidateGuest(action, changes, ref guestId)) return RefusedInventory("native effects");
        }

        var created = new List<int>();
        foreach (var change in changes)
            if (change.After.HasValue &&
                (!NativeInventoryFacts.TryDecode(change.After.Value.Facts, out _) ||
                 !TryMaterialize(change.After.Value, created, out _))) return ForgetCreated(created);
        if (!layout.Commit(changes, ownSerial, Project))
        {
            RefusedInventory("placement ledger");
            return ForgetCreated(created);
        }
        lastGuestId = guestId;
        if (canonicalSource >= 0) tx.Ops[canonicalSource].Data = changes[canonicalSource].After.Value.Encode();
        Prune();
        return true;
    }

    private bool ValidateGuest(InventoryAction action, InventoryChange[] changes, ref int guestId)
    {
        var state = layout.Confirmed;
        if (!state.TryGetValue(action.Source, out var source) || !Accessible(source, state, NetSession.GuestPeer) ||
            !Current(source, out var nativeSource)) return RefusedInventory("source no longer matches its slot");
        var writes = new Dictionary<int, InventoryChange>();
        foreach (var change in changes)
            if (!writes.TryAdd(change.Id, change)) return false;

        if (action.Kind == InventoryActionKind.Target)
        {
            if (!state.TryGetValue(action.Target, out var target) || !Accessible(target, state, NetSession.GuestPeer) ||
                !Current(target, out var nativeTarget) || !nativeSource.MayTarget(nativeTarget) ||
                !nativeSource.CanTarget(nativeTarget) || !writes.ContainsKey(source.Id) || !writes.ContainsKey(target.Id))
                return false;
            return changes.All(change => ValidateTargetChange(change, source.Id, target.Id, state));
        }

        if (action.Amount > source.Units || action.Amount > nativeSource.MaxNumRemove() ||
            !writes.TryGetValue(source.Id, out var sourceWrite) ||
            !action.Destination.Address.TryResolveRoot(source.Id, state, out var root) ||
            !CanAccess(root, NetSession.GuestPeer)) return RefusedInventory("transfer ownership, count or source access");

        int splitId = 0;
        if (action.Target != 0)
        {
            if (!state.TryGetValue(action.Target, out var target) || !Current(target, out var nativeTarget) ||
                !Accessible(target, state, NetSession.GuestPeer) || !target.Placement.Equals(action.Destination) ||
                !writes.TryGetValue(target.Id, out var targetWrite) ||
                !Matches(targetWrite, target, target.Units + action.Amount, target.Placement) ||
                !Matches(sourceWrite, source, source.Units - action.Amount, source.Placement) ||
                (nativeTarget.GetStackSlot(nativeSource)?.numTransfer ?? 0) < action.Amount) return false;
        }
        else
        {
            if (!grids.TryGetValue(action.Destination.Address, out var destination)) return false;
            var marker = destination.TryInventorySlot(nativeSource, action.Amount, Anywhere,
                Shape(nativeSource, action.Destination.Pose), nativeSource.modifiedState);
            if (marker == null || marker.numTransfer < action.Amount || marker.targetItem != null ||
                !TryPlacement(destination, marker.itemGridShape, out var actual) || !actual.Equals(action.Destination))
                return RefusedInventory("native destination slot");

            if (action.Amount == source.Units)
            {
                if (!Matches(sourceWrite, source, source.Units, action.Destination))
                    return RefusedInventory("whole-item transfer changed ownership or shape");
            }
            else
            {
                if (!Matches(sourceWrite, source, source.Units - action.Amount, source.Placement)) return false;
                foreach (var change in changes)
                {
                    if (state.ContainsKey(change.Id)) continue;
                    if (splitId != 0 || !IsAllocated(change.Id, NetSession.GuestPeer) || change.Id <= guestId ||
                        !Matches(change, source, action.Amount, action.Destination)) return false;
                    splitId = change.Id;
                }
                if (splitId == 0) return false;
                guestId = splitId;
            }
        }

        foreach (var change in changes)
        {
            if (change.Id == source.Id || change.Id == action.Target || change.Id == splitId) continue;
            if (!state.TryGetValue(change.Id, out var row) ||
                (!source.Placement.Address.IsInside(change.Id, state) &&
                 !action.Destination.Address.IsInside(change.Id, state)) ||
                !Matches(change, row, row.Units, row.Placement))
                return RefusedInventory($"transfer side effect on item {change.Id} outside of the source or destination");
        }
        return true;
    }

    private static bool RefusedInventory(string reason)
    {
#if PROBABLY_COOP_GRID_QA
        Debug.Log($"[probably_coop GRID] refused inventory effect: {reason}");
#endif
        return false;
    }

    private static bool ValidateTargetChange(InventoryChange change, int source, int target,
        IReadOnlyDictionary<int, InventoryEntry> state)
    {
        if (!state.TryGetValue(change.Id, out var before) || !Accessible(before, state, NetSession.GuestPeer)) return false;
        bool tool = change.Id == source;
        if (!tool && change.Id != target && !before.Placement.Address.IsInside(source, state) &&
            !before.Placement.Address.IsInside(target, state)) return false;
        if (!change.After.HasValue) return tool;
        int units = change.After.Value.Units;
        return (tool ? units <= before.Units : units == before.Units) &&
            Matches(change, before, units, before.Placement);
    }

    private static bool Matches(InventoryChange change, InventoryEntry before, int units, InventoryPlacement placement)
    {
        if (units == 0) return !change.After.HasValue;
        if (!change.After.HasValue) return false;
        var after = change.After.Value;
        return after.Units == units && after.CatalogId == before.CatalogId && after.Placement.Equals(placement) &&
            NativeInventoryFacts.TryDecode(before.Facts, out var prior) &&
            NativeInventoryFacts.TryDecode(after.Facts, out var next) && prior.Owned == next.Owned;
    }

    private bool Current(InventoryEntry row, out GameItem item) => items.TryGetValue(row.Id, out item) &&
        item.unitCount == row.Units && TryPlacement(item.parentInventory, item.modifiedShape, out var actual) &&
        actual.Equals(row.Placement);

    private static bool Accessible(InventoryEntry row, IReadOnlyDictionary<int, InventoryEntry> entries, byte actor) =>
        row.Placement.Address.TryResolveRoot(row.Id, entries, out var root) && CanAccess(root, actor);

    private static bool CanAccess(InventoryRoot root, byte actor) => root switch
    {
        InventoryRoot.HostCarry => actor == SyncHost.HostPeer,
        InventoryRoot.PartnerCarry => actor == NetSession.GuestPeer,
        InventoryRoot.ParkedCounter => false,
        _ => true,
    };

    private static bool IsAllocated(int id, byte actor) => actor == SyncHost.HostPeer
        ? id >= HostIdStart && id < GuestIdStart : id >= GuestIdStart && id < GuestIdEnd;

    private static bool IsItemKey(EntityKey key) => key.Space == SyncSpace.Item && key.Id > 0 && key.Id <= int.MaxValue;

    private bool TryMaterialize(InventoryEntry row, List<int> created, out GameItem item)
    {
        if (items.TryGetValue(row.Id, out item)) return item.identifier == row.CatalogId;
        if (!row.Valid || !DirectoryMaster.Has<GameItem>(row.CatalogId)) return false;
        item = DirectoryMaster.Item(row.CatalogId);
        if (item == null) return false;
        item.uniqueId = row.Id;
        if (row.Facts.Length != 0)
        {
            if (!NativeInventoryFacts.TryDecode(row.Facts, out var facts)) { item.Destroy(); return false; }
            facts.Apply(item);
            item.modifiedShape = item.shape.Clone();
            item.modifiedState = item.state.Clone();
            StorageExpansionHelper.RestoreTabs(item);
            if (item.GetTagReadonly("MODULE_UPGRADE_STAGE_INT").GetInt() > 0)
                MachineHelper.LoadUpgrade(item);
        }
        if (!Register(item)) { item.Destroy(); return false; }
        if (row.Facts.Length != 0) renderedFacts[row.Id] = row.Facts;
        created.Add(row.Id);
        return true;
    }

    private bool Register(GameItem item, bool adoptContents = false)
    {
        var children = item.contentWindow == null && item.examineWindow == null
            ? new List<GameInventory>() : GraphUtils.FindChildrenType<GameInventory>(item);
        if (children.Count > byte.MaxValue + 1 ||
            children.Any(child => !NativeInventoryMembership.Supported(child) || !adoptContents && child.childItems.Count != 0 ||
                                  addresses.ContainsKey(child)) || !items.TryAdd(item.uniqueId, item)) return false;
        for (int index = 0; index < children.Count; index++)
            Bind(new InventoryAddress(InventoryRoot.Nested, item.uniqueId, (byte)index), children[index]);
        return true;
    }

    private bool ForgetCreated(List<int> created)
    {
        foreach (int id in created) DestroyUnretained(id);
        return false;
    }

    private void Prune()
    {
        retirementPending = false;
        foreach (int id in items.Keys.ToArray())
            if (!layout.Retains(id)) DestroyUnretained(id);
    }

    private void DestroyUnretained(int id)
    {
        if (!items.TryGetValue(id, out var item)) return;
        items.Remove(id);
        renderedFacts.Remove(id);
        capturedFacts.Remove(id);
        foreach (var address in grids.Keys.Where(key => key.Root == InventoryRoot.Nested && key.OwnerItem == id).ToArray())
        {
            addresses.Remove(grids[address]);
            grids.Remove(address);
        }
        // Original transactions can transfer stock into an unenrolled native
        // container (for example the sold-item ledger). The host still owns it.
        if (!host || item.parentInventory == null) item.Destroy();
    }

    private static bool TryPose(GridShape shape, out GridPose pose)
    {
        pose = default;
        if (shape == null || shape.minX < 0 || shape.minX > ushort.MaxValue ||
            shape.minY < 0 || shape.minY > ushort.MaxValue || shape.orientation < 0 || shape.orientation > 3) return false;
        pose = new GridPose((ushort)shape.minX, (ushort)shape.minY, (byte)(shape.orientation | (shape.flipped ? 4 : 0)));
        return true;
    }

    private bool Fits(InventoryEntry entry, IReadOnlyDictionary<int, InventoryEntry> placements)
    {
        var placement = entry.Placement;
        if (!grids.TryGetValue(placement.Address, out var inventory) || !items.TryGetValue(entry.Id, out var item)) return false;
        GridShape shape = PlannedShape(entry, item);
        if (inventory is GameGridInventory grid &&
            !shape.FullyIntersects(grid.inventoryShape, (cell, target) => cell != 1 || target == 0)) return false;
        if (inventory is GameGridScrollableInventory scroll &&
            (shape.minX < 0 || shape.minY < 0 || shape.maxX >= scroll.width || shape.maxY >= scroll.height)) return false;
        foreach (var other in placements.Values)
        {
            if (other.Id == entry.Id || !other.Placement.Address.Equals(placement.Address)) continue;
            if (inventory is GameSlotInventory) return false;
            GameItem current = items[other.Id];
            GridShape occupied = PlannedShape(other, current);
            if (shape.Intersects(occupied)) return false;
        }
        return true;
    }

    private static GridShape Shape(GameItem item, GridPose pose) => new GridShapeBuilder(item.modifiedShape)
        .SetTransform(pose.X, pose.Y, (pose.Transform & 4) != 0, pose.Transform & 3);

    private GridShape PlannedShape(InventoryEntry entry, GameItem item)
    {
        var current = item.modifiedShape;
        if ((!renderedFacts.TryGetValue(entry.Id, out var known) || !known.AsSpan().SequenceEqual(entry.Facts)) &&
            NativeInventoryFacts.TryDecode(entry.Facts, out var facts))
            current = facts.Footprint(item);
        var pose = entry.Placement.Pose;
        return TryPose(current, out var existing) && existing.Equals(pose) ? current :
            new GridShapeBuilder(current).SetTransform(pose.X, pose.Y, (pose.Transform & 4) != 0, pose.Transform & 3);
    }

    private sealed class Projection
    {
        internal readonly GameItem Item;
        internal readonly GameInventory Source, Destination;
        internal readonly GridShape BeforeShape, AfterShape;
        internal readonly int BeforeUnits, AfterUnits;
        internal readonly NativeInventoryFacts BeforeFacts, AfterFacts;

        internal Projection(GameItem item, GameInventory destination, GridShape shape, int units,
            NativeInventoryFacts beforeFacts, NativeInventoryFacts afterFacts)
        {
            Item = item;
            Source = item.parentInventory;
            Destination = destination;
            BeforeShape = item.modifiedShape;
            AfterShape = shape;
            BeforeUnits = item.unitCount;
            AfterUnits = units;
            BeforeFacts = beforeFacts;
            AfterFacts = afterFacts;
        }

        internal bool ChangesInventory => Source != Destination;
    }

    private bool Project(IReadOnlyDictionary<int, InventoryEntry> entries)
    {
        var changes = new List<Projection>();
        foreach (var pair in items)
        {
            GameItem item = pair.Value;
            if (!entries.TryGetValue(pair.Key, out var row))
            {
                if (item.parentInventory != null)
                    changes.Add(new Projection(item, null, item.modifiedShape, item.unitCount, null, null));
                continue;
            }
            bool positioned = TryPlacement(item.parentInventory, item.modifiedShape, out var current) &&
                              current.Equals(row.Placement);
            bool hasFacts = renderedFacts.TryGetValue(pair.Key, out var facts) && facts.AsSpan().SequenceEqual(row.Facts);
            if (positioned && item.unitCount == row.Units && hasFacts) continue;
            if (!NativeInventoryFacts.TryDecode(row.Facts, out var afterFacts) ||
                !NativeInventoryFacts.TryDecode(NativeInventoryFacts.Capture(item), out var beforeFacts)) return false;
            changes.Add(new Projection(item, grids[row.Placement.Address], PlannedShape(row, item),
                row.Units, beforeFacts, afterFacts));
        }

        Applying = true;
        try
        {
            if (!Apply(changes)) return false;
            foreach (var change in changes)
            {
                int id = change.Item.uniqueId;
                if (entries.TryGetValue(id, out var row)) renderedFacts[id] = row.Facts;
                else renderedFacts.Remove(id);
            }
            return true;
        }
        finally { Applying = false; }
    }

    private bool Apply(List<Projection> changes)
    {
        foreach (var change in changes)
            if (change.Source != null && !IsBound(change.Source) ||
                change.Item is GameItemElement element && element.IsDestroyed()) return false;
        foreach (var change in changes)
            if (change.ChangesInventory && change.Source != null && !NativeInventoryMembership.Remove(change.Source, change.Item))
                return Restore(changes);
        foreach (var change in changes)
        {
            change.Item.modifiedShape = change.AfterShape;
            change.Item.unitCount = change.AfterUnits;
            if (change.ChangesInventory && change.Destination != null &&
                !NativeInventoryMembership.Add(change.Destination, change.Item)) return Restore(changes);
        }
        foreach (var change in changes) change.AfterFacts?.Apply(change.Item);
        Refresh(changes);
        return true;
    }

    private static bool Restore(List<Projection> changes)
    {
        foreach (var change in changes)
            if (change.Item.parentInventory != null && change.Item.parentInventory != change.Source &&
                !NativeInventoryMembership.Remove(change.Item.parentInventory, change.Item))
                throw new InvalidOperationException("Could not remove a partially projected item.");
        foreach (var change in changes)
        {
            change.Item.modifiedShape = change.BeforeShape;
            change.Item.unitCount = change.BeforeUnits;
            if (change.Item.parentInventory == null && change.Source != null &&
                !NativeInventoryMembership.Add(change.Source, change.Item))
                throw new InvalidOperationException("Could not restore the original native inventory.");
            change.BeforeFacts?.Apply(change.Item);
        }
        Refresh(changes);
        return false;
    }

    private static void Refresh(List<Projection> changes)
    {
        if (changes.Count == 0) return;
        foreach (var change in changes)
            if (change.Item.parentInventory != null) change.Item.ValidateShapeState();
        var dossier = EmporiumEntry.Instance?.dossier;
        if (dossier?.contentWindow?.child is GameGridInventory files && changes.Any(change =>
            change.ChangesInventory && (change.Source == files || change.Destination == files)))
        {
            // Membership applies without native inventory callbacks. Rebuild
            // the dossier's appearance from its local contents.
            SpriteHelper.UpdateDossierSprite(files, dossier);
            dossier.Validate();
        }
        var cassette = EmporiumEntry.Instance?.cassettePlayer;
        var tapes = NativeLocalCassette.PlayingInventory(cassette);
        if (tapes != null && changes.Any(change => change.Source == tapes || change.Destination == tapes))
            NativeLocalCassette.RefreshPlaylist(cassette);
        OverlayHandler.current?.UpdateInventoryStateUI();
    }
}
