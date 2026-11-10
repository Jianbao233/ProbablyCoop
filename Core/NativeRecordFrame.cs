using System;
using System.Buffers.Binary;
using System.IO;

namespace ProbablyCoop.Core;

internal static class NativeRecordFrame
{
    private const uint Magic=0x31445250; // PRD1; the body is one PSB1 projection.
    private const int HeaderSize=12;
    internal static bool IsDelta(byte[] bytes) => bytes?.Length>=4 && BinaryPrimitives.ReadUInt32LittleEndian(bytes)==Magic;
    internal static bool IsFull(byte[] bytes) => bytes?.Length>=4 && BinaryPrimitives.ReadUInt32LittleEndian(bytes)==0x31425350;
    internal static bool HasBase(byte[] bytes,ulong revision) => revision!=0 && IsDelta(bytes) &&
        bytes.Length>=HeaderSize+4 && BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(4))==revision &&
        BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(HeaderSize))==0x31425350;
    internal static byte[] Delta(byte[] body,ulong revision)
    {
        if(revision==0 || !IsFull(body) || body.Length>ReplicaLink.MaxBytes-HeaderSize) throw new InvalidDataException("Invalid incremental item frame");
        var bytes=new byte[HeaderSize+body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes,Magic);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(4),revision);
        body.CopyTo(bytes,HeaderSize);
        return bytes;
    }
    internal static byte[] Body(byte[] bytes,ulong revision)
    {
        if(!HasBase(bytes,revision)) throw new InvalidDataException("Unavailable native record basis");
        return bytes.AsSpan(HeaderSize).ToArray();
    }
}
