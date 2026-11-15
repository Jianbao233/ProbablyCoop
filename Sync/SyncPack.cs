using System;
using System.IO;
using System.IO.Compression;

namespace ProbablyCoop.Sync;

// Large payloads (a whole save) travel deflated: declared length, then the stream.
// Unpack never trusts the declared length beyond the caller's limit and rejects
// any stream that inflates to a different size.
internal static class SyncPack
{
    internal static byte[] Pack(byte[] raw)
    {
        if (raw == null) throw new ArgumentNullException(nameof(raw));
        using var buffer = new MemoryStream();
        var header = new SyncWriter();
        header.ULong((ulong)raw.Length);
        byte[] head = header.ToArray();
        buffer.Write(head, 0, head.Length);
        using (var deflate = new DeflateStream(buffer, CompressionLevel.Fastest, true))
            deflate.Write(raw, 0, raw.Length);
        return buffer.ToArray();
    }

    internal static bool TryUnpack(byte[] packed, int offset, int limit, out byte[] raw)
    {
        raw = null;
        if (packed == null || offset < 0 || limit < 0) return false;
        int head = 0;
        ulong length = 0;
        while (true)
        {
            if (offset + head >= packed.Length || head >= 5) return false;
            byte part = packed[offset + head];
            length |= (ulong)(part & 0x7F) << (7 * head);
            head++;
            if ((part & 0x80) == 0) break;
        }
        if (length > (ulong)limit) return false;
        var result = new byte[(int)length];
        try
        {
            using var source = new MemoryStream(packed, offset + head, packed.Length - offset - head, false);
            using var inflate = new DeflateStream(source, CompressionMode.Decompress);
            int filled = 0;
            while (filled < result.Length)
            {
                int read = inflate.Read(result, filled, result.Length - filled);
                if (read <= 0) return false;
                filled += read;
            }
            if (inflate.ReadByte() >= 0) return false;
        }
        catch (InvalidDataException)
        {
            return false;
        }
        catch (IOException)
        {
            // Mono reports a corrupt stream this way.
            return false;
        }
        raw = result;
        return true;
    }
}
