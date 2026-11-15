namespace ProbablyCoop.Sync;

internal enum ItemUse : byte { Activate = 1, Unload, Pin, OpenScratcher, StartScratch, RevealSymbol, FillAtTap, EmptyAtDrain }

internal readonly struct ItemIntent
{
    internal readonly ItemUse Use;
    internal readonly int Item;
    internal readonly ulong Proof;
    internal readonly byte Argument;

    internal ItemIntent(ItemUse use, int item, ulong proof, byte argument = 0)
    {
        Use = use;
        Item = item;
        Proof = proof;
        Argument = argument;
    }

    internal bool Valid => Item > 0 && Proof != 0 && (Use switch
    {
        ItemUse.Activate or ItemUse.OpenScratcher or ItemUse.StartScratch or ItemUse.FillAtTap or ItemUse.EmptyAtDrain =>
            Argument == 0,
        ItemUse.Unload or ItemUse.Pin => Argument <= 1,
        ItemUse.RevealSymbol => Argument is >= 1 and <= 3,
        _ => false,
    });

    internal void Write(SyncWriter writer)
    {
        writer.Byte((byte)Use);
        writer.UInt((uint)Item);
        writer.ULong(Proof);
        writer.Byte(Argument);
    }

    internal static ItemIntent Read(SyncReader reader)
    {
        var use = (ItemUse)reader.Byte();
        uint item = reader.UInt();
        ulong proof = reader.ULong();
        byte argument = reader.Byte();
        return reader.Ok && item <= int.MaxValue ? new ItemIntent(use, (int)item, proof, argument) : default;
    }
}
