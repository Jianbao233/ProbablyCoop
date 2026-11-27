using System.Net;
using System.Net.Sockets;
using System.Buffers.Binary;
using ProbablyCoop.Core;

internal static class DirectConnectionMeasurementsTests
{
    internal static void Run()
    {
        var fingerprint = new byte[Wire.FingerprintSize];
        double now = 0, measuredNow = 0;
        using var relay = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var host = new LoopbackLink(fingerprint, 0, bindAddress: IPAddress.Loopback,
            measurementClock: () => measuredNow);
        using var guest = new LoopbackLink(fingerprint, 0, host.Code, mappedEndpoint: (IPEndPoint)relay.Client.LocalEndPoint);
        IPEndPoint guestEndpoint = null;
        bool dropPongs = true, dropFirstFragment = false, delayedFirstHostProbe = false;
        bool watchGuestPong = false, guestPongQueued = false;
        int lostProbes = 0, lostFragments = 0;
        bool complementaryLoss = false, dropNextNativeAck = false, suppressProgress = false;
        int fragmentPasses = 0, complementaryDrops = 0, snapshotBytes = 0, lostAcks = 0;
        var fragmentSequences = new List<ulong>();

        void Pump()
        {
            measuredNow = now;
            host.Tick(now, () =>
            {
                // Saturate one capture before sending the host's first probe.
                if (delayedFirstHostProbe || host.Session.State != LinkState.Connected) return;
                measuredNow += 0.7;
                delayedFirstHostProbe = true;
            });
            now = measuredNow;
            guest.Tick(now);
            while (relay.Available != 0)
            {
                var from = new IPEndPoint(IPAddress.Any, 0);
                byte[] bytes = relay.Receive(ref from);
                if (!from.Equals(host.LocalEndpoint))
                {
                    guestEndpoint = from;
                    if (bytes[16] == 0 && bytes.Length >= 23)
                    {
                        if (dropNextNativeAck && bytes[22] == (byte)Kind.NativeAck)
                        { lostAcks++; dropNextNativeAck = false; suppressProgress = true; continue; }
                        if (suppressProgress && bytes[22] == (byte)Kind.NativeProgress) continue;
                    }
                    relay.Send(bytes, bytes.Length, host.LocalEndpoint);
                    continue;
                }
                byte marker = bytes[16];
                if (dropPongs && marker == 0 && bytes.Length >= 23 && bytes[22] == (byte)Kind.Pong)
                { lostProbes++; continue; }
                if (watchGuestPong && marker == 0 && bytes.Length >= 23 && bytes[22] == (byte)Kind.Pong)
                    guestPongQueued = true;
                if (dropFirstFragment && marker == DirectFragments.Marker && bytes[27] == 0)
                { lostFragments++; continue; }
                if (complementaryLoss && marker == DirectFragments.Marker)
                {
                    snapshotBytes += bytes.Length;
                    int fragment = bytes[27];
                    if (fragment == 0)
                    {
                        fragmentPasses++;
                        fragmentSequences.Add(BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(17, 8)));
                    }
                    if (fragmentPasses == 1 && fragment == 1 || fragmentPasses == 2 && fragment == 2)
                    { complementaryDrops++; continue; }
                }
                relay.Send(bytes, bytes.Length, guestEndpoint);
            }
            now += 0.025;
            Thread.Sleep(1);
        }

        void Until(Func<bool> condition, double seconds)
        {
            double deadline = now + seconds;
            while (!condition() && now < deadline) Pump();
            Check(condition(), "timed out while waiting for expected diagnostic state");
        }

        var samples = guest.Session.Measurements;
        Until(() => samples.TimeoutPercent == 100, 8);
        Check(lostProbes >= 1 && samples.CompletedProbes > 0 && samples.RoundTripMilliseconds == null,
            "all dropped pong replies are distinguishable from an unmeasured connection");
        Check(delayedFirstHostProbe && host.Session.Measurements.RoundTripMilliseconds is >= 0 and < 100,
            "the 700 ms before the host probe was sent must not be charged to its RTT");
        dropPongs = false;
        Until(() => samples.RoundTripMilliseconds.HasValue, 6);
        Check(samples.TimeoutPercent < 100 && samples.UploadBytesPerSecond > 0 && samples.DownloadBytesPerSecond > 0,
            "successful probes resume and both rates reflect real UDP activity");

        watchGuestPong = true;
        Until(() => guestPongQueued, 6);
        double? latencyBeforeStall = samples.RoundTripMilliseconds;
        double? lossBeforeStall = samples.TimeoutPercent;
        int goodWindowBeforeStall = samples.CompletedProbes;
        now += 0.75;
        Pump();
        Check(samples.RecentLocalStallMilliseconds is >= 750 && samples.LocallyDelayedProbes == 1,
            "a real queued UDP reply received after a skipped local pump is identified");
        Check(samples.RoundTripMilliseconds == latencyBeforeStall && samples.TimeoutPercent == lossBeforeStall,
            "a local stall cannot inflate latency or count as a game probe timeout");
        Until(() => samples.CompletedProbes > goodWindowBeforeStall, 6);

        dropFirstFragment = true;
        var payload = new byte[6500];
        new Random(1).NextBytes(payload);
        host.Session.Native.Publish(payload);
        Until(() => lostFragments >= 2 && samples.DownloadBytesPerSecond > 350, 6);
        Check(guest.Session.Native.Revision == 0,
            "authenticated fragments count toward UDP traffic before a message can be completed");
        dropFirstFragment = false;
        Until(() => guest.Session.Native.State != null && guest.Session.Native.State.AsSpan().SequenceEqual(payload), 6);
        Check(guest.Session.State == LinkState.Connected, "buffered stock repaired without rejoining");
        Console.WriteLine("PASS direct connection diagnostics: lost echo, recovered RTT, local stall, actual UDP fragments and in-room repair");

        Until(() => host.Session.Native.Revision == guest.Session.Native.Revision, 4);
        var next = new byte[14000];
        new Random(2).NextBytes(next);
        ulong expected = host.Session.Native.Revision + 1;
        complementaryLoss = dropNextNativeAck = true;
        host.Session.Native.Publish(next);
        Until(() => guest.Session.Native.Revision == expected && lostAcks == 1, 3);
        Check(complementaryDrops == 2 && fragmentPasses == 2 && fragmentSequences[0] == fragmentSequences[1] &&
            guest.Session.Native.State.AsSpan().SequenceEqual(next),
            "complementary loss must assemble one exact snapshot from two incomplete UDP sends");
        int bytesToApply = snapshotBytes;
        Until(() => host.Session.Native.Revision == expected, 1.35);
        Check(fragmentPasses == 3 && fragmentSequences.All(seq => seq == fragmentSequences[0]) &&
            host.Session.State == LinkState.Connected && guest.Session.State == LinkState.Connected,
            "a completed chunk must repeat its lost ACK on the same fragment sequence");
        Console.WriteLine($"PASS real UDP complementary loss: two incomplete sends, {bytesToApply} snapshot bytes to apply, lost ACK recovered without a sequence reset");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
