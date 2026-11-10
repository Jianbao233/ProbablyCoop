using System;
using System.Buffers.Binary;

namespace ProbablyCoop.Core;

internal enum Kind : byte
{
    Hello = 1, Welcome, Ready, State, ChangeTile, Ping, Pong, Cursor, Reject, ShopState, ShopCommand, ShopAck,
    NativeChunk, NativeAck, NativeCommand, NativeReply, NativePresence, NativeTooltip, NativeProgress, NativeNeedFull
}

internal readonly struct Packet
{
    internal Kind Kind { get; }
    internal Guid Session { get; }
    internal Guid Epoch { get; }
    internal ulong Serial { get; }
    internal ulong Revision { get; }
    internal ushort Value { get; }
    internal ushort X { get; }
    internal ushort Y { get; }
    internal byte Flags { get; }
    internal byte[] Build { get; }
    internal byte[] Payload { get; }

    internal Packet(Kind kind, Guid session = default, Guid epoch = default, ulong serial = 0,
        ulong revision = 0, ushort value = 0, ushort x = 0, ushort y = 0, byte flags = 0, byte[] build = null,
        byte[] payload = null)
    {
        Kind = kind;
        Session = session;
        Epoch = epoch;
        Serial = serial;
        Revision = revision;
        Value = value;
        X = x;
        Y = y;
        Flags = flags;
        Build = build;
        Payload = payload;
    }
}

internal static class Wire
{
    internal const byte Version = 22;
    internal const int FingerprintSize = 64; // SHA-256 of game DLL followed by SHA-256 of this mod DLL.
    internal const int HeaderSize = 60;
    internal const int MaxSize = 20 * 1024;
    private const uint Magic = 0x504f4350; // PCOP

    internal static byte[] Encode(Packet packet)
    {
        bool handshake = packet.Kind is Kind.Hello or Kind.Welcome;
        if (handshake && packet.Build?.Length != FingerprintSize)
            throw new ArgumentException("Invalid build fingerprint.", nameof(packet));
        var body = handshake ? packet.Build : packet.Payload;
        var bytes = new byte[HeaderSize + (body?.Length ?? 0)];
        if (bytes.Length > MaxSize) throw new ArgumentException("Packet too large.", nameof(packet));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, Magic);
        bytes[4] = Version;
        bytes[5] = (byte)packet.Kind;
        bytes[6] = packet.Flags;
        packet.Session.TryWriteBytes(bytes.AsSpan(8, 16));
        packet.Epoch.TryWriteBytes(bytes.AsSpan(24, 16));
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(40), packet.Serial);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(48), packet.Revision);
        // The final four bytes carry either a tile/mask or both cursor coordinates.
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(56), packet.Kind == Kind.Cursor ? packet.X : packet.Value);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(58), packet.Y);
        body?.CopyTo(bytes, HeaderSize);
        return bytes;
    }

    internal static bool TryDecode(ReadOnlySpan<byte> bytes, bool cursorChannel, out Packet packet)
    {
        packet = default;
        if (bytes.Length < HeaderSize || bytes.Length > MaxSize ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes) != Magic || bytes[4] != Version ||
            bytes[7] != 0 || bytes[5] < (byte)Kind.Hello || bytes[5] > (byte)Kind.NativeNeedFull) return false;
        var kind = (Kind)bytes[5];
        bool handshake = kind is Kind.Hello or Kind.Welcome;
        int bodyLength = bytes.Length - HeaderSize;
        if (cursorChannel != (kind == Kind.Cursor) || (kind switch
        {
            Kind.Hello or Kind.Welcome => bodyLength != FingerprintSize,
            Kind.Cursor => bodyLength is not (0 or 4),
            Kind.ShopState => bodyLength < ShopProtocol.MinSize,
            Kind.ShopCommand => bodyLength != 8,
            Kind.NativeChunk => bodyLength <= 4 || bodyLength > ReplicaLink.ChunkSize + 4,
            Kind.NativeCommand => bodyLength == 0 || bodyLength > ReplicaLink.MaxCommand,
            Kind.NativePresence => !ReplicaLink.ValidPresence(bytes.Slice(HeaderSize)),
            Kind.NativeTooltip => bodyLength<1 || bodyLength>ReplicaLink.MaxTooltip,
            _ => bodyLength != 0
        })) return false;

        var flags = bytes[6];
        var a = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(56));
        var b = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(58));
        var serial = BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(40));
        if (kind == Kind.Cursor ? flags > 3 || serial == 0 : flags != 0 || b != 0) return false;
        if (kind == Kind.ChangeTile && (serial == 0 || a >= 16)) return false;
        if (kind == Kind.ShopCommand)
        {
            if (serial == 0 || a < 1 || a > (ushort)ShopAction.ChooseDialogue) return false;
            var target = bytes.Slice(HeaderSize + 4);
            if (a == (ushort)ShopAction.Place)
            {
                if (!new ShopPlacement(target[0], target[1], target[2], target[3]).Valid) return false;
            }
            else if (BinaryPrimitives.ReadUInt32LittleEndian(target) != 0) return false;
        }
        if (kind == Kind.ShopState && a > (ushort)ShopResult.Failed) return false;
        if (kind == Kind.NativeCommand && (serial == 0 || a != 0)) return false;
        if (kind == Kind.NativeReply && (serial == 0 || a > (ushort)ShopResult.Failed)) return false;
        if (kind == Kind.NativeAck && (a != 0 || serial != 0)) return false;
        if (kind == Kind.NativeNeedFull && (a!=0 || serial!=0 || BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(48))==0)) return false;
        if (kind == Kind.NativePresence && (a != 0 || serial == 0)) return false;
        if (kind == Kind.NativeTooltip && (a!=0 || serial==0 || serial>uint.MaxValue || BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(48))==0)) return false;
        if (kind==Kind.NativeProgress && (serial==ulong.MaxValue || BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(48))!=serial+1 ||
            a>=(ReplicaLink.MaxBytes+ReplicaLink.ChunkSize-1)/ReplicaLink.ChunkSize)) return false;
        if (kind == Kind.ShopAck && (a != 0 || BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(48)) == 0))
            return false;

        packet = new Packet(kind, new Guid(bytes.Slice(8, 16)), new Guid(bytes.Slice(24, 16)), serial,
            BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(48)), kind == Kind.Cursor ? (ushort)0 : a,
            kind == Kind.Cursor ? a : (ushort)0, b, flags, handshake ? bytes.Slice(HeaderSize).ToArray() : null,
            kind is Kind.Cursor or Kind.ShopState or Kind.ShopCommand or Kind.NativeChunk or Kind.NativeCommand or Kind.NativePresence or Kind.NativeTooltip ? bytes.Slice(HeaderSize).ToArray() : null);
        return true;
    }
}
