using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ProbablyCoop.Sync;

internal readonly struct GridPose : IEquatable<GridPose>
{
    internal readonly ushort X, Y;
    internal readonly byte Transform;

    internal GridPose(ushort x, ushort y, byte transform) { X = x; Y = y; Transform = transform; }

    public bool Equals(GridPose other) => X == other.X && Y == other.Y && Transform == other.Transform;
    public override bool Equals(object other) => other is GridPose pose && Equals(pose);
    public override int GetHashCode() => (X * 397 ^ Y) * 397 ^ Transform;
}

internal readonly struct InventoryEntry : IEquatable<InventoryEntry>
{
    internal const int MaxCatalogBytes = 128, MaxUnits = 10000, MaxFactsBytes = 24 * 1024;

    internal readonly int Id, Units;
    internal readonly string CatalogId;
    internal readonly InventoryPlacement Placement;
    internal readonly byte[] Facts;

    internal InventoryEntry(int id, string catalogId, int units, InventoryPlacement placement, byte[] facts = null)
    {
        Id = id;
        CatalogId = catalogId;
        Units = units;
        Placement = placement;
        Facts = facts ?? Array.Empty<byte>();
    }

    internal bool Valid => Id > 0 && Units > 0 && Units <= MaxUnits && Placement.Valid &&
        !string.IsNullOrWhiteSpace(CatalogId) && Encoding.UTF8.GetByteCount(CatalogId) <= MaxCatalogBytes &&
        !CatalogId.Any(char.IsControl) && Facts != null && Facts.Length <= MaxFactsBytes;

    internal byte[] Encode()
    {
        var writer = new SyncWriter();
        Write(writer);
        return writer.ToArray();
    }

    internal InventoryEntry WithFacts(byte[] facts) => new InventoryEntry(Id, CatalogId, Units, Placement, facts);

    internal bool SameOccupancy(InventoryEntry other) => Id == other.Id && Units == other.Units &&
        string.Equals(CatalogId, other.CatalogId, StringComparison.Ordinal) && Placement.Equals(other.Placement);

    internal void Write(SyncWriter writer)
    {
        writer.Text(CatalogId);
        writer.UInt((uint)Units);
        Placement.Write(writer);
        writer.Bytes(Facts);
    }

    internal static InventoryEntry Read(int id, SyncReader reader)
    {
        string catalog = reader.Text(MaxCatalogBytes);
        uint units = reader.UInt();
        var placement = InventoryPlacement.Read(reader);
        return new InventoryEntry(id, catalog, unchecked((int)units), placement, reader.Bytes(MaxFactsBytes));
    }

    public bool Equals(InventoryEntry other) => Id == other.Id && Units == other.Units &&
        string.Equals(CatalogId, other.CatalogId, StringComparison.Ordinal) && Placement.Equals(other.Placement) &&
        Facts.AsSpan().SequenceEqual(other.Facts);
    public override bool Equals(object other) => other is InventoryEntry entry && Equals(entry);
    public override int GetHashCode() => Id;
}

internal readonly struct InventoryChange
{
    internal readonly int Id;
    internal readonly InventoryEntry? After;

    internal InventoryChange(int id, InventoryEntry? after)
    {
        Id = id;
        After = after;
    }

    internal SyncOp ToOp() => After.HasValue ? SyncOp.Put(EntityKey.Item(Id), After.Value.Encode()) :
        SyncOp.Remove(EntityKey.Item(Id));

    internal static bool TryRead(SyncOp op, out InventoryChange change)
    {
        change = default;
        if (op == null || op.Key.Space != SyncSpace.Item || op.Key.Id <= 0 || op.Key.Id > int.MaxValue) return false;
        int id = (int)op.Key.Id;
        if (op.Kind == SyncOpKind.Remove)
        {
            change = new InventoryChange(id, null);
            return true;
        }
        if (op.Kind != SyncOpKind.Put || op.Data == null ||
            op.Data.Length > InventoryEntry.MaxFactsBytes + InventoryEntry.MaxCatalogBytes + 32) return false;
        var reader = new SyncReader(op.Data);
        var entry = InventoryEntry.Read(id, reader);
        if (!reader.Ok || !reader.AtEnd || !entry.Valid) return false;
        change = new InventoryChange(id, entry);
        return true;
    }
}

// Checkpoints use the same entries as commits. Only joining and realignment
// serialize the entire ledger. Facts are bounded data interpreted by the game adapter.
internal static class InventoryCheckpoint
{
    internal const int MaxItems = 4096;
    internal const int MaxBytes = 4 * 1024 * 1024;
    private const byte Version = 3;

    internal static byte[] Encode(IEnumerable<InventoryEntry> items)
    {
        var sorted = items.OrderBy(item => item.Id).ToArray();
        if (sorted.Length > MaxItems) throw new ArgumentOutOfRangeException(nameof(items));
        var writer = new SyncWriter();
        writer.Byte(Version);
        writer.UInt((uint)sorted.Length);
        int last = 0;
        foreach (var item in sorted)
        {
            if (item.Id <= last || !item.Valid) throw new ArgumentException("Invalid inventory entry.", nameof(items));
            last = item.Id;
            writer.UInt((uint)item.Id);
            item.Write(writer);
            if (writer.Length > MaxBytes) throw new ArgumentException("Inventory checkpoint exceeds size limit.", nameof(items));
        }
        if (!ValidTopology(sorted)) throw new ArgumentException("Invalid inventory ownership.", nameof(items));
        return writer.ToArray();
    }

    internal static bool TryDecode(byte[] data, out InventoryEntry[] entries)
    {
        entries = null;
        if (data == null || data.Length > MaxBytes) return false;
        var reader = new SyncReader(data);
        if (reader.Byte() != Version) return false;
        uint count = reader.UInt();
        if (!reader.Ok || count > MaxItems) return false;
        var items = new InventoryEntry[count];
        int last = 0;
        for (int i = 0; i < items.Length; i++)
        {
            int id = unchecked((int)reader.UInt());
            var item = InventoryEntry.Read(id, reader);
            if (!reader.Ok || id <= last || !item.Valid) return false;
            items[i] = item;
            last = id;
        }
        if (!reader.AtEnd || !ValidTopology(items)) return false;
        entries = items;
        return true;
    }

    private static bool ValidTopology(InventoryEntry[] items)
    {
        var graph = items.ToDictionary(item => item.Id);
        return items.All(item => item.Placement.Address.TryResolveRoot(item.Id, graph, out _));
    }
}

// An action may change several native objects. A prediction has a before-image
// for each one, so rebasing cannot apply half a split or resurrect an old stack.
internal sealed class InventoryLayout
{
    internal const int MaxChanges = 96;
    internal delegate bool CanPlace(InventoryEntry entry, IReadOnlyDictionary<int, InventoryEntry> current);

    private sealed class Prediction
    {
        internal readonly ulong Serial;
        internal readonly InventoryChange[] Changes, Before;
        internal readonly int PreservedSource;

        internal Prediction(ulong serial, InventoryChange[] changes, IReadOnlyDictionary<int, InventoryEntry> basis,
            int preservedSource)
        {
            Serial = serial;
            Changes = changes;
            PreservedSource = preservedSource;
            Before = changes.Select(change => new InventoryChange(change.Id, Find(basis, change.Id))).ToArray();
        }

        private Prediction(Prediction previous, byte[] facts)
        {
            Serial = previous.Serial;
            PreservedSource = previous.PreservedSource;
            Changes = ReplaceSourceFacts(previous.Changes, PreservedSource, facts);
            Before = ReplaceSourceFacts(previous.Before, PreservedSource, facts);
        }

        private static InventoryChange[] ReplaceSourceFacts(InventoryChange[] changes, int source, byte[] facts) =>
            changes.Select(change => change.Id == source && change.After.HasValue
                ? new InventoryChange(source, change.After.Value.WithFacts(facts)) : change).ToArray();

        internal Prediction WithUpdatedSource(byte[] facts) => new Prediction(this, facts);

        internal bool DependsOn(int id) => Before.Any(entry => entry.Id == id);
        internal bool Matches(IReadOnlyDictionary<int, InventoryEntry> basis) =>
            Before.All(entry => Nullable.Equals(entry.After, Find(basis, entry.Id)));
    }

    private readonly CanPlace fits;
    private Dictionary<int, InventoryEntry> confirmed = new Dictionary<int, InventoryEntry>();
    private Dictionary<int, InventoryEntry> visible = new Dictionary<int, InventoryEntry>();
    private List<Prediction> pending = new List<Prediction>();

    internal InventoryLayout(CanPlace fits) => this.fits = fits;

    internal IReadOnlyDictionary<int, InventoryEntry> Confirmed => confirmed;
    internal IReadOnlyDictionary<int, InventoryEntry> Visible => visible;

    internal bool Load(IEnumerable<InventoryEntry> entries, Func<IReadOnlyDictionary<int, InventoryEntry>, bool> project)
    {
        var next = new Dictionary<int, InventoryEntry>();
        foreach (var entry in entries)
            if (!entry.Valid || !next.TryAdd(entry.Id, entry) || next.Count > InventoryCheckpoint.MaxItems) return false;
        if (!next.Values.All(entry => entry.Placement.Address.TryResolveRoot(entry.Id, next, out _) && fits(entry, next)) ||
            !project(next)) return false;
        confirmed = visible = next;
        pending.Clear();
        return true;
    }

    internal bool CanPredict(InventoryChange[] changes) => Stage(visible, changes, out _);

    internal bool RecordLocal(InventoryChange[] changes, ulong serial = 0, int preservedSource = 0)
    {
        if (serial == 0 && pending.Count != 0 ||
            serial != 0 && pending.Count != 0 && serial <= pending[pending.Count - 1].Serial ||
            !Stage(visible, changes, out var next)) return false;
        if (serial != 0) pending.Add(new Prediction(serial, changes, visible, preservedSource));
        else confirmed = next;
        visible = next;
        return true;
    }

    internal bool Commit(InventoryChange[] changes, ulong ownSerial,
        Func<IReadOnlyDictionary<int, InventoryEntry>, bool> project)
    {
        if (!Stage(confirmed, changes, out var nextConfirmed)) return false;
        var nextPending = new List<Prediction>(pending);
        if (ownSerial != 0)
        {
            var accepted = nextPending.FirstOrDefault(action => action.Serial == ownSerial);
            nextPending.RemoveAll(action => action.Serial == ownSerial);
            if (accepted?.PreservedSource > 0 && nextConfirmed.TryGetValue(accepted.PreservedSource, out var current))
            {
                var predicted = accepted.Changes.FirstOrDefault(change => change.Id == current.Id).After;
                if (predicted.HasValue && current.SameOccupancy(predicted.Value) && !current.Equals(predicted.Value))
                    for (int index = 0; index < nextPending.Count; index++)
                        if (nextPending[index].PreservedSource == current.Id)
                            nextPending[index] = nextPending[index].WithUpdatedSource(current.Facts);
            }
        }
        else if (nextPending.Count > 0)
        {
            foreach (var change in changes)
            {
                var before = Find(confirmed, change.Id);
                if (Nullable.Equals(change.After, before)) continue;
                bool stayedInPlace = before.HasValue && change.After.HasValue &&
                    before.Value.SameOccupancy(change.After.Value);
                for (int index = nextPending.Count - 1; index >= 0; index--)
                {
                    var action = nextPending[index];
                    if (!action.DependsOn(change.Id)) continue;
                    if (stayedInPlace && action.PreservedSource == change.Id)
                        nextPending[index] = action.WithUpdatedSource(change.After.Value.Facts);
                    else nextPending.RemoveAt(index);
                }
            }
        }
        var nextVisible = Rebase(nextConfirmed, nextPending);
        if (!project(nextVisible)) return false;
        confirmed = nextConfirmed;
        visible = nextVisible;
        pending = nextPending;
        return true;
    }

    internal bool Reject(ulong serial, Func<IReadOnlyDictionary<int, InventoryEntry>, bool> project)
    {
        var nextPending = pending.Where(action => action.Serial != serial).ToList();
        if (nextPending.Count == pending.Count) return true;
        var next = Rebase(confirmed, nextPending);
        if (!project(next)) return false;
        pending = nextPending;
        visible = next;
        return true;
    }

    internal bool Retains(int id) => confirmed.ContainsKey(id) || visible.ContainsKey(id) ||
        pending.Any(action => action.DependsOn(id));

    private Dictionary<int, InventoryEntry> Rebase(Dictionary<int, InventoryEntry> basis, List<Prediction> predictions)
    {
        foreach (var action in predictions)
            if (action.Matches(basis) && Stage(basis, action.Changes, out var next)) basis = next;
        return basis;
    }

    private bool Stage(Dictionary<int, InventoryEntry> basis, InventoryChange[] changes, out Dictionary<int, InventoryEntry> next)
    {
        next = null;
        if (changes == null || changes.Length == 0 || changes.Length > MaxChanges) return false;
        var result = new Dictionary<int, InventoryEntry>(basis);
        var changed = new HashSet<int>();
        bool removedOwner = false;
        foreach (var change in changes)
        {
            if (change.Id <= 0 || !changed.Add(change.Id)) return false;
            if (!change.After.HasValue)
            {
                removedOwner |= result.Remove(change.Id);
                continue;
            }
            var item = change.After.Value;
            if (!item.Valid || item.Id != change.Id) return false;
            if (basis.TryGetValue(item.Id, out var existing) && existing.CatalogId != item.CatalogId) return false;
            result[item.Id] = item;
        }
        if (result.Count > InventoryCheckpoint.MaxItems) return false;
        foreach (var change in changes)
        {
            if (change.After.HasValue &&
                (!change.After.Value.Placement.Address.TryResolveRoot(change.Id, result, out _) ||
                 !fits(change.After.Value, result))) return false;
        }
        if (removedOwner && result.Values.Any(item => !item.Placement.Address.TryResolveRoot(item.Id, result, out _)))
            return false;
        next = result;
        return true;
    }

    private static InventoryEntry? Find(IReadOnlyDictionary<int, InventoryEntry> state, int id) =>
        state.TryGetValue(id, out var value) ? value : (InventoryEntry?)null;
}
