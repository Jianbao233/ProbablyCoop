using System;
using System.Collections.Generic;
using ProbablyCoop.Sync;

namespace ProbablyCoop.Net;

// Reliable, ordered message stream over a datagram transport that may drop, duplicate
// or reorder. Messages of any size up to MaxMessage are cut into segments that fit one
// datagram; the peer reassembles them and delivers whole messages in send order.
//
// One sequence space covers every channel, so messages on different channels keep their
// relative order. That is required: a replication commit and the session directive that
// follows it must be seen in the order the host produced them.
//
// Acknowledgement is cumulative (ack = highest sequence below which everything arrived)
// plus a 64-bit mask where bit i reports sequence ack + 2 + i. Sequence ack + 1 is by
// definition missing, so it needs no bit.
internal sealed class NetStream
{
    internal const int MaxMessage = SyncCodec.MaxFrameBytes;
    internal const int MaxBufferedBytes = 2 * MaxMessage;
    internal const int MaxBufferedMessages = 4096;
    internal const int Window = 256;
    internal const int Overhead = 56;
    private const int Burst = 32;
    private const byte First = 1, Last = 2;

    private readonly ulong link;
    private readonly int segment;
    private readonly Func<byte[], bool> send;
    private readonly Action<byte, byte[]> deliver;

    private readonly Queue<Outgoing> backlog = new Queue<Outgoing>();
    private readonly List<Flight> flights = new List<Flight>();
    private readonly Dictionary<ulong, Parked> parked = new Dictionary<ulong, Parked>();
    private readonly List<byte[]> parts = new List<byte[]>();
    private int cut;
    private int bufferedBytes;
    private int bufferedMessages;
    private ulong nextSeq = 1;
    private ulong delivered;
    private bool ackDue;
    private bool assembling;
    private byte assemblingChannel;
    private int assembledBytes;
    private bool stopped;

    internal NetStream(ulong link, int maxDatagram, Func<byte[], bool> send, Action<byte, byte[]> deliver)
    {
        if (maxDatagram < Overhead + 64) throw new ArgumentOutOfRangeException(nameof(maxDatagram));
        this.link = link;
        this.send = send ?? throw new ArgumentNullException(nameof(send));
        this.deliver = deliver ?? throw new ArgumentNullException(nameof(deliver));
        segment = maxDatagram - Overhead;
    }

    // Retransmission timeout in seconds. The link adjusts it from measured round trips.
    internal double Rto { get; set; } = 0.5;

    // Set when the peer violates the stream contract. The link must be closed.
    internal bool Broken { get; private set; }

    internal bool Idle => backlog.Count == 0 && flights.Count == 0;

    internal int Queued => backlog.Count + flights.Count;

    internal int Resent { get; private set; }

    // The array is referenced until every segment is acknowledged; the caller must not
    // modify it after this call.
    internal bool Send(byte channel, byte[] message)
    {
        if (stopped || Broken || message == null || message.Length == 0 || message.Length > MaxMessage ||
            bufferedMessages >= MaxBufferedMessages || message.Length > MaxBufferedBytes - bufferedBytes)
            return false;
        backlog.Enqueue(new Outgoing { Channel = channel, Data = message });
        bufferedBytes += message.Length;
        bufferedMessages++;
        return true;
    }

    internal void Stop()
    {
        stopped = true;
        backlog.Clear();
        flights.Clear();
        parked.Clear();
        parts.Clear();
        bufferedBytes = 0;
        bufferedMessages = 0;
    }

    internal void Pump(double now)
    {
        if (stopped || Broken) return;
        Fill();
        int budget = Burst;
        bool carried = false;
        for (int i = 0; i < flights.Count && budget > 0; i++)
        {
            var flight = flights[i];
            if (flight.Acked) continue;
            if (flight.Tries != 0 && !flight.Lost && now < flight.Resend) continue;
            if (!send(Encode(flight))) break;
            if (flight.Tries != 0) Resent++;
            flight.Lost = false;
            flight.SentAt = now;
            flight.Tries = Math.Min(flight.Tries + 1, 6);
            // Exponential backoff, capped at eight times the base timeout.
            flight.Resend = now + Rto * (1 << Math.Min(flight.Tries - 1, 3));
            carried = true;
            budget--;
        }
        if (carried) ackDue = false;
        else if (ackDue && send(EncodeAck())) ackDue = false;
    }

    // Body of a Segment frame, positioned after the frame header.
    internal bool OnSegment(SyncReader body, double now)
    {
        ulong seq = body.ULong();
        ulong ack = body.ULong();
        ulong mask = body.ULong();
        byte channel = body.Byte();
        byte flags = body.Byte();
        byte[] payload = body.Bytes(64 * 1024);
        if (stopped || Broken) return false;
        if (!body.Ok || !body.AtEnd || seq == 0 || flags > (First | Last) || payload.Length == 0)
            return false;
        if (!OnAck(ack, mask, now)) return false;

        // A duplicate still schedules an acknowledgement: the earlier one may be lost.
        ackDue = true;
        if (seq <= delivered || seq - delivered > Window) return true;
        if (seq != delivered + 1)
        {
            if (!parked.ContainsKey(seq))
                parked[seq] = new Parked { Channel = channel, Flags = flags, Payload = payload };
            return true;
        }

        delivered = seq;
        Accept(channel, flags, payload);
        while (!stopped && !Broken && parked.TryGetValue(delivered + 1, out var next))
        {
            parked.Remove(delivered + 1);
            delivered++;
            Accept(next.Channel, next.Flags, next.Payload);
        }
        return !Broken;
    }

    // Body of an Ack frame, positioned after the frame header.
    internal bool OnAckFrame(SyncReader body, double now)
    {
        ulong ack = body.ULong();
        ulong mask = body.ULong();
        if (stopped || Broken || !body.Ok || !body.AtEnd) return false;
        return OnAck(ack, mask, now);
    }

    private void Fill()
    {
        while (flights.Count < Window && backlog.Count > 0)
        {
            var message = backlog.Peek();
            int count = Math.Min(segment, message.Data.Length - cut);
            byte flags = 0;
            if (cut == 0) flags |= First;
            if (cut + count == message.Data.Length) flags |= Last;
            flights.Add(new Flight
            {
                Seq = nextSeq++,
                Channel = message.Channel,
                Flags = flags,
                Data = message.Data,
                Offset = cut,
                Count = count
            });
            cut += count;
            if ((flags & Last) == 0) continue;
            backlog.Dequeue();
            cut = 0;
        }
    }

    private bool OnAck(ulong ack, ulong mask, double now)
    {
        // The peer cannot acknowledge a sequence that was never sent.
        if (ack >= nextSeq)
        {
            Broken = true;
            return false;
        }
        int confirmed = 0;
        while (confirmed < flights.Count && flights[confirmed].Seq <= ack)
        {
            var flight = flights[confirmed++];
            if ((flight.Flags & Last) == 0) continue;
            bufferedBytes -= flight.Data.Length;
            bufferedMessages--;
        }
        if (confirmed > 0) flights.RemoveRange(0, confirmed);
        if (mask == 0 || flights.Count == 0) return true;

        // flights is contiguous by sequence, so a reported sequence maps to an index.
        ulong first = flights[0].Seq;
        int highest = -1;
        for (int bit = 0; bit < 64; bit++)
        {
            if ((mask & (1UL << bit)) == 0) continue;
            ulong seq = ack + 2 + (ulong)bit;
            if (seq < first) continue;
            ulong index = seq - first;
            if (index >= (ulong)flights.Count) break;
            flights[(int)index].Acked = true;
            highest = (int)index;
        }

        // Segments well behind the newest selectively acknowledged one are presumed
        // lost. The age test keeps one hole from being resent on every incoming ack
        // before the peer could possibly have reported the retransmission.
        for (int i = 0; i <= highest - 3; i++)
        {
            var flight = flights[i];
            if (flight.Acked || flight.Lost || flight.Tries == 0) continue;
            if (now - flight.SentAt >= Rto * 0.5) flight.Lost = true;
        }
        return true;
    }

    private void Accept(byte channel, byte flags, byte[] payload)
    {
        if ((flags & First) != 0)
        {
            if (assembling)
            {
                Broken = true;
                return;
            }
            assembling = true;
            assemblingChannel = channel;
            assembledBytes = 0;
            parts.Clear();
        }
        else if (!assembling || channel != assemblingChannel)
        {
            Broken = true;
            return;
        }

        assembledBytes += payload.Length;
        if (assembledBytes > MaxMessage)
        {
            Broken = true;
            return;
        }
        parts.Add(payload);
        if ((flags & Last) == 0) return;

        byte[] message;
        if (parts.Count == 1) message = parts[0];
        else
        {
            message = new byte[assembledBytes];
            int offset = 0;
            foreach (var part in parts)
            {
                Buffer.BlockCopy(part, 0, message, offset, part.Length);
                offset += part.Length;
            }
        }
        assembling = false;
        parts.Clear();
        deliver(channel, message);
    }

    private byte[] Encode(Flight flight)
    {
        var writer = NetWire.Open(NetKind.Segment, link);
        writer.ULong(flight.Seq);
        writer.ULong(delivered);
        writer.ULong(Mask());
        writer.Byte(flight.Channel);
        writer.Byte(flight.Flags);
        writer.Bytes(flight.Data, flight.Offset, flight.Count);
        return writer.ToArray();
    }

    private byte[] EncodeAck()
    {
        var writer = NetWire.Open(NetKind.Ack, link);
        writer.ULong(delivered);
        writer.ULong(Mask());
        return writer.ToArray();
    }

    private ulong Mask()
    {
        if (parked.Count == 0) return 0;
        ulong mask = 0;
        for (int bit = 0; bit < 64; bit++)
            if (parked.ContainsKey(delivered + 2 + (ulong)bit)) mask |= 1UL << bit;
        return mask;
    }

    private sealed class Outgoing
    {
        internal byte Channel;
        internal byte[] Data;
    }

    private sealed class Flight
    {
        internal ulong Seq;
        internal byte Channel;
        internal byte Flags;
        internal byte[] Data;
        internal int Offset;
        internal int Count;
        internal int Tries;
        internal double SentAt;
        internal double Resend;
        internal bool Acked;
        internal bool Lost;
    }

    private struct Parked
    {
        internal byte Channel;
        internal byte Flags;
        internal byte[] Payload;
    }
}
