using System.Net;
using System.Net.Sockets;
using ProbablyCoop.Core;

internal static class QaLanRelayTests
{
    internal static void Run()
    {
        using var host = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var firstGuest = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var secondGuest = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var relay = new ProbablyCoopQaLanRelay("127.0.0.1", ((IPEndPoint)host.Client.LocalEndPoint).Port, false, true);
        var relayEndpoint = new IPEndPoint(IPAddress.Loopback, relay.Port);
        var session = Guid.NewGuid();
        var epoch = Guid.NewGuid();

        byte[] Plain(Packet packet)
        {
            var wire = Wire.Encode(packet);
            var datagram = new byte[17 + wire.Length];
            wire.CopyTo(datagram, 17);
            return datagram;
        }

        void Send(UdpClient sender, byte[] datagram) => sender.Send(datagram, datagram.Length, relayEndpoint);

        void Expect(UdpClient receiver, byte[] expected)
        {
            receiver.Client.ReceiveTimeout = 2000;
            IPEndPoint origin = new IPEndPoint(IPAddress.Any, 0);
            if (!receiver.Receive(ref origin).SequenceEqual(expected))
                throw new InvalidOperationException("QA relay delivered the wrong datagram");
        }

        void Join(UdpClient guest)
        {
            var hello = Plain(new Packet(Kind.Hello, serial: 1, build: new byte[Wire.FingerprintSize]));
            Send(guest, hello);
            Expect(host, hello);
        }

        void Snapshot(UdpClient receiver, ulong revision, Guid sourceEpoch, ulong sequence, bool expectLoss)
        {
            var body = new byte[1004];
            BitConverter.GetBytes(1000).CopyTo(body, 0);
            var wire = Wire.Encode(new Packet(Kind.NativeChunk, session, sourceEpoch, revision: revision, payload: body));
            var first = DirectFragments.Encode(wire, sequence, 0);
            var second = DirectFragments.Encode(wire, sequence, 1);
            var marker = Plain(new Packet(Kind.Ping, session, sourceEpoch, serial: sequence));
            Send(host, first);
            Send(host, second);
            Send(host, marker);
            Expect(receiver, first);
            if (!expectLoss) Expect(receiver, second);
            Expect(receiver, marker);
        }

        Join(firstGuest);
        var presence = Plain(new Packet(Kind.NativePresence, session, epoch, serial: 1,
            payload: new byte[] { 1, 0, 0, 0, 1, 0, 0, 0 }));
        Send(firstGuest, presence);
        Expect(host, presence);
        Snapshot(firstGuest, 3, epoch, 1, false);
        relay.ArmActiveLoss();
        Snapshot(firstGuest, 3, epoch, 2, false);
        var priorGap = Plain(new Packet(Kind.NativeProgress, session, epoch, serial: 3, revision: 4));
        Send(firstGuest, priorGap);
        Expect(host, priorGap);
        Snapshot(firstGuest, 4, epoch, 3, false);
        Snapshot(firstGuest, 5, epoch, 4, true);

        Join(secondGuest);
        var newEpoch = Guid.NewGuid();
        var foreignGap = Plain(new Packet(Kind.NativeProgress, session, newEpoch, serial: 4, revision: 5));
        Send(secondGuest, foreignGap);
        Expect(host, foreignGap);
        Snapshot(secondGuest, 5, newEpoch, 5, false);
        var foreignAck = Plain(new Packet(Kind.NativeAck, session, newEpoch, revision: 5));
        Send(secondGuest, foreignAck);
        Expect(host, foreignAck);
        if (relay.Passed || !relay.Summary.Contains("revision=5 chunks=1 gap_ms=-1 repair_ms=-1 ack_drops=0"))
            throw new InvalidOperationException("QA relay counted a new peer's matching revision as repair");
        Console.WriteLine("PASS QA loss injection avoids previously sent/requested snapshots and ignores matching revisions after rejoin");
    }
}
