using System;
using System.Collections.Generic;
using System.Linq;
using ProbablyCoop.Sync;

namespace ProbablyCoop.SyncTests;

internal static class InventoryLayoutTests
{
    internal static int Run()
    {
        try
        {
            Checkpoint();
            Race();
            RapidMoves();
            WholeItemCarriesLatestFacts();
            Dependencies();
            SplitAndMerge();
            RejectDependentSplit();
            NestedOwnership();
            ActionContext();
            Cursor();
            Console.WriteLine("PASS inventory-layout");
            return 0;
        }
        catch (InvalidOperationException error)
        {
            Console.WriteLine("FAIL inventory-layout: " + error.Message);
            return 1;
        }
    }

    private static void Checkpoint()
    {
        var items = new[]
        {
            new InventoryEntry(15, "crate", 1, new InventoryPlacement(new InventoryAddress(InventoryRoot.PartnerCarry, slot: 2), new GridPose(8, 3, 6))),
            new InventoryEntry(2, "罐头", 9, new InventoryPlacement(new InventoryAddress(InventoryRoot.Nested, 15, 2), new GridPose(1, 280, 3)),
                new byte[] { 4, 2, 8 }),
        };
        byte[] encoded = InventoryCheckpoint.Encode(items);
        Check(encoded.SequenceEqual(InventoryCheckpoint.Encode(items.Reverse())), "canonical inventory ordering");
        Check(InventoryCheckpoint.TryDecode(encoded, out var entries) && entries[0].Equals(items[1]) &&
            entries[1].Equals(items[0]), "checkpoint roundtrip preserves quantities, placement and facts");
        Check(!InventoryCheckpoint.TryDecode(encoded.Concat(new byte[] { 0 }).ToArray(), out _), "trailing input rejected");
        Check(!InventoryCheckpoint.TryDecode(encoded.Take(encoded.Length - 1).ToArray(), out _), "truncated input rejected");
        var bad = new SyncWriter();
        bad.Byte(3);
        bad.UInt((uint)InventoryCheckpoint.MaxItems + 1);
        Check(!InventoryCheckpoint.TryDecode(bad.ToArray(), out _), "item count bounded");
        Check(!InventoryPlacement.TryRead(new byte[] { 1, 2, 3, 8 }, out _), "invalid orientation rejected");
        Check(!InventoryPlacement.TryRead(new byte[] { 1, 2, 3, 4, 0 }, out _), "placement must be exact length");
        Check(InventoryPlacement.TryRead(new byte[] { (byte)InventoryRoot.ParkedCounter, 1, 1, 0 }, out var parked) &&
            parked.Address.Root == InventoryRoot.ParkedCounter, "parked merchant counter is preserved");
        var dossier = new InventoryPlacement(new InventoryAddress(InventoryRoot.Dossier), new GridPose(1, 1, 0));
        Check(InventoryPlacement.TryRead(dossier.Encode(), out var files) && files.Equals(dossier) &&
            !new InventoryAddress(InventoryRoot.Dossier, slot: 1).Valid, "dossier uses its fixed native grid");
        Check(!InventoryPlacement.TryRead(new byte[] { 14, 1, 1, 0 }, out _), "unknown root is rejected");
        var cassette = new InventoryPlacement(new InventoryAddress(InventoryRoot.Cassette, slot: 1), new GridPose(1, 1, 0));
        Check(InventoryPlacement.TryRead(cassette.Encode(), out var deck) && deck.Equals(cassette) &&
            !new InventoryAddress(InventoryRoot.Cassette, slot: 2).Valid, "only the two original cassette slots are accepted");
        Check(!InventoryPlacement.TryRead(new byte[] { (byte)InventoryRoot.PartnerCarry, 3, 1, 1, 0 }, out _),
            "unknown personal slot is rejected");
        Check(InventoryPlacement.TryRead(items[1].Placement.Encode(), out var decoded) && decoded.Equals(items[1].Placement),
            "nested placement roundtrip");
    }

    private static void Cursor()
    {
        byte[] data = new CursorPresence(uint.MaxValue, 100, ushort.MaxValue, true, true).Encode();
        Check(CursorPresence.TryRead(data, out var sample) && sample.Epoch == uint.MaxValue &&
            sample.X == 100 && sample.Y == ushort.MaxValue && sample.Shown && sample.Clicked, "cursor codec roundtrip");
        data[^1] = 2;
        Check(!CursorPresence.TryRead(data, out _) && !CursorPresence.TryRead(data.Take(8).ToArray(), out _),
            "invalid cursor state or length rejected");
    }

    private static void Race()
    {
        var layout = Seed();
        var showcase = At(10, InventoryRoot.Showcase);
        var counter = At(14, InventoryRoot.FrontCounter);
        Check(layout.RecordLocal(new[] { Put(2, showcase) }, 1) && layout.RecordLocal(new[] { Put(3, counter) }, 2),
            "local actions take effect before acknowledgement");
        Check(layout.Visible[2].Placement.Equals(showcase) && layout.Confirmed[2].Placement.Equals(At(4)), "prediction preserves authority");
        Check(Commit(layout, Put(1, showcase)), "winning multi-cell move accepted");
        Check(layout.Visible[1].Placement.Equals(showcase) && layout.Visible[2].Placement.Equals(At(4)) &&
            layout.Visible[3].Placement.Equals(counter), "only overlapping prediction rolls back");
        layout.Reject(1, _ => true);
        Check(Commit(layout, Put(3, counter), 2) && layout.Visible[3].Equals(layout.Confirmed[3]),
            "unrelated action still confirms");
        Check(!Commit(layout, Put(2, At(11, InventoryRoot.Showcase))) && layout.Confirmed[2].Placement.Equals(At(4)),
            "second cell of an item blocks occupancy in its own root");
        Check(Commit(layout, Put(2, At(11))), "same cell in another inventory is independent");
    }

    private static void RapidMoves()
    {
        var layout = Seed();
        var back = At(10, InventoryRoot.BackCounter);
        var showcase = At(2, InventoryRoot.Showcase);
        Check(layout.RecordLocal(new[] { Put(2, back) }, 1) && layout.RecordLocal(new[] { Put(2, showcase) }, 2),
            "the same item can move twice before the first response");
        Check(Commit(layout, Put(2, back), 1) && layout.Visible[2].Placement.Equals(showcase) &&
            layout.Confirmed[2].Placement.Equals(back), "first acknowledgement does not snap back a later move");
        Check(Commit(layout, Put(2, showcase), 2) && layout.Visible[2].Equals(layout.Confirmed[2]),
            "both moves confirmed in order");
        Check(layout.RecordLocal(new[] { Put(2, back) }, 3), "retry after the previous moves");
        Check(Commit(layout, Put(2, showcase)) && layout.Visible[2].Placement.Equals(back),
            "a repeated correction must not snap a valid new prediction back");
    }

    private static void WholeItemCarriesLatestFacts()
    {
        var layout = Seed();
        var initial = layout.Confirmed[2];
        var back = At(10, InventoryRoot.BackCounter);
        var showcase = At(2, InventoryRoot.Showcase);
        InventoryChange Move(InventoryPlacement placement, byte measurement) =>
            new InventoryChange(2, new InventoryEntry(2, initial.CatalogId, initial.Units, placement, new[] { measurement }));

        Check(Commit(layout, Move(initial.Placement, 10)), "initial live measurement");
        Check(layout.RecordLocal(new[] { Move(back, 10) }, 1, preservedSource: 2) &&
            layout.RecordLocal(new[] { Move(showcase, 10) }, 2, preservedSource: 2), "two plain relocations");
        Check(Commit(layout, Move(initial.Placement, 11)) && layout.Visible[2].Placement.Equals(showcase) &&
            layout.Visible[2].Facts[0] == 11, "concurrent facts follow the item without snapping its predicted placement");
        Check(Commit(layout, Move(back, 12), 1) && layout.Visible[2].Placement.Equals(showcase) &&
            layout.Visible[2].Facts[0] == 12, "canonical receipt carries its facts into the next pending move");
        Check(Commit(layout, Move(showcase, 12), 2) && layout.Visible[2].Equals(layout.Confirmed[2]),
            "whole-item moves converge to the authoritative version");

        Check(layout.RecordLocal(new[] { Move(back, 12) }, 3, preservedSource: 2) &&
            Commit(layout, Move(At(14), 13)) && layout.Visible[2].Placement.Equals(At(14)),
            "a competing relocation still cancels the losing prediction");
    }

    private static void NestedOwnership()
    {
        var bag = new InventoryAddress(InventoryRoot.Nested, 10);
        var box = new InventoryAddress(InventoryRoot.Nested, 20, 1);
        var items = new Dictionary<int, InventoryEntry>
        {
            [10] = Item(10, At(1, InventoryRoot.HostCarry)),
            [20] = Item(20, new InventoryPlacement(bag, new GridPose(0, 0, 0))),
        };
        Check(box.TryResolveRoot(30, items, out var root) && root == InventoryRoot.HostCarry, "nested owner inherits carry root");
        Check(!box.TryResolveRoot(10, items, out _), "cannot put a container into its descendant");
        var layout = new InventoryLayout(Fits);
        Check(layout.Load(items.Values, _ => true), "load nested");
        Check(!Commit(layout, new InventoryChange(10, null)), "cannot delete an owner leaving its children behind");
        Check(layout.Commit(new[] { new InventoryChange(10, null), new InventoryChange(20, null) }, 0, _ => true),
            "whole subtree can be deleted atomically");
        items.Remove(10);
        Check(!box.TryResolveRoot(30, items, out _), "unregistered container owner rejected");
    }

    private static void Dependencies()
    {
        var layout = Seed();
        Check(layout.RecordLocal(new[] { Put(2, At(10)) }, 1) && layout.RecordLocal(new[] { Put(3, At(4)) }, 2),
            "a later prediction can use an earlier free cell");
        layout.Reject(1, _ => true);
        Check(layout.Visible[2].Placement.Equals(At(4)) && layout.Visible[3].Placement.Equals(At(7)),
            "a dependent prediction returns to its confirmed cell");
        Check(Commit(layout, Put(2, At(12))) && layout.Visible[3].Placement.Equals(At(4)),
            "still-pending move is reevaluated when the cell frees");
        Check(Commit(layout, Put(3, At(4)), 2) && layout.Visible[3].Equals(layout.Confirmed[3]),
            "dependency finishes without reload");
    }

    private static void SplitAndMerge()
    {
        var layout = Seed();
        var split = new[] { Put(2, At(4), 5), Put(4, At(10), 3) };
        var merge = new[] { Put(2, At(4), 8), new InventoryChange(4, null) };
        Check(layout.RecordLocal(split, 1) && layout.RecordLocal(merge, 2), "split and immediate merge before ACK");
        Check(layout.Visible[2].Units == 8 && !layout.Visible.ContainsKey(4) && layout.Retains(4),
            "depleted source is retained while tentative");
        Check(layout.Commit(split, 1, _ => true) && layout.Confirmed[4].Units == 3 && !layout.Visible.ContainsKey(4),
            "split ACK keeps the later merge visible");
        Check(!layout.Commit(merge, 2, _ => false) && layout.Confirmed[4].Units == 3,
            "a failed native projection never advances the authoritative ledger");
        Check(layout.Commit(merge, 2, _ => true) && !layout.Retains(4) && layout.Confirmed[2].Units == 8,
            "merge ACK retires the exhausted stack");
        var forged = new[] { Put(2, At(4), 7), Put(5, At(7), 1) };
        Check(!layout.Commit(forged, 0, _ => true) && layout.Confirmed[2].Units == 8 && !layout.Retains(5),
            "an occupied split cell cannot leave half an edit");
    }

    private static void RejectDependentSplit()
    {
        var layout = Seed();
        Check(layout.RecordLocal(new[] { Put(2, At(4), 5), Put(4, At(10), 3) }, 1) &&
            layout.RecordLocal(new[] { Put(4, At(11), 3) }, 2) &&
            layout.RecordLocal(new[] { Put(3, At(15)) }, 3), "dependent and independent predictions");
        Check(layout.Reject(1, _ => true) && layout.Visible[2].Units == 8 && !layout.Visible.ContainsKey(4) &&
            layout.Visible[3].Placement.Equals(At(15)), "split rejection restores source and suppresses the dependent move");
        Check(layout.Reject(2, _ => true) && !layout.Retains(4), "rejected provisional object can be collected");
    }

    private static void ActionContext()
    {
        var intent = new InventoryAction(2, 0, 3, At(8, InventoryRoot.BackCounter));
        var tx = new SyncTx { Origin = 1, Serial = 1, Context = intent.Encode(), Ops = new List<SyncOp> { Put(2, At(4), 5).ToOp() } };
        Check(SyncCodec.TryDecode(SyncCodec.Tx(tx), out var frame) &&
            InventoryAction.TryRead(frame.Tx.Context, out var received) && received.Kind == InventoryActionKind.Transfer &&
            received.Amount == 3 && received.Destination.Equals(intent.Destination) &&
            InventoryChange.TryRead(frame.Tx.Ops[0], out var change) && change.After.Value.Units == 5, "effect transaction roundtrip");
        Check(!InventoryAction.TryRead(new InventoryAction(2, 2).Encode(), out _) &&
            !InventoryAction.TryRead(intent.Encode().Concat(new byte[] { 0 }).ToArray(), out _),
            "invalid action context rejected");
        var carrying = new InventoryAction(2, 0, 3, At(10), At(4));
        Check(InventoryAction.TryRead(carrying.Encode(), out received) &&
            received.UnchangedSource?.Equals(At(4)) == true &&
            !InventoryAction.TryRead(new InventoryAction(2, 5, 3, At(10), At(4)).Encode(), out _),
            "only a whole relocation carries the authoritative source facts");
    }

    private static InventoryLayout Seed()
    {
        var layout = new InventoryLayout(Fits);
        Check(layout.Load(new[] { Item(1, At(1)), Item(2, At(4), 8), Item(3, At(7)) }, _ => true), "seed valid inventory");
        return layout;
    }

    private static bool Commit(InventoryLayout layout, InventoryChange change, ulong ownSerial = 0) =>
        layout.Commit(new[] { change }, ownSerial, _ => true);

    private static InventoryChange Put(int id, InventoryPlacement at, int units = -1) =>
        new InventoryChange(id, Item(id, at, units));

    private static InventoryEntry Item(int id, InventoryPlacement at, int units = -1) =>
        new InventoryEntry(id, "stock", units < 0 ? id == 2 ? 8 : 1 : units, at);

    private static bool Fits(InventoryEntry entry, IReadOnlyDictionary<int, InventoryEntry> current)
    {
        var placement = entry.Placement;
        GridPose pose = placement.Pose;
        int width = entry.Id == 1 ? 2 : 1;
        if (pose.X + width > 20 || pose.Y >= 10) return false;
        foreach (var other in current.Values)
            if (other.Id != entry.Id && other.Placement.Address.Equals(placement.Address) && other.Placement.Pose.Y == pose.Y &&
                pose.X < other.Placement.Pose.X + (other.Id == 1 ? 2 : 1) && other.Placement.Pose.X < pose.X + width) return false;
        return true;
    }

    private static InventoryPlacement At(ushort x, InventoryRoot root = InventoryRoot.Main) =>
        new InventoryPlacement(new InventoryAddress(root), new GridPose(x, 1, 0));

    private static void Check(bool passed, string label)
    {
        if (!passed) throw new InvalidOperationException(label);
    }
}
