namespace ProbablyCoop.Sync;

internal enum InventoryActionKind : byte { Transfer = 1, Target = 2 }

internal readonly struct InventoryAction
{
    internal readonly InventoryActionKind Kind;
    internal readonly int Source, Target, Amount;
    internal readonly InventoryPlacement Destination;
    internal readonly InventoryPlacement? UnchangedSource;

    internal InventoryAction(int source, int target, int amount, InventoryPlacement destination,
        InventoryPlacement? unchangedSource = null)
    {
        Kind = InventoryActionKind.Transfer;
        Source = source;
        Target = target;
        Amount = amount;
        Destination = destination;
        UnchangedSource = unchangedSource;
    }

    internal InventoryAction(int source, int target)
    {
        Kind = InventoryActionKind.Target;
        Source = source;
        Target = target;
        Amount = 0;
        Destination = default;
        UnchangedSource = null;
    }

    internal byte[] Encode()
    {
        var writer = new SyncWriter();
        writer.Byte((byte)Kind);
        writer.UInt((uint)Source);
        writer.UInt((uint)Target);
        if (Kind == InventoryActionKind.Transfer)
        {
            writer.UInt((uint)Amount);
            Destination.Write(writer);
            writer.Bool(UnchangedSource.HasValue);
            if (UnchangedSource.HasValue) UnchangedSource.Value.Write(writer);
        }
        return writer.ToArray();
    }

    internal static bool TryRead(byte[] data, out InventoryAction action)
    {
        action = default;
        if (data == null || data.Length == 0 || data.Length > SyncCodec.MaxContext) return false;
        var reader = new SyncReader(data);
        var kind = (InventoryActionKind)reader.Byte();
        int source = unchecked((int)reader.UInt()), target = unchecked((int)reader.UInt());
        if (!reader.Ok || source <= 0 || target < 0 || source == target) return false;
        switch (kind)
        {
            case InventoryActionKind.Transfer:
                uint amount = reader.UInt();
                var destination = InventoryPlacement.Read(reader);
                InventoryPlacement? unchangedSource = reader.Bool() ? InventoryPlacement.Read(reader) : null;
                if (amount == 0 || amount > InventoryEntry.MaxUnits || !destination.Valid ||
                    unchangedSource.HasValue && (target != 0 || !unchangedSource.Value.Valid ||
                        unchangedSource.Value.Equals(destination))) return false;
                action = new InventoryAction(source, target, (int)amount, destination, unchangedSource);
                break;
            case InventoryActionKind.Target:
                if (target == 0) return false;
                action = new InventoryAction(source, target);
                break;
            default:
                return false;
        }
        return reader.Ok && reader.AtEnd;
    }
}
