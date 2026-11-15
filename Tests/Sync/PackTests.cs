using System;
using System.Text;
using ProbablyCoop.Net;
using ProbablyCoop.Sync;

namespace ProbablyCoop.SyncTests;

internal static partial class NetTests
{
    private static void Packs()
    {
        var random = new Random(41);

        // Round trips: empty, tiny, incompressible, highly compressible.
        foreach (int size in new[] { 0, 1, 2, 127, 128, 129, 4096, 70000 })
        {
            byte[] raw = Blob(random, size);
            byte[] packed = SyncPack.Pack(raw);
            Check(SyncPack.TryUnpack(packed, 0, size, out byte[] back) && Same(raw, back), "pack-round-trip-" + size);
            Check(size == 0 || !SyncPack.TryUnpack(packed, 0, size - 1, out _), "pack-limit-enforced-" + size);
        }
        byte[] text = Encoding.UTF8.GetBytes(new StringBuilder().Insert(0, "{\"itemId\":\"crate\",\"count\":3},", 40000).ToString());
        byte[] small = SyncPack.Pack(text);
        Check(small.Length * 20 < text.Length, "pack-shrinks-repetitive-json");
        Check(SyncPack.TryUnpack(small, 0, text.Length, out byte[] textBack) && Same(text, textBack), "pack-round-trip-json");

        // Offset: the payload may sit behind a header.
        byte[] shifted = new byte[small.Length + 3];
        Buffer.BlockCopy(small, 0, shifted, 3, small.Length);
        Check(SyncPack.TryUnpack(shifted, 3, text.Length, out byte[] shiftedBack) && Same(text, shiftedBack), "pack-offset");
        Check(!SyncPack.TryUnpack(shifted, 0, text.Length, out _), "pack-wrong-offset-rejected");

        // Bad input never throws and never yields data.
        Check(!SyncPack.TryUnpack(null, 0, 10, out _), "pack-null");
        Check(!SyncPack.TryUnpack(small, -1, text.Length, out _), "pack-negative-offset");
        Check(!SyncPack.TryUnpack(small, small.Length, text.Length, out _), "pack-offset-at-end");
        Check(!SyncPack.TryUnpack(small, small.Length + 1, text.Length, out _), "pack-offset-past-end");
        Check(!SyncPack.TryUnpack(small, 0, -1, out _), "pack-negative-limit");
        bool threw = false;
        try { SyncPack.Pack(null); } catch (ArgumentNullException) { threw = true; }
        Check(threw, "pack-null-raw-throws");

        // A truncated stream never yields wrong data: it is rejected, or (when only
        // the stream's closing padding is missing) yields exactly the original.
        byte[] sampleRaw = Blob(random, 3000);
        byte[] sample = SyncPack.Pack(sampleRaw);
        bool truncated = true;
        int accepted = 0;
        for (int length = 0; length < sample.Length; length++)
        {
            byte[] cut = new byte[length];
            Buffer.BlockCopy(sample, 0, cut, 0, length);
            if (!SyncPack.TryUnpack(cut, 0, 3000, out byte[] part)) continue;
            accepted++;
            if (!Same(part, sampleRaw)) truncated = false;
        }
        Check(truncated, "pack-truncation-never-wrong");
        Check(accepted <= 8, "pack-truncation-mostly-rejected");

        // Declared length must match what the stream holds, both ways.
        byte[] two = SyncPack.Pack(new byte[] { 7, 8 });
        byte[] claimsOne = (byte[])two.Clone();
        claimsOne[0] = 1;
        Check(!SyncPack.TryUnpack(claimsOne, 0, 100, out _), "pack-stream-longer-than-declared");
        byte[] claimsThree = (byte[])two.Clone();
        claimsThree[0] = 3;
        Check(!SyncPack.TryUnpack(claimsThree, 0, 100, out _), "pack-stream-shorter-than-declared");

        // A huge declared length is refused before any allocation or inflation.
        byte[] bomb = { 0xFF, 0xFF, 0xFF, 0xFF, 0x07, 0x03, 0x00 };
        Check(!SyncPack.TryUnpack(bomb, 0, WorldWire.MaxSave, out _), "pack-declared-over-limit");
        byte[] endless = { 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x00 };
        Check(!SyncPack.TryUnpack(endless, 0, WorldWire.MaxSave, out _), "pack-overlong-length");
        // Real deflate bomb: 32 MB of zeros packs to well under 1 MB; a 1 MB limit refuses it.
        byte[] zeros = SyncPack.Pack(new byte[32 * 1024 * 1024]);
        Check(zeros.Length < 1024 * 1024, "pack-zeros-small");
        Check(!SyncPack.TryUnpack(zeros, 0, 1024 * 1024, out _), "pack-bomb-over-limit");

        // Corruption fuzz: no exception escapes; a result, if any, has the declared size.
        bool safe = true;
        for (int round = 0; round < 3000; round++)
        {
            byte[] bad = (byte[])sample.Clone();
            int flips = 1 + random.Next(4);
            for (int i = 0; i < flips; i++) bad[random.Next(bad.Length)] ^= (byte)(1 << random.Next(8));
            try
            {
                if (SyncPack.TryUnpack(bad, 0, 5000, out byte[] got) && got == null) safe = false;
            }
            catch (Exception)
            {
                safe = false;
            }
        }
        Check(safe, "pack-fuzz-no-throw");

        // World wire: checkpoint framing.
        byte[] save = Blob(random, 50000);
        byte[] message = WorldWire.EncodeCheckpoint(7, 0x0102030405060708UL, save);
        Check(message[0] == WorldWire.Checkpoint, "wire-checkpoint-kind");
        Check(WorldWire.TryDecodeCheckpoint(message, out uint barrierBack, out ulong sequenceBack, out byte[] saveBack) &&
            Same(save, saveBack) && barrierBack == 7 && sequenceBack == 0x0102030405060708UL, "wire-checkpoint-round-trip");
        Check(WorldWire.TryDecodeCheckpoint(WorldWire.EncodeCheckpoint(uint.MaxValue, ulong.MaxValue, save), out barrierBack, out sequenceBack, out _) &&
            barrierBack == uint.MaxValue && sequenceBack == ulong.MaxValue, "wire-checkpoint-header-extremes");
        byte[] otherKind = (byte[])message.Clone();
        otherKind[0] = 2;
        Check(!WorldWire.TryDecodeCheckpoint(otherKind, out _, out _, out _), "wire-unknown-kind");
        Check(!WorldWire.TryDecodeCheckpoint(null, out _, out _, out _), "wire-null");
        Check(!WorldWire.TryDecodeCheckpoint(new byte[0], out _, out _, out _), "wire-empty");
        bool shortRejected = true;
        for (int length = 1; length <= 13; length++)
        {
            var cut = new byte[length];
            System.Array.Copy(message, cut, length);
            if (WorldWire.TryDecodeCheckpoint(cut, out _, out _, out _)) shortRejected = false;
        }
        Check(shortRejected, "wire-header-only");
        Check(!WorldWire.TryDecodeCheckpoint(WorldWire.EncodeCheckpoint(0, 0, new byte[0]), out _, out _, out _), "wire-empty-save-rejected");
        Check(WorldWire.IsResync(WorldWire.EncodeResync()) && !WorldWire.IsResync(null) && !WorldWire.IsResync(new byte[0]) &&
            !WorldWire.IsResync(new byte[] { WorldWire.Resync, 0 }) && !WorldWire.IsResync(new byte[] { WorldWire.Checkpoint }), "wire-resync");
        bool distinct = WorldWire.Checkpoint != WorldWire.Resync;
        foreach (SyncMessage kind in System.Enum.GetValues(typeof(SyncMessage)))
            if ((byte)kind == WorldWire.Checkpoint || (byte)kind == WorldWire.Resync) distinct = false;
        Check(distinct, "wire-kinds-distinct");
        Check(WorldWire.Channel != NetSession.Control, "wire-channel-not-control");

        // A save-sized checkpoint crosses a real room in one message.
        byte[] big = Encoding.UTF8.GetBytes(new StringBuilder().Insert(0, "{\"itemId\":\"crate\",\"q\":" + random.Next() + "},", 60000).ToString());
        var host = new RoomEnd(Plain());
        var guest = new RoomEnd(Plain());
        host.Room.Create(Now);
        guest.Room.Join(Now, host.Room.Code);
        SpinRooms(() => host.Room.IsReady && guest.Room.IsReady, 5, host, guest);
        byte seenChannel = 0;
        byte[] seen = null;
        int arrivals = 0;
        guest.Room.Message = (channel, body) => { seenChannel = channel; seen = body; arrivals++; };
        bool queued = host.Room.Send(WorldWire.Channel, WorldWire.EncodeCheckpoint(3, 9, big));
        SpinRooms(() => arrivals > 0, 10, host, guest);
        Check(queued && arrivals == 1 && seenChannel == WorldWire.Channel &&
            WorldWire.TryDecodeCheckpoint(seen, out uint bigBarrier, out ulong bigSequence, out byte[] bigBack) &&
            Same(big, bigBack) && bigBarrier == 3 && bigSequence == 9, "wire-checkpoint-crosses-room");
        host.Room.Dispose();
        guest.Room.Dispose();
    }
}
