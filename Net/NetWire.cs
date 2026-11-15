using ProbablyCoop.Sync;

namespace ProbablyCoop.Net;

// Link-layer framing, generation 2 ("PCO2"). Every frame is
//   magic(4) kind(1) link(varint) body
// and bodies use the varint helpers in SyncBuffer. The link id is 0 on Hello and is
// chosen by the host in Welcome; any later frame carrying another id is dropped, which
// is what isolates a new connection from stragglers of an earlier one.
//
//   Hello    guest -> host   nonce, oldest protocol, newest protocol
//   Welcome  host -> guest   nonce echo, chosen protocol
//   Refuse   host -> guest   nonce echo, NetRefusal
//   Ping     both            serial
//   Pong     both            serial echo
//   Segment  both            seq, ack, mask, channel, flags, payload
//   Ack      both            ack, mask
//   Datagram both            channel, serial, payload   (latest wins, may be lost)
//   Bye      both            (empty)
//
// Content compatibility (game build, mod list) is deliberately not part of this layer:
// it is negotiated over the reliable stream once the link is open, so it is not bound by
// the datagram size and can be reviewed by the host instead of failing a byte comparison.
internal enum NetKind : byte
{
    Hello = 1,
    Welcome = 2,
    Refuse = 3,
    Ping = 4,
    Pong = 5,
    Segment = 6,
    Ack = 7,
    Datagram = 8,
    Bye = 9
}

internal enum NetRefusal : byte
{
    Protocol = 1,
    Busy = 2
}

internal static class NetWire
{
    // Bump Protocol for any change to frame bodies or to the messages carried on the
    // stream. Oldest is the lowest version this build can still speak.
    internal const uint Protocol = 1;
    internal const uint Oldest = 1;

    private const byte Magic0 = 0x50, Magic1 = 0x43, Magic2 = 0x4F, Magic3 = 0x32;

    internal static SyncWriter Open(NetKind kind, ulong link)
    {
        var writer = new SyncWriter();
        writer.Byte(Magic0);
        writer.Byte(Magic1);
        writer.Byte(Magic2);
        writer.Byte(Magic3);
        writer.Byte((byte)kind);
        writer.ULong(link);
        return writer;
    }

    internal static bool TryOpen(byte[] data, int count, out NetKind kind, out ulong link, out SyncReader body)
    {
        body = new SyncReader(data, count);
        bool magic = body.Byte() == Magic0 & body.Byte() == Magic1 & body.Byte() == Magic2 & body.Byte() == Magic3;
        kind = (NetKind)body.Byte();
        link = body.ULong();
        return body.Ok && magic && kind >= NetKind.Hello && kind <= NetKind.Bye;
    }

    // Highest version both sides speak, or 0 when the ranges do not overlap.
    internal static uint Agree(uint oldest, uint newest)
    {
        if (oldest == 0 || newest < oldest) return 0;
        uint chosen = newest < Protocol ? newest : Protocol;
        return chosen >= oldest && chosen >= Oldest ? chosen : 0;
    }
}
