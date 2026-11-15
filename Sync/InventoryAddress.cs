using System;
using System.Collections.Generic;

namespace ProbablyCoop.Sync;

internal enum InventoryRoot : byte
{
    Main = 0,
    FrontCounter = 1,
    BackCounter = 2,
    Showcase = 3,
    SceneGround = 4,
    HostCarry = 5,
    PartnerCarry = 6,
    Nested = 7,
    ParkedCounter = 8,
    Vending = 9,
    Fountain = 10,
    HiddenStock = 11,
    Cassette = 12,
    Dossier = 13,
}

// The slot in a nested address is the inventory's index in its item's catalog
// definition. It does not change if the container is moved or rotated.
internal readonly struct InventoryAddress : IEquatable<InventoryAddress>
{
    internal readonly InventoryRoot Root;
    internal readonly int OwnerItem;
    internal readonly byte Slot;

    internal InventoryAddress(InventoryRoot root, int ownerItem = 0, byte slot = 0)
    {
        Root = root;
        OwnerItem = ownerItem;
        Slot = slot;
    }

    internal bool Valid => Root switch
    {
        InventoryRoot.Nested => OwnerItem > 0,
        InventoryRoot.HostCarry or InventoryRoot.PartnerCarry => OwnerItem == 0 && Slot <= 2,
        InventoryRoot.Cassette => OwnerItem == 0 && Slot <= 1,
        InventoryRoot.ParkedCounter or InventoryRoot.Vending or InventoryRoot.Fountain or InventoryRoot.HiddenStock or InventoryRoot.Dossier =>
            OwnerItem == 0 && Slot == 0,
        _ => Root < InventoryRoot.HostCarry && OwnerItem == 0 && Slot == 0,
    };

    internal void Write(SyncWriter writer)
    {
        writer.Byte((byte)Root);
        if (Root == InventoryRoot.Nested) writer.UInt((uint)OwnerItem);
        if (Root is InventoryRoot.Nested or InventoryRoot.HostCarry or InventoryRoot.PartnerCarry or InventoryRoot.Cassette)
            writer.Byte(Slot);
    }

    internal static InventoryAddress Read(SyncReader reader)
    {
        var root = (InventoryRoot)reader.Byte();
        if (root == InventoryRoot.Nested)
            return new InventoryAddress(root, unchecked((int)reader.UInt()), reader.Byte());
        if (root is InventoryRoot.HostCarry or InventoryRoot.PartnerCarry or InventoryRoot.Cassette)
            return new InventoryAddress(root, slot: reader.Byte());
        return new InventoryAddress(root);
    }

    internal bool TryResolveRoot(int movingItem, IReadOnlyDictionary<int, InventoryEntry> placements, out InventoryRoot root)
    {
        InventoryAddress address = this;
        // One parent per item. A path longer than the inventory implies a cycle.
        for (int depth = 0; depth <= placements.Count && address.Valid; depth++)
        {
            if (address.Root != InventoryRoot.Nested)
            {
                root = address.Root;
                return true;
            }
            if (address.OwnerItem == movingItem || !placements.TryGetValue(address.OwnerItem, out var owner)) break;
            address = owner.Placement.Address;
        }
        root = default;
        return false;
    }

    internal bool IsInside(int parentItem, IReadOnlyDictionary<int, InventoryEntry> items)
    {
        var address = this;
        for (int depth = 0; depth < items.Count && address.Root == InventoryRoot.Nested; depth++)
        {
            if (address.OwnerItem == parentItem) return true;
            if (!items.TryGetValue(address.OwnerItem, out var owner)) break;
            address = owner.Placement.Address;
        }
        return false;
    }

    public bool Equals(InventoryAddress other) => Root == other.Root && OwnerItem == other.OwnerItem && Slot == other.Slot;
    public override bool Equals(object other) => other is InventoryAddress address && Equals(address);
    public override int GetHashCode() => ((int)Root * 397 ^ OwnerItem) * 397 ^ Slot;
}

internal readonly struct InventoryPlacement : IEquatable<InventoryPlacement>
{
    internal readonly InventoryAddress Address;
    internal readonly GridPose Pose;

    internal InventoryPlacement(InventoryAddress address, GridPose pose)
    {
        Address = address;
        Pose = pose;
    }

    internal bool Valid => Address.Valid && Pose.Transform <= 7;

    internal byte[] Encode()
    {
        var writer = new SyncWriter();
        Write(writer);
        return writer.ToArray();
    }

    internal void Write(SyncWriter writer)
    {
        Address.Write(writer);
        writer.UInt(Pose.X);
        writer.UInt(Pose.Y);
        writer.Byte(Pose.Transform);
    }

    internal static InventoryPlacement Read(SyncReader reader)
    {
        var address = InventoryAddress.Read(reader);
        uint x = reader.UInt(), y = reader.UInt();
        byte orientation = reader.Byte();
        if (x > ushort.MaxValue || y > ushort.MaxValue) orientation = byte.MaxValue;
        return new InventoryPlacement(address, new GridPose((ushort)x, (ushort)y, orientation));
    }

    internal static bool TryRead(byte[] data, out InventoryPlacement placement)
    {
        placement = default;
        if (data == null || data.Length < 4 || data.Length > 14) return false;
        var reader = new SyncReader(data);
        placement = Read(reader);
        return reader.Ok && reader.AtEnd && placement.Valid;
    }

    public bool Equals(InventoryPlacement other) => Address.Equals(other.Address) && Pose.Equals(other.Pose);
    public override bool Equals(object other) => other is InventoryPlacement placement && Equals(placement);
    public override int GetHashCode() => Address.GetHashCode() * 397 ^ Pose.GetHashCode();
}
