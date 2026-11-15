using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using ProbablyCoop.Net;
using ProbablyCoop.Sync;

namespace ProbablyCoop.SyncTests;

// Link layer tests. The first half drives two NetLink instances over a simulated network
// with a virtual clock; the second half runs LanTransport over real UDP sockets.
internal static partial class NetTests
{
    private static int failures;

    internal static int Run()
    {
        Wire();
        Handshake();
        Ordering();
        Lossy();
        SmallDatagram();
        Limits();
        Loose();
        Closing();
        Aborting();
        Timeouts();
        Refusals();
        Takeover();
        Corruption();
        Fuzz();
        Console.WriteLine(failures == 0 ? "PASS net-link" : "FAIL net-link failures=" + failures);
        int before = failures;
        LanLoopback();
        LanHmac();
        LanCodes();
        Console.WriteLine(failures == before ? "PASS net-lan" : "FAIL net-lan failures=" + (failures - before));
        before = failures;
        SteamCodes();
        Console.WriteLine(failures == before ? "PASS net-steam-code" : "FAIL net-steam-code failures=" + (failures - before));
        before = failures;
        Sessions();
        Console.WriteLine(failures == before ? "PASS net-session" : "FAIL net-session failures=" + (failures - before));
        before = failures;
        Rooms();
        Console.WriteLine(failures == before ? "PASS net-room" : "FAIL net-room failures=" + (failures - before));
        before = failures;
        Packs();
        Console.WriteLine(failures == before ? "PASS world-pack" : "FAIL world-pack failures=" + (failures - before));
        return failures;
    }

    private static void Check(bool condition, string name)
    {
        if (condition) return;
        failures++;
        Console.WriteLine("FAIL " + name);
    }

    private static bool Same(byte[] a, byte[] b)
    {
        if (a == null || b == null || a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (a[i] != b[i]) return false;
        return true;
    }

    private static byte[] Blob(Random random, int size)
    {
        var bytes = new byte[size];
        random.NextBytes(bytes);
        return bytes;
    }

    private sealed class Packet
    {
        internal bool ToHost;
        internal double At;
        internal long Order;
        internal byte[] Data;
    }

    private sealed class Sim
    {
        internal readonly NetLink Host, Guest;
        internal double Now;
        internal double Loss, Duplicate, Jitter;
        internal double Delay = 0.01;
        internal bool CutToHost, CutToGuest;
        internal int DropToGuest;
        internal int Largest;
        internal Action<bool, byte[]> Tap;
        internal readonly List<(byte Channel, byte[] Data)> HostGot = new List<(byte, byte[])>();
        internal readonly List<(byte Channel, byte[] Data)> GuestGot = new List<(byte, byte[])>();
        internal readonly List<(byte Channel, byte[] Data)> HostLoose = new List<(byte, byte[])>();
        internal readonly List<(byte Channel, byte[] Data)> GuestLoose = new List<(byte, byte[])>();
        internal readonly List<NetClose> HostEnded = new List<NetClose>();
        internal readonly List<NetClose> GuestEnded = new List<NetClose>();
        internal int HostOpened, GuestOpened;
        private readonly Random random;
        private readonly List<Packet> air = new List<Packet>();
        private readonly byte[] scratch = new byte[4096];
        private ulong ids = 1000;
        private long order;

        internal Sim(int seed, int maxDatagram = 1100)
        {
            random = new Random(seed);
            Host = new NetLink((bytes, reliable) => Put(false, bytes), maxDatagram, () => ++ids);
            Guest = new NetLink((bytes, reliable) => Put(true, bytes), maxDatagram, () => ++ids);
            Host.Message = (channel, data) => HostGot.Add((channel, data));
            Guest.Message = (channel, data) => GuestGot.Add((channel, data));
            Host.Datagram = (channel, data) => HostLoose.Add((channel, data));
            Guest.Datagram = (channel, data) => GuestLoose.Add((channel, data));
            Host.Opened = () => HostOpened++;
            Guest.Opened = () => GuestOpened++;
            Host.Ended = reason => HostEnded.Add(reason);
            Guest.Ended = reason => GuestEnded.Add(reason);
        }

        internal static Sim Open(int seed, int maxDatagram = 1100)
        {
            var sim = new Sim(seed, maxDatagram);
            sim.Host.Listen();
            sim.Guest.Connect(sim.Now);
            sim.Run(() => sim.Host.State == NetState.Open && sim.Guest.State == NetState.Open, 5);
            return sim;
        }

        private bool Put(bool toHost, byte[] bytes)
        {
            Largest = Math.Max(Largest, bytes.Length);
            Tap?.Invoke(toHost, bytes);
            if (toHost ? CutToHost : CutToGuest) return true;
            if (!toHost && DropToGuest > 0)
            {
                DropToGuest--;
                return true;
            }
            if (random.NextDouble() < Loss) return true;
            int copies = random.NextDouble() < Duplicate ? 2 : 1;
            for (int i = 0; i < copies; i++)
                air.Add(new Packet
                {
                    ToHost = toHost,
                    At = Now + Delay + random.NextDouble() * Jitter,
                    Order = order++,
                    Data = (byte[])bytes.Clone()
                });
            return true;
        }

        // Hands a datagram over in a reused buffer and scribbles on it afterwards, the
        // way a transport with one receive buffer behaves.
        internal bool Deliver(NetLink link, byte[] data)
        {
            Buffer.BlockCopy(data, 0, scratch, 0, data.Length);
            bool valid = link.Receive(scratch, data.Length, Now);
            for (int i = 0; i < data.Length + 8 && i < scratch.Length; i++) scratch[i] = 0xEE;
            return valid;
        }

        internal void Step(double seconds = 0.01)
        {
            Now += seconds;
            List<Packet> due = air.FindAll(packet => packet.At <= Now);
            if (due.Count > 0)
            {
                air.RemoveAll(packet => packet.At <= Now);
                due.Sort((a, b) => a.At != b.At ? a.At.CompareTo(b.At) : a.Order.CompareTo(b.Order));
                foreach (Packet packet in due) Deliver(packet.ToHost ? Host : Guest, packet.Data);
            }
            Host.Tick(Now);
            Guest.Tick(Now);
        }

        internal bool Run(Func<bool> done, double limit = 60)
        {
            double end = Now + limit;
            while (Now < end)
            {
                if (done()) return true;
                Step();
            }
            return done();
        }
    }

    private static void Wire()
    {
        byte[] frame = NetWire.Open(NetKind.Ping, 300).ToArray();
        Check(NetWire.TryOpen(frame, frame.Length, out NetKind kind, out ulong link, out SyncReader body) &&
              kind == NetKind.Ping && link == 300 && body.AtEnd, "wire-roundtrip");
        Check(!NetWire.TryOpen(frame, 3, out _, out _, out _), "wire-short");
        Check(!NetWire.TryOpen(null, 0, out _, out _, out _), "wire-null");
        byte[] bad = (byte[])frame.Clone();
        bad[3] ^= 1;
        Check(!NetWire.TryOpen(bad, bad.Length, out _, out _, out _), "wire-magic");
        // Only the declared count is read, not the rest of the buffer.
        var padded = new byte[frame.Length + 5];
        Buffer.BlockCopy(frame, 0, padded, 0, frame.Length);
        Check(NetWire.TryOpen(padded, frame.Length, out _, out _, out body) && body.AtEnd, "wire-count-bounds");
        Check(NetWire.Agree(NetWire.Oldest, NetWire.Protocol) == NetWire.Protocol, "agree-same");
        Check(NetWire.Agree(NetWire.Protocol + 1, NetWire.Protocol + 9) == 0, "agree-too-new");
        Check(NetWire.Agree(NetWire.Oldest, NetWire.Protocol + 9) == NetWire.Protocol, "agree-newer-peer");
        Check(NetWire.Agree(5, 4) == 0, "agree-inverted-range");
    }

    private static void Handshake()
    {
        Sim clean = Sim.Open(1);
        Check(clean.Host.State == NetState.Open && clean.Guest.State == NetState.Open, "handshake-open");
        Check(clean.Host.Link != 0 && clean.Host.Link == clean.Guest.Link, "handshake-same-link");
        Check(clean.Host.Protocol == NetWire.Protocol && clean.Guest.Protocol == NetWire.Protocol, "handshake-protocol");
        Check(clean.Host.IsHost && !clean.Guest.IsHost, "handshake-roles");
        Check(clean.HostOpened == 1 && clean.GuestOpened == 1, "handshake-opened-once");

        // The first Welcomes are lost: the guest keeps saying Hello and the host answers
        // again without opening a second connection.
        var lost = new Sim(2) { DropToGuest = 3 };
        lost.Host.Listen();
        lost.Guest.Connect(lost.Now);
        Check(lost.Run(() => lost.Guest.State == NetState.Open, 10), "handshake-lost-welcome-opens");
        Check(lost.HostOpened == 1 && lost.GuestOpened == 1 && lost.Host.Link == lost.Guest.Link,
            "handshake-lost-welcome-single");
        lost.Host.Send(1, new byte[] { 5 });
        Check(lost.Run(() => lost.GuestGot.Count == 1, 5), "handshake-lost-welcome-usable");

        clean.Run(() => clean.Host.Rtt > 0 && clean.Guest.Rtt > 0, 5);
        Check(clean.Host.Rtt > 0.015 && clean.Host.Rtt < 0.06, "rtt-measured");
    }

    private static void Ordering()
    {
        Sim sim = Sim.Open(3);
        // Channels share one sequence space: relative order across channels is kept.
        for (int i = 0; i < 300; i++)
            Check(sim.Host.Send((byte)(i % 3), new[] { (byte)i, (byte)(i >> 8) }), "order-send-" + i);
        Check(sim.Run(() => sim.GuestGot.Count == 300, 10), "order-all-arrive");
        bool ordered = sim.GuestGot.Count == 300;
        for (int i = 0; ordered && i < 300; i++)
            ordered = sim.GuestGot[i].Channel == i % 3 && sim.GuestGot[i].Data[0] == (byte)i &&
                      sim.GuestGot[i].Data[1] == (byte)(i >> 8);
        Check(ordered, "order-across-channels");

        // More messages than the send window in one frame.
        var random = new Random(30);
        var sent = new List<byte[]>();
        for (int i = 0; i < 3000; i++)
        {
            byte[] message = Blob(random, 1 + i % 7);
            sent.Add(message);
            sim.Guest.Send(9, message);
        }
        Check(sim.Guest.Queued == 3000 && !sim.Guest.Flushed, "window-queued");
        Check(sim.Run(() => sim.HostGot.Count == 3000 && sim.Guest.Flushed, 30), "window-drains");
        bool intact = sim.HostGot.Count == 3000;
        for (int i = 0; intact && i < 3000; i++) intact = Same(sim.HostGot[i].Data, sent[i]);
        Check(intact, "window-in-order");
        Check(sim.Guest.Queued == 0 && sim.Guest.Resent == 0, "window-no-spurious-resend");
    }

    private static void Lossy()
    {
        Sim sim = Sim.Open(4);
        sim.Loss = 0.2;
        sim.Duplicate = 0.1;
        sim.Jitter = 0.05;
        var random = new Random(40);
        var down = new List<byte[]>();
        var up = new List<byte[]>();
        byte[] big = Blob(random, 300 * 1024);
        down.Add(big);
        sim.Host.Send(1, big);
        for (int i = 0; i < 400; i++)
        {
            byte[] a = Blob(random, 1 + random.Next(5000));
            byte[] b = Blob(random, 1 + random.Next(5000));
            down.Add(a);
            up.Add(b);
            sim.Host.Send((byte)(i & 3), a);
            sim.Guest.Send((byte)(i & 3), b);
        }
        bool done = sim.Run(() => sim.GuestGot.Count >= down.Count && sim.HostGot.Count >= up.Count &&
                                  sim.Host.Flushed && sim.Guest.Flushed, 600);
        Check(done, "lossy-completes got=" + sim.GuestGot.Count + "/" + down.Count + " " + sim.HostGot.Count + "/" +
                    up.Count + " hostState=" + sim.Host.State + " guestState=" + sim.Guest.State);
        bool exact = sim.GuestGot.Count == down.Count && sim.HostGot.Count == up.Count;
        for (int i = 0; exact && i < down.Count; i++) exact = Same(sim.GuestGot[i].Data, down[i]);
        for (int i = 0; exact && i < up.Count; i++) exact = Same(sim.HostGot[i].Data, up[i]);
        Check(exact, "lossy-exactly-once-in-order");
        Check(sim.Host.Resent > 0 && sim.Guest.Resent > 0, "lossy-retransmits");
        Check(sim.Host.State == NetState.Open && sim.Guest.State == NetState.Open, "lossy-stays-open");
        Check(sim.Largest <= 1100, "lossy-frame-fits largest=" + sim.Largest);
        Console.WriteLine("info lossy sim-seconds=" + sim.Now.ToString("0.0") + " resent=" + sim.Host.Resent + "/" +
                          sim.Guest.Resent + " largest-frame=" + sim.Largest);
    }

    private static void SmallDatagram()
    {
        int smallest = NetStream.Overhead + 64;
        bool threw = false;
        try { _ = new NetLink((bytes, reliable) => true, smallest - 1); }
        catch (ArgumentOutOfRangeException) { threw = true; }
        Check(threw, "small-datagram-rejected");

        Sim sim = Sim.Open(5, smallest);
        byte[] message = Blob(new Random(50), 10000);
        sim.Host.Send(2, message);
        Check(sim.Run(() => sim.GuestGot.Count == 1, 20) && Same(sim.GuestGot[0].Data, message), "small-datagram-fragments");
        Check(sim.Largest <= smallest, "small-datagram-frame-fits largest=" + sim.Largest);
        Check(sim.Host.SendLoose(1, new byte[sim.Host.LooseLimit]), "small-datagram-loose-max");
        Check(sim.Largest <= smallest, "small-datagram-loose-fits largest=" + sim.Largest);
    }

    private static void Limits()
    {
        var idle = new NetLink((bytes, reliable) => true, 1100);
        Check(!idle.Send(0, new byte[] { 1 }) && !idle.SendLoose(0, new byte[] { 1 }), "send-before-open");
        Check(idle.Flushed && idle.Queued == 0, "idle-counters");
        Check(!idle.Receive(NetWire.Open(NetKind.Ping, 1).ToArray(), 6, 0), "idle-ignores-frames");

        Sim sim = Sim.Open(6);
        Check(!sim.Host.Send(0, null) && !sim.Host.Send(0, new byte[0]), "send-empty-rejected");
        Check(!sim.Host.Send(0, new byte[NetStream.MaxMessage + 1]), "send-oversize-rejected");
        Check(sim.Host.Queued == 0, "rejected-not-queued");
        Check(!sim.Host.SendLoose(0, null) && !sim.Host.SendLoose(0, new byte[0]) &&
              !sim.Host.SendLoose(0, new byte[sim.Host.LooseLimit + 1]), "loose-limits");
        Check(sim.Host.SendLoose(0, new byte[sim.Host.LooseLimit]) && sim.Largest <= 1100,
            "loose-max-fits largest=" + sim.Largest);
    }

    private static void Loose()
    {
        Sim sim = Sim.Open(7);
        var captured = new List<byte[]>();
        sim.CutToGuest = true;
        sim.Tap = (toHost, bytes) =>
        {
            if (!toHost && bytes[4] == (byte)NetKind.Datagram) captured.Add(bytes);
        };
        sim.Host.SendLoose(3, new byte[] { 1 });
        sim.Host.SendLoose(3, new byte[] { 2 });
        sim.Host.SendLoose(3, new byte[] { 3 });
        sim.Host.SendLoose(4, new byte[] { 9 });
        sim.Tap = null;
        sim.CutToGuest = false;
        Check(captured.Count == 4, "loose-captured");
        if (captured.Count != 4) return;
        // Newest first, then the stale ones and a duplicate.
        Check(sim.Deliver(sim.Guest, captured[2]), "loose-newest-accepted");
        Check(sim.Deliver(sim.Guest, captured[0]) && sim.Deliver(sim.Guest, captured[1]) &&
              sim.Deliver(sim.Guest, captured[2]), "loose-stale-is-valid-frame");
        Check(sim.GuestLoose.Count == 1 && sim.GuestLoose[0].Channel == 3 && sim.GuestLoose[0].Data[0] == 3,
            "loose-latest-wins");
        sim.Deliver(sim.Guest, captured[3]);
        Check(sim.GuestLoose.Count == 2 && sim.GuestLoose[1].Channel == 4, "loose-channels-independent");
        // The reliable stream is unaffected by loose traffic.
        sim.Host.Send(3, new byte[] { 7 });
        Check(sim.Run(() => sim.GuestGot.Count == 1, 5), "loose-beside-reliable");
    }

    private static void Closing()
    {
        Sim sim = Sim.Open(8);
        sim.Guest.Close();
        Check(sim.Guest.State == NetState.Closed && sim.Guest.Reason == NetClose.Local &&
              sim.GuestEnded.Count == 1 && sim.GuestEnded[0] == NetClose.Local, "close-local");
        Check(sim.Run(() => sim.Host.State == NetState.Closed, 2) && sim.Host.Reason == NetClose.Remote &&
              sim.HostEnded.Count == 1, "close-reaches-peer");
        Check(!sim.Guest.Send(0, new byte[] { 1 }), "closed-send-rejected");
        sim.Guest.Close();
        Check(sim.GuestEnded.Count == 1, "close-twice-single-event");

        // Before the connection opened.
        var early = new Sim(9);
        early.Guest.Connect(0);
        early.Guest.Close();
        early.Host.Listen();
        early.Host.Close();
        Check(early.GuestEnded.Count == 1 && early.HostEnded.Count == 1 && early.Guest.State == NetState.Closed,
            "close-before-open-raises-ended");

        // A handler closes the link in the middle of a delivery batch.
        Sim reentrant = Sim.Open(10);
        int seen = 0;
        reentrant.Guest.Message = (channel, data) =>
        {
            seen++;
            reentrant.Guest.Close();
        };
        bool threw = false;
        try
        {
            for (int i = 0; i < 5; i++) reentrant.Host.Send(0, new byte[] { (byte)i });
            reentrant.Run(() => reentrant.Host.State == NetState.Closed, 3);
        }
        catch (Exception) { threw = true; }
        Check(!threw && seen == 1 && reentrant.Guest.Reason == NetClose.Local, "close-inside-handler seen=" + seen);

        // A closed link can be reused for a new connection.
        sim.Host.Listen();
        sim.Guest.Connect(sim.Now);
        Check(sim.Run(() => sim.Host.State == NetState.Open && sim.Guest.State == NetState.Open, 5), "reopen");
        sim.Host.Send(0, new byte[] { 4 });
        Check(sim.Run(() => sim.GuestGot.Count == 1, 5) && sim.GuestGot[0].Data[0] == 4, "reopen-fresh-stream");
    }

    private static void Timeouts()
    {
        Sim sim = Sim.Open(11);
        sim.Run(() => false, 5);
        Check(sim.Host.State == NetState.Open && sim.Guest.State == NetState.Open, "idle-link-kept-alive");
        sim.CutToHost = sim.CutToGuest = true;
        sim.Run(() => false, 11);
        Check(sim.Host.State == NetState.Open, "silence-not-early");
        sim.Run(() => false, 2);
        Check(sim.Host.State == NetState.Closed && sim.Host.Reason == NetClose.Timeout &&
              sim.Guest.State == NetState.Closed && sim.Guest.Reason == NetClose.Timeout, "silence-timeout");

        var alone = new Sim(12) { CutToHost = true };
        alone.Guest.Connect(alone.Now);
        alone.Run(() => false, 19);
        Check(alone.Guest.State == NetState.Connecting, "connect-keeps-trying");
        alone.Run(() => false, 2);
        Check(alone.Guest.State == NetState.Closed && alone.Guest.Reason == NetClose.Timeout, "connect-timeout");
    }

    private static void Refusals()
    {
        // Host side: a guest whose protocol range does not overlap.
        byte[] reply = null;
        var host = new NetLink((bytes, reliable) => { reply = bytes; return true; }, 1100);
        host.Listen();
        SyncWriter hello = NetWire.Open(NetKind.Hello, 0);
        hello.ULong(77);
        hello.UInt(NetWire.Protocol + 5);
        hello.UInt(NetWire.Protocol + 9);
        byte[] frame = hello.ToArray();
        Check(!host.Receive(frame, frame.Length, 0) && host.State == NetState.Listening, "refuse-host-stays-listening");
        Check(reply != null && NetWire.TryOpen(reply, reply.Length, out NetKind kind, out ulong link, out SyncReader body) &&
              kind == NetKind.Refuse && link == 0 && body.ULong() == 77 && body.Byte() == (byte)NetRefusal.Protocol &&
              body.AtEnd, "refuse-frame");

        // Guest side.
        NetLink Guest(out List<NetClose> ended)
        {
            var list = new List<NetClose>();
            var guest = new NetLink((bytes, reliable) => true, 1100, () => 4242) { Ended = list.Add };
            guest.Connect(0);
            ended = list;
            return guest;
        }
        byte[] Refuse(ulong echo, NetRefusal reason)
        {
            SyncWriter writer = NetWire.Open(NetKind.Refuse, 0);
            writer.ULong(echo);
            writer.Byte((byte)reason);
            return writer.ToArray();
        }
        byte[] Welcome(ulong id, ulong echo, uint protocol)
        {
            SyncWriter writer = NetWire.Open(NetKind.Welcome, id);
            writer.ULong(echo);
            writer.UInt(protocol);
            return writer.ToArray();
        }

        NetLink guest = Guest(out List<NetClose> ended);
        frame = Refuse(1, NetRefusal.Protocol);
        guest.Receive(frame, frame.Length, 0.1);
        Check(guest.State == NetState.Connecting, "refuse-wrong-nonce-ignored");
        frame = Refuse(4242, NetRefusal.Busy);
        guest.Receive(frame, frame.Length, 0.1);
        Check(guest.State == NetState.Connecting, "refuse-busy-keeps-trying");
        frame = Refuse(4242, NetRefusal.Protocol);
        guest.Receive(frame, frame.Length, 0.1);
        Check(guest.State == NetState.Closed && guest.Reason == NetClose.Refused &&
              guest.Refusal == NetRefusal.Protocol && ended.Count == 1, "refuse-protocol-ends");

        guest = Guest(out ended);
        frame = Welcome(9, 1, NetWire.Protocol);
        Check(!guest.Receive(frame, frame.Length, 0.1) && guest.State == NetState.Connecting, "welcome-wrong-nonce");
        frame = Welcome(0, 4242, NetWire.Protocol);
        Check(!guest.Receive(frame, frame.Length, 0.1) && guest.State == NetState.Connecting, "welcome-zero-link");
        frame = Welcome(9, 4242, NetWire.Protocol + 1);
        guest.Receive(frame, frame.Length, 0.1);
        Check(guest.State == NetState.Closed && guest.Reason == NetClose.Refused, "welcome-unknown-protocol");

        guest = Guest(out ended);
        frame = Welcome(9, 4242, NetWire.Protocol);
        Check(guest.Receive(frame, frame.Length, 0.1) && guest.State == NetState.Open && guest.Link == 9, "welcome-opens");
        // A guest never accepts a Hello, and a host never accepts a Welcome.
        Check(!guest.Receive(hello.ToArray(), hello.Length, 0.2), "guest-ignores-hello");
        host.Listen();
        Check(!host.Receive(frame, frame.Length, 0.2) && host.State == NetState.Listening, "host-ignores-welcome");
    }

    private static void Takeover()
    {
        // While the first guest is alive a second one cannot push it out.
        Sim busy = Sim.Open(13);
        var outbox = new List<byte[]>();
        var second = new NetLink((bytes, reliable) => { outbox.Add(bytes); return true; }, 1100, () => 555);
        busy.Tap = (toHost, bytes) =>
        {
            if (!toHost) second.Receive((byte[])bytes.Clone(), bytes.Length, busy.Now);
        };
        void Shuttle(Sim sim)
        {
            second.Tick(sim.Now);
            foreach (byte[] bytes in outbox.ToArray()) sim.Deliver(sim.Host, bytes);
            outbox.Clear();
        }
        second.Connect(busy.Now);
        busy.Run(() => { Shuttle(busy); return false; }, 21);
        Check(second.State == NetState.Closed && second.Reason == NetClose.Timeout, "takeover-busy-times-out");
        Check(busy.Host.State == NetState.Open && busy.Guest.State == NetState.Open && busy.HostEnded.Count == 0,
            "takeover-live-guest-kept");
        busy.Host.Send(0, new byte[] { 1 });
        Check(busy.Run(() => busy.GuestGot.Count == 1, 5), "takeover-live-link-works");

        // The first guest vanished: after the quiet period a new Hello ends the old
        // connection and the next one starts a clean one.
        Sim gone = Sim.Open(14);
        outbox.Clear();
        second = new NetLink((bytes, reliable) => { outbox.Add(bytes); return true; }, 1100, () => 556);
        gone.Tap = (toHost, bytes) =>
        {
            if (!toHost) second.Receive((byte[])bytes.Clone(), bytes.Length, gone.Now);
        };
        gone.CutToHost = true;
        second.Connect(gone.Now);
        gone.Run(() => { Shuttle(gone); return false; }, 2);
        Check(gone.Host.State == NetState.Open && second.State == NetState.Connecting, "takeover-waits-for-silence");
        Check(gone.Run(() => { Shuttle(gone); return gone.Host.State == NetState.Closed; }, 3) &&
              gone.Host.Reason == NetClose.Remote, "takeover-ends-old");
        ulong old = gone.Guest.Link;
        gone.Host.Listen();
        Check(gone.Run(() => { Shuttle(gone); return second.State == NetState.Open; }, 3) &&
              gone.Host.State == NetState.Open && gone.Host.Link == second.Link && second.Link != old,
            "takeover-new-connection");
        Check(gone.HostOpened == 2, "takeover-opened-again");
    }

    private static void Corruption()
    {
        Sim sim = Sim.Open(15);
        SyncWriter ping = NetWire.Open(NetKind.Ping, sim.Host.Link + 1);
        ping.ULong(1);
        Check(!sim.Deliver(sim.Host, ping.ToArray()) && sim.Host.State == NetState.Open, "wrong-link-ignored");
        SyncWriter trailing = NetWire.Open(NetKind.Bye, sim.Host.Link);
        trailing.Byte(1);
        Check(!sim.Deliver(sim.Host, trailing.ToArray()) && sim.Host.State == NetState.Open, "bye-with-payload-ignored");

        // Acknowledging a sequence that was never sent.
        SyncWriter ack = NetWire.Open(NetKind.Ack, sim.Host.Link);
        ack.ULong(1000);
        ack.ULong(0);
        sim.Deliver(sim.Host, ack.ToArray());
        Check(sim.Host.State == NetState.Closed && sim.Host.Reason == NetClose.Corrupt &&
              sim.HostEnded.Count == 1 && sim.HostEnded[0] == NetClose.Corrupt, "impossible-ack-closes");

        // A continuation segment without a first segment.
        sim = Sim.Open(16);
        SyncWriter segment = NetWire.Open(NetKind.Segment, sim.Guest.Link);
        segment.ULong(1);
        segment.ULong(0);
        segment.ULong(0);
        segment.Byte(0);
        segment.Byte(0);
        segment.Bytes(new byte[] { 1, 2, 3 });
        sim.Deliver(sim.Guest, segment.ToArray());
        Check(sim.Guest.State == NetState.Closed && sim.Guest.Reason == NetClose.Corrupt && sim.GuestGot.Count == 0,
            "orphan-segment-closes");
    }

    private static void Fuzz()
    {
        var random = new Random(17);
        string error = null;
        try
        {
            for (int round = 0; round < 40 && error == null; round++)
            {
                Sim sim = Sim.Open(100 + round);
                var frames = new List<(bool ToHost, byte[] Data)>();
                sim.Tap = (toHost, bytes) => frames.Add((toHost, (byte[])bytes.Clone()));
                for (int i = 0; i < 20; i++)
                {
                    sim.Host.Send((byte)i, Blob(random, 1 + random.Next(3000)));
                    sim.Guest.Send((byte)i, Blob(random, 1 + random.Next(3000)));
                    sim.Host.SendLoose(1, Blob(random, 1 + random.Next(200)));
                }
                sim.Run(() => sim.Host.Flushed && sim.Guest.Flushed, 5);
                sim.Tap = null;
                for (int i = 0; i < 500; i++)
                {
                    // Pure noise, then real frames with one byte changed or cut short.
                    byte[] noise = Blob(random, random.Next(80));
                    sim.Deliver(random.Next(2) == 0 ? sim.Host : sim.Guest, noise);
                    (bool toHost, byte[] data) = frames[random.Next(frames.Count)];
                    byte[] mutated = (byte[])data.Clone();
                    if (random.Next(3) == 0) Array.Resize(ref mutated, random.Next(mutated.Length + 1));
                    else mutated[random.Next(mutated.Length)] ^= (byte)(1 << random.Next(8));
                    sim.Deliver(toHost ? sim.Host : sim.Guest, mutated);
                    if (i % 10 == 0) sim.Step();
                }
            }
        }
        catch (Exception exception) { error = exception.ToString(); }
        Check(error == null, "fuzz-never-throws " + error);
    }

    // ---- real sockets ----

    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    private static double Now => Clock.Elapsed.TotalSeconds;

    private static bool Spin(Func<bool> done, double limit, params LanTransport[] transports)
    {
        double end = Now + limit;
        while (true)
        {
            foreach (LanTransport transport in transports)
            {
                transport.Poll(Now);
                transport.Flush(Now);
            }
            if (done()) return true;
            if (Now >= end) return false;
            Thread.Sleep(1);
        }
    }

    private static string Forge(string code)
    {
        int cut = code.LastIndexOf('-');
        return code.Substring(0, cut + 1) + Guid.NewGuid().ToString("N");
    }

    private static void Exchange(string name, LanTransport host, LanTransport guest)
    {
        var hostGot = new List<byte[]>();
        var guestGot = new List<byte[]>();
        var guestLoose = new List<byte[]>();
        host.Link.Message = (channel, data) => hostGot.Add(data);
        guest.Link.Message = (channel, data) => guestGot.Add(data);
        guest.Link.Datagram = (channel, data) => guestLoose.Add(data);
        Check(Spin(() => host.Link.State == NetState.Open && guest.Link.State == NetState.Open, 5, host, guest),
            name + "-opens host=" + host.Link.State + " guest=" + guest.Link.State);
        var random = new Random(name.Length);
        byte[] big = Blob(random, 200 * 1024);
        byte[] small = Blob(random, 40);
        host.Link.Send(1, big);
        host.Link.Send(2, small);
        guest.Link.Send(1, small);
        guest.Link.Send(2, big);
        Check(Spin(() => guestGot.Count == 2 && hostGot.Count == 2, 20, host, guest),
            name + "-delivers " + guestGot.Count + "/" + hostGot.Count);
        Check(guestGot.Count == 2 && Same(guestGot[0], big) && Same(guestGot[1], small) &&
              hostGot.Count == 2 && Same(hostGot[0], small) && Same(hostGot[1], big), name + "-content");
        host.Link.SendLoose(5, new byte[] { 42 });
        Check(Spin(() => guestLoose.Count == 1, 5, host, guest) && guestLoose[0][0] == 42, name + "-loose");
    }

    private static void LanLoopback()
    {
        using var host = new LanTransport(Now);
        Check(host.IsHost && !host.Lan && host.Code.StartsWith("PSL-", StringComparison.Ordinal) &&
              LanTransport.IsCode(host.Code) && host.Link.State == NetState.Listening, "loopback-host");

        // Right address and port, wrong room token.
        using (var stranger = new LanTransport(Now, Forge(host.Code)))
        {
            Spin(() => false, 1.2, host, stranger);
            Check(stranger.Sent >= 2 && host.Received == 0 && host.Sent == 0 &&
                  host.Link.State == NetState.Listening && stranger.Link.State == NetState.Connecting,
                "loopback-wrong-token-ignored sent=" + stranger.Sent + " received=" + host.Received);
        }

        var guest = new LanTransport(Now, host.Code);
        Check(!guest.IsHost && !guest.Lan && guest.Code == host.Code, "loopback-guest");
        Exchange("loopback", host, guest);

        // Holding the room code is not enough to inject frames into an open connection:
        // a frame from any endpoint other than the connected guest is dropped, even
        // with the right tag and link id.
        string[] parts = host.Code.Split('-');
        byte[] bye = NetWire.Open(NetKind.Bye, host.Link.Link).ToArray();
        var forged = new byte[16 + bye.Length];
        Buffer.BlockCopy(Guid.ParseExact(parts[2], "N").ToByteArray(), 0, forged, 0, 16);
        Buffer.BlockCopy(bye, 0, forged, 16, bye.Length);
        using (var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
        {
            socket.Send(forged, forged.Length, new IPEndPoint(IPAddress.Loopback, int.Parse(parts[1])));
            Spin(() => false, 0.3, host, guest);
        }
        Check(host.Link.State == NetState.Open && guest.Link.State == NetState.Open,
            "loopback-foreign-endpoint-ignored host=" + host.Link.State);

        // A second client cannot disturb the open connection.
        using (var intruder = new LanTransport(Now, host.Code))
        {
            Spin(() => false, 1.2, host, guest, intruder);
            Check(intruder.Link.State == NetState.Connecting && host.Link.State == NetState.Open &&
                  guest.Link.State == NetState.Open, "loopback-busy-room");
        }

        // The guest leaves; the room accepts the next one.
        guest.Dispose();
        Check(Spin(() => host.Link.State == NetState.Listening, 5, host), "loopback-relistens state=" + host.Link.State);
        using var again = new LanTransport(Now, host.Code);
        Exchange("loopback-rejoin", host, again);
    }

    private static IPAddress LocalAddress()
    {
        foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up ||
                adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (UnicastIPAddressInformation entry in adapter.GetIPProperties().UnicastAddresses)
            {
                IPAddress address = entry.Address;
                if (address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address) &&
                    !address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                    return address;
            }
        }
        return null;
    }

    private static void LanHmac()
    {
        IPAddress address = LocalAddress();
        if (address == null)
        {
            Console.WriteLine("SKIP net-lan-hmac: this machine has no IPv4 adapter besides loopback");
            return;
        }
        int fixedPort;
        using (var reservation = new UdpClient(new IPEndPoint(address, 0)))
            fixedPort = ((IPEndPoint)reservation.Client.LocalEndPoint).Port;
        using var host = new LanTransport(Now, bindAddress: address, listenPort: fixedPort);
        Check(host.Lan && host.Code.StartsWith("PSI-" + address + "-", StringComparison.Ordinal), "lan-host-code");
        Check(host.LocalEndpoint.Equals(new IPEndPoint(address, fixedPort)), "lan-fixed-port");
        using (var occupied = new LanCarrier())
        {
            occupied.Host(Now, address.ToString(), fixedPort.ToString());
            Check(!occupied.HasLobby && occupied.Status == "udp_port_in_use", "lan-occupied-port");
        }

        // The loopback rule (tag = raw token) must not be accepted in LAN mode.
        string[] parts = host.Code.Split('-');
        int port = int.Parse(parts[2]);
        byte[] token = Guid.ParseExact(parts[3], "N").ToByteArray();
        SyncWriter hello = NetWire.Open(NetKind.Hello, 0);
        hello.ULong(99);
        hello.UInt(NetWire.Oldest);
        hello.UInt(NetWire.Protocol);
        byte[] frame = hello.ToArray();
        var raw = new byte[16 + frame.Length];
        Buffer.BlockCopy(token, 0, raw, 0, 16);
        Buffer.BlockCopy(frame, 0, raw, 16, frame.Length);
        using (var socket = new UdpClient(new IPEndPoint(address, 0)))
        {
            socket.Send(raw, raw.Length, new IPEndPoint(address, port));
            Spin(() => false, 0.5, host);
        }
        Check(host.Received == 0 && host.Link.State == NetState.Listening, "lan-raw-token-rejected");

        using (var stranger = new LanTransport(Now, Forge(host.Code)))
        {
            Spin(() => false, 1.2, host, stranger);
            Check(stranger.Sent >= 2 && host.Received == 0 && host.Link.State == NetState.Listening,
                "lan-wrong-token-ignored");
        }

        string remappedCode = $"PSI-192.0.2.1-{(port == 1 ? 2 : 1)}-{parts[3]}";
        using var guest = new LanTransport(Now, remappedCode, mappedEndpoint: host.LocalEndpoint);
        Check(guest.Lan && guest.Code == remappedCode, "lan-guest-mapped-endpoint");
        Exchange("lan", host, guest);
    }

    // The carrier ends a link whose peer it knows to be gone.
    private static void Aborting()
    {
        Sim sim = Sim.Open(11);
        sim.Host.Abort(NetClose.Remote);
        Check(sim.Host.State == NetState.Closed && sim.Host.Reason == NetClose.Remote &&
              sim.HostEnded.Count == 1 && sim.HostEnded[0] == NetClose.Remote, "abort-ends-with-reason");
        sim.Host.Abort(NetClose.Timeout);
        Check(sim.HostEnded.Count == 1 && sim.Host.Reason == NetClose.Remote, "abort-twice-single-event");
        // Nothing was sent: the other side only notices through silence.
        Check(!sim.Run(() => sim.Guest.State == NetState.Closed, 2), "abort-sends-no-bye");
        Check(sim.Run(() => sim.Guest.State == NetState.Closed, 15) && sim.Guest.Reason == NetClose.Timeout,
            "abort-peer-times-out");

        var early = new Sim(12);
        early.Guest.Abort(NetClose.Remote);
        Check(early.GuestEnded.Count == 0 && early.Guest.State == NetState.Idle, "abort-idle-ignored");
        early.Guest.Connect(0);
        early.Guest.Abort(NetClose.Timeout);
        Check(early.GuestEnded.Count == 1 && early.Guest.State == NetState.Closed &&
              early.Guest.Reason == NetClose.Timeout, "abort-while-connecting");
        early.Host.Listen();
        early.Guest.Connect(early.Now);
        Check(early.Run(() => early.Host.State == NetState.Open && early.Guest.State == NetState.Open, 5),
            "abort-then-reconnect");
    }

    // The Steam room code keeps the 0.1.0 format, checked against the old implementation.
    private static void SteamCodes()
    {
        var random = new Random(21);
        var ids = new List<ulong> { 1, 35, 36, 1295, 109775241234567890UL, ulong.MaxValue - 1, ulong.MaxValue };
        var raw = new byte[8];
        for (int i = 0; i < 2000; i++)
        {
            random.NextBytes(raw);
            ulong id = BitConverter.ToUInt64(raw, 0);
            if (id != 0) ids.Add(id);
        }
        int mismatched = 0, typosAccepted = 0, typos = 0;
        foreach (ulong id in ids)
        {
            string code = SteamCode.Encode(id);
            if (code != ProbablyCoop.Core.RoomCode.Encode(id) || code.Length > 22 ||
                !SteamCode.TryDecode(code, out ulong back) || back != id ||
                !ProbablyCoop.Core.RoomCode.TryDecode(code, out ulong old) || old != id) mismatched++;
            // One mistyped character of the lobby part.
            int at = 4 + random.Next(code.Length - 9);
            char wrong = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ"[random.Next(36)];
            if (wrong == code[at]) continue;
            typos++;
            if (SteamCode.TryDecode(code.Substring(0, at) + wrong + code.Substring(at + 1), out _)) typosAccepted++;
        }
        Check(mismatched == 0, "steam-code-matches-old-format mismatched=" + mismatched);
        Check(typos > 1500 && typosAccepted == 0, "steam-code-typos-rejected " + typosAccepted + "/" + typos);
        Check(SteamCode.Encode(1) == "PS1-1-" + SteamCode.Encode(1).Substring(6) && SteamCode.Encode(36).StartsWith("PS1-10-"),
            "steam-code-shape");

        string good = SteamCode.Encode(109775241234567890UL);
        Check(SteamCode.TryDecode("  " + good.ToLowerInvariant() + " ", out ulong relaxed) &&
              relaxed == 109775241234567890UL, "steam-code-case-and-spaces");
        string tail = good.Substring(good.Length - 4);
        string flipped = (tail[0] == '0' ? "1" : "0") + tail.Substring(1);
        string[] bad =
        {
            null, "", "PS1", "PS1--" + tail, "PS1-0-0000", "PS2" + good.Substring(3),
            good.Substring(0, good.Length - 4) + flipped, good.Substring(0, good.Length - 1), good + "0",
            good + "-X", "PS1-ZZZZZZZZZZZZZ-0000", "PS1-11111111111111-0000", "PS1-AB_C-0000", "PS1-ABC-12G4",
            "PSL-1234-" + Guid.NewGuid().ToString("N"), good + new string(' ', 60)
        };
        foreach (string code in bad)
            Check(!SteamCode.TryDecode(code, out ulong none) && none == 0, "steam-code-rejected '" + code + "'");
        bool threw = false;
        try { SteamCode.Encode(0); }
        catch (ArgumentOutOfRangeException) { threw = true; }
        Check(threw, "steam-code-zero-rejected");
        Check(!LanTransport.IsCode(good), "steam-code-is-not-direct-code");
    }

    private static void LanCodes()
    {
        string token = Guid.NewGuid().ToString("N");
        string[] bad =
        {
            "", "PSL", "PSL-0-" + token, "PSL-70000-" + token, "PSL-+123-" + token, "PSL-123-" + token.Substring(1),
            "PSL-123-" + Guid.Empty.ToString("N"), "PSL-123-" + token + "-x", "psl-123-" + token,
            "PSI-127.0.0.1-123-" + token, "PSI-224.0.0.1-123-" + token, "PSI-0.1.2.3-123-" + token,
            "PSI-192.168.001.5-123-" + token, "PSI-::1-123-" + token, "PSI-example.com-123-" + token,
            "PS1-ABC-1234", "PSI-192.168.1.5-123-" + token + new string('0', 40)
        };
        foreach (string code in bad)
        {
            bool threw = false;
            try { new LanTransport(0, code).Dispose(); }
            catch (FormatException) { threw = true; }
            Check(threw, "code-rejected '" + code + "'");
        }
        bool bind = false;
        try { new LanTransport(0, null, IPAddress.IPv6Loopback).Dispose(); }
        catch (FormatException) { bind = true; }
        Check(bind, "host-ipv6-rejected");
        Check(LanTransport.IsCode("PSL-1-x") && LanTransport.IsCode("PSI-1-x") && !LanTransport.IsCode("PS1-ABC-1234") &&
              !LanTransport.IsCode(null), "is-code");
    }
}
