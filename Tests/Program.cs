using System.Diagnostics;
using System.Reflection;
using System.Net;
using System.Net.Sockets;
using Newtonsoft.Json.Linq;
using ProbablyCoop;
using ProbablyCoop.Core;

var build = new byte[Wire.FingerprintSize];
if (args.Length > 0)
{
    if (args[0] == "--local-guest" && args.Length == 4)
        HeadlessGuest.Run(args[1], HeadlessGuest.ReadFingerprint(args[2], args[3]));
    else if (args[0] == "--fixture-guest" && args.Length == 2)
        HeadlessGuest.Run(args[1], build);
    else if (args[0] == "host" || (args[0] == "guest" && args.Length == 2))
        RunPeer(args[0] == "host", args.Length > 1 ? args[1] : null);
    else if(args[0]=="--inspect-native-samples" && (args.Length==2 || args.Length==3 && args[2]=="--measure"))
        NativeSampleDelta.Run(args[1],args.Length==3);
    else if (args[0] == "--qa-relay" && args.Length == 1)
        QaLanRelayTests.Run();
    else if (args[0] == "--effect-transition" && args.Length == 2)
        NativeEffectTransitionTests.Run(args[1]);
    else if (args[0] == "--day-settlement-boundaries" && args.Length == 3)
        DaySettlementBoundaryTests.Run(args[1], args[2]);
    else if (args[0] == "--lan-smoke" && args.Length == 2)
    {
        await CheckDirectPeers(args[1]);
        var address = IPAddress.Parse(args[1]);
        int port;
        using (var reservation = new UdpClient(new IPEndPoint(address, 0)))
            port = ((IPEndPoint)reservation.Client.LocalEndPoint).Port;
        using var lanHost = new LoopbackLink(build, 0, bindAddress: address, listenPort: port);
        Check(lanHost.LocalEndpoint.Equals(new IPEndPoint(address, port)), "IPv4 uses requested UDP port");
        var inventory = new TestShop { Full = true };
        lanHost.Session.Shop.SetAuthority(inventory);
        var output = await RunHeadless(lanHost, Stopwatch.StartNew(), null, "move 7", "exit");
        Check(output.Contains("\"Id\":80") && output.Contains("\"Result\":\"Applied\"") && inventory.Moves == 1,
            "all 80 inventory entries arrive via IPv4 fragments and guest applies exactly one move");
        Console.WriteLine("PASS real IPv4 UDP: full fragmented 80-item snapshot and authoritative move");
        using var mappedHost = new LoopbackLink(build, 0, bindAddress: address);
        using var relay = new ProbablyCoopQaLanRelay(args[1], mappedHost.LocalEndpoint.Port,
            dropReply:false, activeLoss:false, injectLoss:false);
        Check(DirectRoomEndpoint.TryParseMappedEndpoint($"{address}:{relay.Port}", out var mappedEndpoint) &&
            mappedEndpoint.Port != mappedHost.LocalEndpoint.Port, "relay publishes a separate UDP endpoint");
        using var mappedGuest = new LoopbackLink(build, 0, mappedHost.Code, mappedEndpoint:mappedEndpoint);
        var clock = Stopwatch.StartNew();
        while (mappedGuest.Session.State != LinkState.Connected && clock.Elapsed.TotalSeconds < 4)
        {
            mappedHost.Tick(clock.Elapsed.TotalSeconds);
            mappedGuest.Tick(clock.Elapsed.TotalSeconds);
            await Task.Delay(10);
        }
        Check(mappedGuest.Session.State == LinkState.Connected, "IPv4 joins using mapped address and UDP port");
        Console.WriteLine("PASS original IPv4 room code through a separate UDP forwarding port");
        Check(!DirectRoomEndpoint.TryParseIpv4("frp.example.test", out _) &&
            CoopIssueCode.DirectRoom("bad_address") == "LAN-101", "domain is not a local listen address");
        bool rejected = false;
        try { using var wrong = new LoopbackLink(build, 0, bindAddress: IPAddress.Parse("192.0.2.1")); }
        catch(SocketException error) when(error.SocketErrorCode == SocketError.AddressNotAvailable)
        { rejected = CoopIssueCode.DirectRoom("udp_address_not_local") == "LAN-102"; }
        Check(rejected, "unassigned relay IPv4 fails when used as a local listen address");
        Console.WriteLine("PASS local UDP bind rejects nonlocal IPv4 and reports a distinct code");
    }
    else throw new ArgumentException("Usage: --local-guest <PSL code> <Assembly-CSharp.dll> <ProbablyCoop.dll>");
    return;
}

ActivityScopeTests.Run();
ModExtensionTests.Run();
OpeningAndTravelTests.Run();
CaptureChangeTests.Run();
RoomAdmissionTests.Run();
NativeJoinSnapshotTests.Run();
RoomModManifestTests.Run();
NativeSnapshotPageTests.Run();
NativeCommandEnvelopeTests.Run();
ChoiceLeaseTests.Run();
DaySettlementTests.Run();
ActivityRequestTests.Run();
ActivityRequestTransportTests.Run();
QaLanRelayTests.Run();
var fillRows=new TooltipFill();
foreach(char replacement in new[]{' ','█'})
{
    foreach(string input in new[]{"","A","中文🙂","<b>售价</b>","\u2007",new string('x',256)})
    {
        string expected=new string(input.Select(c=>char.IsControl(c) ? c : replacement).ToArray());
        Check(fillRows.TryGet(input,replacement,out var actual) && actual==expected,"native UTF-16 fill equivalence");
        Check(fillRows.TryGet(new string('y',input.Length),replacement,out var reused) && ReferenceEquals(actual,reused),"fill width shares no source text");
    }
    foreach(string fallback in new[]{null,"\0ABC","水\n","a\t","\u0085",new string('x',257)})
        Check(!fillRows.TryGet(fallback,replacement,out _),"special text takes original fill");
}
Check(!fillRows.TryGet("word",'x',out _),"unknown palette uses original fill");
HeldInteractionTests.Run();
PickupLeaseTests.Run();
RunClockTests.Run();
TooltipSnapshotWatermarkTests.Run();
MoveProofCacheTests.Run();
ContentsProofCacheTests.Run();
SortProofCacheTests.Run();
PointerContextTests.Run();
ReplicaLinkTests.Run();
IndependentRequestTests.Run();
OperationContextTests.Run();
PendingActionsTests.Run();
PendingActionsTransportTests.Run();
CommandProjectionTimingTests.Run();
DirectCaptureOrderTests.Run();
NativeSnapshotCodecTests.Run();
NativeSnapshotBinaryTests.Run();
NativeSnapshotIndexTests.Run();
NativeRootProjectionTests.Run();
NativeReceiptProjectionTests.Run();
NativeDeferredFieldsTests.Run();
InlineSpriteDataTests.Run();
foreach (ulong id in new[] { 1UL, 36UL, 0x0186AB00_ABCDEF01UL, ulong.MaxValue })
    Check(RoomCode.TryDecode(RoomCode.Encode(id).ToLowerInvariant(), out var parsed) && parsed == id, "room round trip");
foreach (var code in new[] { "", "PS1-0-FFFF", "PS1-ZZZZZZZZZZZZZ-FFFF", "PS1-1-0000", new string('Z', 65) })
    Check(!RoomCode.TryDecode(code, out _), "invalid code rejected");
Console.WriteLine("PASS room code: UInt64 round trip, overflow and checksum");
var invalidBill = new ShopView
{
    Active = true, Phase = ShopDay.Ready, Day = 4, NightServices = 1,
    NightCost = 5, ServiceCosts = new[] { 4 }
};
Check(!ShopProtocol.TryDecode(ShopProtocol.Encode(invalidBill), out _),
    "reject nightly bills whose line items do not sum to the total");
var projection = new ShopView { Active = true, Cash = 492, Day = 5 };
for (uint i = 1; i <= ShopProtocol.MaxItems; i++)
    projection.Items.Add(new ShopItem
    {
        Id = i, Place = 1, Quantity = 2, X = 12, Y = 3, Width = 3, Height = 2,
        Cells = 0b101111, Transform = 5, Atlas = new string('A', ShopProtocol.MaxSpriteText),
        Sprite = new string('B', ShopProtocol.MaxSpriteText), Label = new string('L', ShopProtocol.MaxText)
    });
projection.ShowcaseTier = 2;
projection.Items[0].Place = 7;
projection.DialogueId = 34;
projection.DialogueSpeaker = 1;
projection.DialogueChoicesEnabled = 0b00000101;
projection.DialogueLine = new string('L', ShopProtocol.MaxDialogueLine);
projection.DialogueChoices = Enumerable.Range(0, ShopProtocol.MaxDialogueChoices)
    .Select(_ => new string('C', ShopProtocol.MaxDialogueChoice)).ToArray();
projection.Phase = ShopDay.Saved;
projection.MorningReport = Enumerable.Range(0, ShopProtocol.MaxReportLines)
    .Select(_ => new string('R', ShopProtocol.MaxReportText)).ToArray();
var projectedBytes = ShopProtocol.Encode(projection);
var fragmentReceiver = new DirectFragments();
int fragmentCount = DirectFragments.Count(projectedBytes.Length);
for (int i = fragmentCount - 1; i > 0; i--)
{
    var fragment = DirectFragments.Encode(projectedBytes, 1, i);
    Check(fragment.Length <= 1028 && fragmentReceiver.Receive(fragment, 1) == null &&
          fragmentReceiver.Receive(fragment, 1.1) == null, "incomplete and duplicate fragments");
}
var combined = fragmentReceiver.Receive(DirectFragments.Encode(projectedBytes, 1, 0), 1.2);
Check(combined != null && combined.SequenceEqual(projectedBytes), "out-of-order fragments reassemble once");
Check(fragmentReceiver.Receive(DirectFragments.Encode(projectedBytes, 1, 0), 1.3) == null,
    "completed fragment is not delivered again");
Console.WriteLine("PASS direct reassembly: reordering, duplicate fragments and datagram size");
// All four native chunks may be on the wire together. Interleave them and
// then fill the window with completed traffic while one early packet is late.
var burstReceiver = new DirectFragments();
var twoPieces = projectedBytes.Take(DirectFragments.ChunkSize+1).ToArray();
for(ulong seq=1;seq<=4;seq++)
    Check(burstReceiver.Receive(DirectFragments.Encode(twoPieces,seq,0),1)==null,"four partial chunks");
for(ulong seq=4;seq>=2;seq--)
    Check(burstReceiver.Receive(DirectFragments.Encode(twoPieces,seq,1),1.1)?.SequenceEqual(twoPieces)==true,"four interleaved native chunks");
for(ulong seq=5;seq<=12;seq++)
{
    Check(burstReceiver.Receive(DirectFragments.Encode(twoPieces,seq,0),1.2)==null,"new burst incomplete");
    Check(burstReceiver.Receive(DirectFragments.Encode(twoPieces,seq,1),1.2)?.SequenceEqual(twoPieces)==true,"new burst complete");
}
Check(burstReceiver.Receive(DirectFragments.Encode(twoPieces,1,1),1.3)?.SequenceEqual(twoPieces)==true,
    "late fragment survives eviction of completed bursts");
Check(projectedBytes.Length <= Wire.MaxSize - Wire.HeaderSize &&
      ShopProtocol.TryDecode(projectedBytes, out var decoded) &&
      decoded.Items.Count == ShopProtocol.MaxItems && decoded.Items[0].Cells == 0b101111 &&
      decoded.Items[0].Transform == 5 && decoded.Items[0].X == 12 && decoded.Items[0].Y == 3 &&
      decoded.Items[0].Sprite.Length == ShopProtocol.MaxSpriteText &&
      decoded.Items[0].Place == 7 && decoded.ShowcaseTier == 2 &&
      decoded.DialogueId == 34 && decoded.DialogueChoicesEnabled == 0b00000101 &&
      decoded.DialogueChoices.Length == ShopProtocol.MaxDialogueChoices &&
      decoded.MorningReport.Length == ShopProtocol.MaxReportLines,
    "native projection preserves nonrectangular rotated shape and fits a full snapshot");
projection.Items[0].Cells = 1UL << 12;
Check(!ShopProtocol.TryDecode(ShopProtocol.Encode(projection), out _),
    "native projection rejects shape bits outside item bounds");
Console.WriteLine("PASS native projection: full snapshot bound, showcase, dialogue, report and invalid shape");


var toHost = new Queue<(byte[] Data, bool Cursor)>();
var toGuest = new Queue<(byte[] Data, bool Cursor)>();
Kind? rejectNextHostPacket = Kind.State;
Kind? dropNextHostPacket = null;
var host = new CoopSession(true, build, SendToGuest);
var guest = new CoopSession(false, build, SendToHost);
host.Connect(0);
guest.Connect(0);
guest.Tick(0);
Drain(0);
Check(host.State == LinkState.Connected && guest.State == LinkState.Handshake, "initial state rejected by transport");
host.Tick(0);
Drain(0);
Check(guest.State == LinkState.Connected, "initial state retry completes handshake");

guest.ChangeTile(3, 1);
var request = toHost.Dequeue();
guest.Tick(2.1); // Missing result causes a retry of the same request.
Check(toHost.Any(x => x.Data.SequenceEqual(request.Data)), "retry preserves the original command");
Check(host.Receive(request.Data, false, 2.1) && host.Receive(request.Data, false, 2.2),
    "duplicate request accepted for resync");
Drain(2.2);
Check(host.Revision == 1 && guest.Revision == 1 && host.Tiles == 8 && guest.Tiles == 8 &&
      !guest.HasPendingChange, "one authoritative mutation despite retries");

guest.ChangeTile(4, 3);
host.ChangeTile(5, 3);
Drain(3);
Check(host.Revision == 2 && host.Tiles == 40 && guest.Tiles == 40 && !guest.HasPendingChange,
    "stale revision refreshes state without applying stale edit");
guest.ChangeTile(4, 3.5);
Drain(3.5);
Check(host.Revision == 3 && guest.Revision == 3 && host.Tiles == 56 && guest.Tiles == 56,
    "fresh request after conflict");

var displayed = new List<Packet>();
guest.RemoteCursor += displayed.Add;
host.MoveCursor(123, 456, true, false);
host.MoveCursor(456, 789, true, true);
var older = toGuest.Dequeue();
var newer = toGuest.Dequeue();
Check(!guest.Receive(newer.Data, false, 4), "cursor cannot enter control channel");
Check(guest.Receive(newer.Data, true, 4) && !guest.Receive(older.Data, true, 4), "cursor ordering");
Check(displayed.Count == 1 && displayed[0].X == 456 && displayed[0].Y == 789 &&
      displayed[0].Flags == 3, "cursor payload");
Check(!guest.Receive(new byte[Wire.MaxSize + 1], false, 4) &&
      !guest.Receive(newer.Data.AsSpan(0, 13), true, 4), "malformed lengths");

guest.SetPointerName("  伙伴\nA  ");
var profile = toHost.Dequeue();
Check(!host.Receive(profile.Data, true, 4) && host.Receive(profile.Data, false, 4) &&
      host.RemotePointerName == "伙伴A" && !host.Receive(profile.Data, false, 4), "pointer name channel, display and replay");
guest.SetPointerName("  伙伴\nA  ");
Check(toHost.Count == 0, "unchanged pointer name is not resent");
Check(guest.SendItemPing(uint.MaxValue, 17, 4) && !guest.SendItemPing(9, 17, 4.1),
    "item ping identity, context and rate limit");
var itemPing = toHost.Dequeue();
Check(host.Receive(itemPing.Data, false, 4) && host.RemoteItemPing == (1UL, 17UL, uint.MaxValue) &&
      !host.Receive(itemPing.Data, false, 4), "item ping is reliable, bounded and processed once");
Check(!PointerProfile.TryRead(new byte[]{0xff},out _) &&
      !PointerProfile.TryRead(PointerProfile.Encode("<b>提示"),out _) &&
      !PointerProfile.TryRead(new byte[PointerProfile.NameBytes+1],out _), "pointer profile rejects invalid text");
guest.SetPointerName("");
Drain(4);
Check(host.RemotePointerName=="", "clearing name restores the transport fallback");

host.Tick(17);
guest.Tick(17);
Check(host.State == LinkState.TimedOut && guest.State == LinkState.TimedOut, "heartbeat timeout");
Connect(18);
Check(guest.Revision == 3 && guest.Tiles == 56, "new peer epoch gets host snapshot");
Check(!host.Receive(request.Data, false, 18.2), "previous epoch request denied");
Check(!guest.Receive(newer.Data, true, 18.2), "previous epoch cursor denied");
Check(!host.Receive(itemPing.Data, false, 18.2) && host.RemoteItemPing.Serial==0 &&
      host.RemotePointerName=="", "old room epoch cannot replay pointer settings or item pings");
Console.WriteLine("PASS pointer profile and item pings: Unicode, limits, rate, duplicates, clearing and reconnect");
for (double now = 19; now <= 619; now++)
{
    host.Tick(now);
    guest.Tick(now);
    Drain(now);
}
Check(host.State == LinkState.Connected && guest.State == LinkState.Connected, "ten simulated minutes of heartbeat");
Console.WriteLine("PASS core: handshake, duplicate requests, conflicts, snapshot, cursor ordering, timeout and reconnect");
Check(host.Measurements.RoundTripMilliseconds == 0 && guest.Measurements.RoundTripMilliseconds == 0 &&
    host.Measurements.TimeoutPercent == 0 && guest.Measurements.TimeoutPercent == 0 &&
    host.Measurements.UploadBytesPerSecond > 0 && host.Measurements.DownloadBytesPerSecond > 0,
    "heartbeat serials echo through the connected peers and produce transport statistics");
var measurements = new ConnectionMeasurements();
measurements.Reset(10);
var unanswered = measurements.BeginProbe(10);
var answered = measurements.BeginProbe(12);
measurements.AcceptPong(unanswered, 12.1);
Check(measurements.RoundTripMilliseconds == null && measurements.TimeoutPercent == 100 && measurements.CompletedProbes == 1,
    "the first lost probe reports a timeout even before any successful sample");
measurements.AcceptPong(answered, 12.25);
measurements.AddSent(3072);
measurements.AddReceived(1536);
measurements.Tick(13);
Check(measurements.TimeoutPercent == 50 && measurements.RoundTripMilliseconds == 250 &&
    measurements.UploadBytesPerSecond == 1024 && measurements.DownloadBytesPerSecond == 512,
    "measurements count a missed probe and calculate traffic using elapsed time");
for (int i = 0; i < 30; i++) measurements.AcceptPong(measurements.BeginProbe(14 + i * 2), 14.1 + i * 2);
Check(measurements.TimeoutPercent == 0, "old loss falls out of the rolling probe window");
measurements.Reset(100);
Check(measurements.RoundTripMilliseconds == null && measurements.TimeoutPercent == null &&
    measurements.UploadBytesPerSecond == 0 && measurements.DownloadBytesPerSecond == 0,
    "new connection starts with no measurement history");
Console.WriteLine("PASS connection measurements: RTT echo, rolling probe loss, directional traffic and epoch reset");
measurements.ObserveLocalPump(100);
measurements.BeginProbe(100);
measurements.ObserveLocalPump(100.5);
var afterPause = measurements.BeginProbe(100.5);
Check(measurements.CompletedProbes == 0 && measurements.LocallyDelayedProbes == 1 &&
    measurements.TimeoutPercent == null && measurements.RecentLocalStallMilliseconds == 500,
    "a local stall crosses the unanswered probe without recording network loss");
measurements.ObserveLocalPump(100.6);
measurements.AcceptPong(afterPause, 100.6);
for (int i=1; i<=160; i++) measurements.ObserveLocalPump(100.6+i*0.1);
Check(measurements.RecentLocalStallMilliseconds == null && measurements.TimeoutPercent == 0,
    "the local stall notice expires independently of the valid probe rate");
for (int i=1; i<=30; i++)
{
    double time=116.6+i*0.1;
    measurements.ObserveLocalPump(time);
    measurements.AcceptPong(measurements.BeginProbe(time), time);
}
Check(measurements.LocallyDelayedProbes == 0 && measurements.CompletedProbes == 30,
    "samples affected by local stalls leave the rolling window");
Console.WriteLine("PASS connection processing gaps: exclude affected probes, expire notice and clear old samples");
DirectConnectionMeasurementsTests.Run();
CheckWorldReload(build);
CheckNativeProjectionGrace(build);

var incompatible = (byte[])build.Clone();
incompatible[0] = 1;
var wrongGuest = new CoopSession(false, incompatible, SendToHost);
host.Connect(620);
wrongGuest.Connect(620);
wrongGuest.Tick(620);
var hello = toHost.Dequeue();
host.Receive(hello.Data, hello.Cursor, 620);
var reject = toGuest.Dequeue();
wrongGuest.Receive(reject.Data, reject.Cursor, 620);
Check(host.State == LinkState.Incompatible && wrongGuest.State == LinkState.Incompatible, "build mismatch");
Console.WriteLine("PASS fingerprint mismatch on both sides");

toHost.Clear();
toGuest.Clear();
var authority = new TestShop();
host.Shop.SetAuthority(authority);
host.DropPeer();
host.Tick(622); // A host can preview its real shop before a guest arrives.
Check(host.Shop.View.Active && toGuest.Count == 0, "unconnected shop preview stays local");
Connect(623);
rejectNextHostPacket = Kind.ShopState;
host.Tick(623.3);
Drain(623.3);
Check(!guest.Shop.View.Active, "initial shop snapshot rejected by transport");
host.Tick(623.6);
Drain(623.6);
Check(guest.Shop.View.Active && guest.Shop.View.Cash == 550 && guest.Shop.View.Items.Count == 1 &&
      guest.Shop.View.OfferLabel == "瓶装水 ×1" && guest.Shop.View.Price == 38,
    "unchanged shop snapshot retries after transport rejection");
Console.WriteLine("PASS authoritative initial state and shop snapshot retry after transport rejection");
guest.Shop.Submit(ShopAction.Move, 7, 624);
var move = toHost.Dequeue();
Check(host.Receive(move.Data, move.Cursor, 624) && host.Receive(move.Data, move.Cursor, 624.1),
    "duplicate shop request receives snapshot");
Drain(624.1);
Check(authority.Moves == 1 && guest.Shop.View.Items[0].Place == 2 && !guest.Shop.Waiting,
    "one authoritative move and one consistent result");
guest.Shop.Submit(ShopAction.Move, 7, 624.2);
host.Shop.Submit(ShopAction.Move, 7, 624.2);
Drain(624.2);
Check(authority.Moves == 2 && guest.Shop.View.Items[0].Place == 1 && guest.Shop.Result == ShopResult.Stale,
    "simultaneous host and guest requests only move the same item once");

guest.Shop.Submit(ShopAction.Buy, 99, 625);
authority.Cash = 500; // Host bought something while the guest was looking at the prior view.
Drain(625);
Check(authority.Cash == 500 && guest.Shop.View.Cash == 500 && guest.Shop.Result == ShopResult.Stale,
    "stale offer cannot debit cash");
authority.EndDayOpen = true;
host.Tick(626);
Drain(626);
guest.Shop.Submit(ShopAction.Ready, 0, 627);
Drain(627);
Check(guest.Shop.View.ReadyMask == 2 && guest.Shop.View.ServiceCosts.SequenceEqual(new[] { 5 }),
    "guest ready for the authoritative service breakdown");
host.Shop.Submit(ShopAction.FinishDay, 0, 628);
Check(authority.Days == 0, "host cannot settle before both ready");
host.Shop.Submit(ShopAction.Ready, 0, 629);
Drain(629);
Check(guest.Shop.View.ReadyMask == 3, "both ready");
authority.NightServices = 2;
host.Shop.Submit(ShopAction.FinishDay, 0, 629.2);
Drain(629.2);
Check(authority.Days == 0 && guest.Shop.View.ReadyMask == 0 &&
      guest.Shop.View.NightServices == 2 && guest.Shop.View.NightCost == 5,
    "switching to another nightly service at the same cost invalidates both confirmations");
guest.Shop.Submit(ShopAction.Ready, 0, 629.4);
Drain(629.4);
host.Shop.Submit(ShopAction.Ready, 0, 629.6);
Drain(629.6);
host.Shop.Submit(ShopAction.FinishDay, 0, 630);
host.Shop.Submit(ShopAction.FinishDay, 0, 630.1);
Drain(630.1);
Check(authority.Days == 1 && guest.Shop.View.Day == 2 && guest.Shop.View.ReadyMask == 0 &&
      guest.Shop.View.MorningReport.SequenceEqual(new[] { "夜间电费 -5" }),
    "day advances once, clears readiness, and delivers the morning report");
authority.EndDayOpen = true;
host.Tick(631);
Drain(631);
guest.Shop.Submit(ShopAction.Ready, 0, 631.1);
var previousReady = toHost.Peek();
Drain(631.1);
Check(guest.Shop.View.ReadyMask == 2, "guest ready before disconnect");
host.DropPeer();
guest.DropPeer();
Connect(632);
host.Tick(632.3);
Drain(632.3);
Check(!host.Receive(previousReady.Data, false, 632.4) && guest.Shop.View.Day == 2 &&
      guest.Shop.View.Cash == 495 && guest.Shop.View.Items[0].Place == 1 && guest.Shop.View.ReadyMask == 0,
    "reconnect restores current shop without replaying previous ready command");
Console.WriteLine("PASS shop: named quotes, duplicate and simultaneous moves, stale trade, changed night plan, one-shot day and reconnect");

int previousCash = guest.Shop.View.Cash;
authority.Cash = previousCash + 1;
dropNextHostPacket = Kind.ShopState;
host.Tick(633.0);
Drain(633.0);
Check(guest.Shop.View.Cash == previousCash, "accepted but lost shop snapshot has not reached guest");
host.Tick(633.0 + 0.61);
Drain(633.0 + 0.61);
Check(guest.Shop.View.Cash == previousCash + 1, "missing acknowledgement triggers another shop snapshot");
Console.WriteLine("PASS shop state delivery after silent loss");
{
    var shared=new TestShop(); int sentViews=0;
    var panel=new ShopLink(true,(kind,serial,revision,value,bytes)=>{if(kind==Kind.ShopState) sentViews++; return true;});
    panel.SetAuthority(shared);
    var observed=shared.Capture();
    panel.ObserveAuthority(shared,observed,ShopProtocol.Encode(observed),1);
    panel.Tick(1,true); panel.Tick(1.21,true);
    Check(shared.Captures==1 && sentViews==1 && panel.View.Cash==shared.Cash,"metadata re-read the shop or missed its first publication");
    shared.Cash++;
    panel.Submit(ShopAction.Ready,0,1.22);
    Check(shared.Captures==2 && panel.Result==ShopResult.Stale && panel.View.Cash==shared.Cash,"input trusted a prior capture after a real change");
    shared.Cash++; panel.Tick(1.31,true);
    Check(shared.Captures==3 && panel.View.Cash==shared.Cash,"metadata fallback stopped after native publications ceased");
    Console.WriteLine("PASS native and panel share one host observation; inputs revalidate and periodic fallback resumes");
}
{
    var shared = new TestShop { EndDayOpen = true };
    var sent = new List<byte[]>();
    var panel = new ShopLink(true, (kind, serial, revision, value, bytes) =>
    {
        if (kind == Kind.ShopState) sent.Add(bytes);
        return true;
    });
    panel.SetAuthority(shared);
    var observed = shared.Capture();
    byte[] published = ShopProtocol.Encode(observed);
    panel.ObserveAuthority(shared, observed, published, 1);
    panel.Tick(1, true);
    panel.Tick(1.61, true);
    Check(sent.Count == 2 && ReferenceEquals(sent[0], published) && ReferenceEquals(sent[1], published),
        "shop observation and retransmit did not share the original bytes");
    panel.Submit(ShopAction.Ready, 0, 1.62);
    panel.Tick(2.23, true);
    Check(sent.Count == 4 && !ReferenceEquals(sent[2], published) && ReferenceEquals(sent[2], sent[3]) &&
        ShopProtocol.TryDecode(sent[2], out var readyView) && readyView.ReadyMask == 1,
        "readiness must update the outbound bytes once and use them on retry");
    shared.Cash++;
    panel.Tick(2.44, true);
    Check(sent.Count == 5 && ShopProtocol.TryDecode(sent[4], out var changed) && changed.Cash == shared.Cash &&
        changed.ReadyMask == 0 && ShopProtocol.TryDecode(published, out var original) && original.Cash != changed.Cash,
        "a fresh observation must replace the old bytes without mutating them");
    Console.WriteLine("PASS shop wire payload reuse: initial publication, silent loss, new readiness and new authority state");
}
int movesBeforeHold = authority.Moves;
guest.Shop.Submit(ShopAction.PickUp, 7, 634);
Drain(634);
Check(guest.Shop.View.HeldItem == 7 && authority.Moves == movesBeforeHold,
    "host grants hold without removing the item");
var destination = new ShopPlacement(2, 6, 3, 1);
guest.Shop.Submit(ShopAction.Place, 7, 634.2, destination);
var placementPacket = toHost.Peek();
authority.Cash++;
host.Tick(634.25);
Drain(634.25);
Check(host.Receive(placementPacket.Data, placementPacket.Cursor, 634.3), "duplicate placement receives state");
Drain(634.3);
Check(authority.Moves == movesBeforeHold + 1 && guest.Shop.View.HeldItem == 0 &&
      authority.LastPlacement.X == 6 && authority.LastPlacement.Y == 3 &&
      authority.LastPlacement.Container == 2 && authority.LastPlacement.Transform == 1,
    "placement preserves coordinates and applies once after an unrelated change");
guest.Shop.Submit(ShopAction.PickUp, 7, 635);
Drain(635);
guest.Shop.Submit(ShopAction.Release, 7, 635.2);
Drain(635.2);
Check(guest.Shop.View.HeldItem == 0 && authority.Moves == movesBeforeHold + 1,
    "cancel releases a reservation without moving the item");
Console.WriteLine("PASS authoritative hold, exact placement, duplicate and cancel");

guest.Shop.Submit(ShopAction.GoScavenge, 0, 636);
Drain(636);
Check(guest.Shop.View.GuestOutside && guest.Shop.View.SearchesLeft == 2, "guest trip begins");
guest.Shop.Submit(ShopAction.Scavenge, 0, 636.1);
var searchPacket = toHost.Peek();
Drain(636.1);
Check(host.Receive(searchPacket.Data, searchPacket.Cursor, 636.2), "duplicate search resync");
Drain(636.2);
Check(guest.Shop.View.SearchesLeft == 1 && guest.Shop.View.Items.Any(item => item.Id == 81 && item.Place == 3),
    "one search consumes one attempt and publishes ground loot");
guest.Shop.Submit(ShopAction.TakeLoot, 81, 636.3);
Drain(636.3);
Check(guest.Shop.View.Items.Any(item => item.Id == 81 && item.Place == 4), "loot moves to guest pocket");
host.DropPeer();
guest.DropPeer();
Connect(637);
host.Tick(637.3);
Drain(637.3);
Check(guest.Shop.View.GuestOutside && guest.Shop.View.Items.Any(item => item.Id == 81 && item.Place == 4),
    "reconnect restores the active trip and carried loot");
guest.Shop.Submit(ShopAction.ReturnFromScavenge, 0, 637.4);
Drain(637.4);
Check(!guest.Shop.View.GuestOutside && guest.Shop.View.Items.Any(item => item.Id == 81 && item.Place == 1),
    "guest returns and loot reaches store");
Console.WriteLine("PASS scavenging protocol: shared attempt, ground, carrying, reconnect and return");

authority.CounterGoodsPlace = 6;
host.Tick(638);
Drain(638);
Check(guest.Shop.View.Shutter == 1 && guest.Shop.View.Customer == "顾客" &&
      guest.Shop.View.CustomerSprite == "default" && guest.Shop.View.Items.Any(i => i.Id == 82 && i.Place == 6),
    "front counter includes the current customer and goods");
guest.Shop.Submit(ShopAction.PickUp, 82, 638.2);
Drain(638.2);
guest.Shop.Submit(ShopAction.Place, 82, 638.3, new ShopPlacement(2, 4, 1, 0));
Drain(638.3);
Check(guest.Shop.View.Items.Any(i => i.Id == 82 && i.Place == 2), "goods on weighing counter");
guest.Shop.Submit(ShopAction.SelectOffer, 82, 638.4);
Drain(638.4);
Check(guest.Shop.View.OfferId == 500 && guest.Shop.View.Price == 15, "selected goods yield a host quote");
int beforeCounterTrade = guest.Shop.View.Cash;
guest.Shop.Submit(ShopAction.Buy, 500, 638.5);
Drain(638.5);
Check(guest.Shop.View.OfferId == 0 && guest.Shop.View.Cash == beforeCounterTrade - 15 &&
      guest.Shop.View.Items.Any(i => i.Id == 82 && i.Place == 1 && i.Owned),
    "guest buys against the host quote and receives the item in stock");
Console.WriteLine("PASS counter protocol: customer, front goods, weighing, quote selection and purchase");

authority.DialogueId = 90;
host.Tick(639);
Drain(639);
guest.Shop.Submit(ShopAction.ChooseDialogue, (90u << 8) | 1u, 639.1);
var choiceRequest = toHost.Peek();
Drain(639.1);
Check(host.Receive(choiceRequest.Data, choiceRequest.Cursor, 639.2), "duplicate choice acknowledged");
Drain(639.2);
Check(authority.ChoicesMade == 1 && guest.Shop.View.DialogueId == 0, "choice applied once and cleared");
guest.Shop.Submit(ShopAction.ChooseDialogue, (90u << 8) | 1u, 639.3);
Drain(639.3);
Check(authority.ChoicesMade == 1 && guest.Shop.Result == ShopResult.Stale, "expired dialogue rejected");
Console.WriteLine("PASS dialogue protocol: host choice, duplicate replay and old dialogue rejection");

using (var tunnelHost = new LoopbackLink(build, 0, bindAddress: IPAddress.Loopback, requireApproval: true))
using (var tunnelGuest = new LoopbackLink(build, 0, tunnelHost.Code))
{
    Check(tunnelHost.Code.StartsWith("PSI-127.0.0.1-") &&
        tunnelHost.LocalEndpoint.Address.Equals(IPAddress.Loopback), "tunnel listener uses the chosen adapter and the player room format");
    Check(!DirectRoomEndpoint.TryParseMappedEndpoint("127.0.0.1:28015", out _),
        "the guest's external mapping cannot point back to its own machine");
    var clock = Stopwatch.StartNew();
    bool approved = false;
    while (tunnelGuest.Session.State != LinkState.Connected && clock.Elapsed.TotalSeconds < 5)
    {
        tunnelHost.Tick(clock.Elapsed.TotalSeconds);
        if (!approved && tunnelHost.Session.JoinPending)
        {
            Check(tunnelGuest.Session.State != LinkState.Connected, "forwarded room waits for host permission");
            tunnelHost.Session.ApproveJoin();
            approved = true;
        }
        tunnelGuest.Tick(clock.Elapsed.TotalSeconds);
        await Task.Delay(5);
    }
    Check(approved && tunnelGuest.Session.State == LinkState.Connected && tunnelHost.Session.State == LinkState.Connected,
        "player IPv4 room over loopback completes approved handshake");
}
await CheckDirectPeers(null);

using (var localHost = new LoopbackLink(build, 0))
{
    var localShop = new TestShop();
    localHost.Session.Shop.SetAuthority(localShop);
    var clock = Stopwatch.StartNew();
    var gotCursor = false;
    localHost.Session.RemoteCursor += packet => gotCursor |= packet.X == 32768 && packet.Revision == 1;
    var first = await RunHeadless(localHost, clock, null, "cursor", "move 7", "exit");
    Check(first.Contains("\"Result\":\"Applied\"") && first.Contains("\"Place\":2") &&
          localShop.Moves == 1 && gotCursor, "headless guest moves inventory and sends cursor");
    var trades = await RunHeadless(localHost, clock, null, "buy 99", "buy 99", "sell 100", "exit");
    Check(trades.Contains("\"Cash\":512") && trades.Contains("\"Result\":\"Unavailable\"") &&
          trades.Contains("\"Cash\":530") && localShop.Trades == 2,
        "headless guest buys and sells once each; old quote does not debit again");
    Console.WriteLine("PASS loopback trades: buy 550->512, old quote rejected, sell 512->530 (fixture)");

    localShop.EndDayOpen = true;
    using (var dropping = StartPeer("--fixture-guest", localHost.Code))
    {
        try
        {
            await dropping.StandardInput.WriteLineAsync("ready");
            await PumpUntil(localHost, clock, () => localHost.Session.Shop.View.ReadyMask == 2,
                5, "guest ready before process loss");
            dropping.Kill();
            await dropping.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await PumpUntil(localHost, clock, () => localHost.Session.State == LinkState.Waiting,
                14, "abrupt guest loss detected by heartbeat");
            Check(localHost.Session.Shop.View.ReadyMask == 0 && localShop.Days == 0,
                "disconnect clears ready without settling");
        }
        finally { if (!dropping.HasExited) dropping.Kill(); }
    }

    var second = await RunHeadless(localHost, clock, now =>
    {
        if (localHost.Session.Shop.View.ReadyMask != 2) return;
        localHost.Session.Shop.Submit(ShopAction.Ready, 0, now);
        localHost.Session.Shop.Submit(ShopAction.FinishDay, 0, now);
        localHost.Session.Shop.Submit(ShopAction.FinishDay, 0, now);
    }, "ready", "wait 1", "snapshot", "exit");
    Check(second.Contains("\"Day\":2") && second.Contains("\"Cash\":525") &&
          second.Contains("\"Place\":2") && localShop.Days == 1 && localShop.Moves == 1,
        "headless reconnect retains inventory and receives one settlement");
    var checkpoint = await RunHeadless(localHost, clock, null, "snapshot", "exit");
    Check(checkpoint.Contains("\"Day\":2") && checkpoint.Contains("\"ReadyMask\":0") &&
          checkpoint.Contains("\"Cash\":525"), "reconnect receives current day checkpoint");
    Console.WriteLine("PASS loopback day: guest process killed, 12s timeout, rejoin, one settlement, final day 2 / cash 525 (fixture)");
}

bool SendToHost(byte[] bytes, bool cursor)
{
    toHost.Enqueue((bytes, cursor));
    return true;
}

bool SendToGuest(byte[] bytes, bool cursor)
{
    if (rejectNextHostPacket.HasValue && Wire.TryDecode(bytes, cursor, out var packet) &&
        packet.Kind == rejectNextHostPacket)
    {
        rejectNextHostPacket = null;
        return false;
    }
    if (dropNextHostPacket.HasValue && Wire.TryDecode(bytes, cursor, out var lost) && lost.Kind == dropNextHostPacket)
    {
        dropNextHostPacket = null;
        return true;
    }
    toGuest.Enqueue((bytes, cursor));
    return true;
}

void Connect(double now)
{
    host.Connect(now);
    guest.Connect(now);
    guest.Tick(now);
    Drain(now);
    Check(host.State == LinkState.Connected && guest.State == LinkState.Connected, "handshake");
}

void Drain(double now)
{
    for (int round = 0; round < 8; round++)
    {
        while (toHost.TryDequeue(out var h)) host.Receive(h.Data, h.Cursor, now);
        while (toGuest.TryDequeue(out var g)) guest.Receive(g.Data, g.Cursor, now);
    }
    Check(toGuest.Count == 0 && toHost.Count == 0, "message queues settled");
}

static void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
}

static void CheckWorldReload(byte[] fingerprint)
{
    var toHost = new Queue<byte[]>();
    var toGuest = new Queue<byte[]>();
    var host = new CoopSession(true, fingerprint, (data, _) => { toGuest.Enqueue(data); return true; });
    var guest = new CoopSession(false, fingerprint, (data, _) => { toHost.Enqueue(data); return true; });
    void Drain(double t)
    {
        while (toGuest.Count > 0 || toHost.Count > 0)
        {
            while (toHost.TryDequeue(out var command)) host.Receive(command, false, t);
            while (toGuest.TryDequeue(out var update)) guest.Receive(update, false, t);
        }
    }

    host.Connect(0);
    guest.Connect(0);
    guest.Tick(0);
    Drain(0);
    guest.ChangeTile(3, 0.5);
    byte[] previousAction = toHost.Dequeue();
    var hostStamp = host.Native.CaptureStamp();
    var guestStamp = guest.Native.CaptureStamp();
    Check(host.BeginWorldReload(1) && !guest.BeginWorldReload(1) && !host.BeginWorldReload(1), "only the host can stage one reload");
    Check(!hostStamp.IsCurrent(host.Native) && !host.Receive(previousAction, false, 1), "stale host action refused immediately");
    host.Tick(1);
    toGuest.Clear(); // Drop the first reload notification.
    host.Tick(2);
    byte[] firstWelcome = toGuest.Dequeue();
    Check(guest.Receive(firstWelcome, false, 2) && !guestStamp.IsCurrent(guest.Native) && guest.WorldGeneration == 1,
        "retry rotates the guest world while preserving the connected room");
    toHost.Clear(); // Drop the first acknowledgement.
    host.Tick(3);
    Drain(3);
    Check(host.State == LinkState.Connected && guest.State == LinkState.Connected && !host.WorldReloadPending,
        "duplicate notification supplies a replacement acknowledgement without rejoining");
    guest.ChangeTile(4, 4);
    Drain(4);
    Check(host.Tiles == 16 && guest.Tiles == 16, "ordinary requests work on the replacement epoch");

    Check(host.BeginWorldReload(5), "second checkpoint reload can start in the same room");
    host.Tick(5);
    Drain(5);
    Check(guest.WorldGeneration == 2 && !guest.Receive(firstWelcome, false, 5.1) && !host.WorldReloadPending && host.Tiles == 0,
        "late reload from the former generation cannot roll back a newer world");
    guest.Tick(26);
    host.Tick(26);
    Check(host.State == LinkState.Connected && guest.State == LinkState.Connected,
        "a slow announced host load survives twenty seconds without receiving new packets");
    toHost.Clear();
    toGuest.Clear();
    guest.Tick(66);
    host.Tick(66);
    Check(host.State == LinkState.TimedOut && guest.State == LinkState.TimedOut,
        "a stalled announced load still expires after the bounded grace period");
    Console.WriteLine("PASS in-room reload handshake: lost start and acknowledgement, old action and announcement, repeat reload and connected room");
}

static void CheckNativeProjectionGrace(byte[] fingerprint)
{
    foreach (bool dropCheckpointAck in new[] { false, true })
    {
        var toHost = new Queue<byte[]>();
        var toGuest = new Queue<byte[]>();
        var host = new CoopSession(true, fingerprint, (data, _) => { toGuest.Enqueue(data); return true; });
        var guest = new CoopSession(false, fingerprint, (data, _) => { toHost.Enqueue(data); return true; });
        void Drain(double time)
        {
            while (toGuest.Count > 0 || toHost.Count > 0)
            {
                while (toHost.TryDequeue(out var command)) host.Receive(command, false, time);
                while (toGuest.TryDequeue(out var update)) guest.Receive(update, false, time);
            }
        }

        host.Connect(0);
        guest.Connect(0);
        guest.Tick(0);
        Drain(0);
        host.Native.PublishCheckpoint(NativeSnapshotBinary.Pack(new JObject { ["day"] = 75 }));
        host.Tick(1);
        if (dropCheckpointAck)
        {
            while (toGuest.TryDequeue(out var update)) guest.Receive(update, false, 1);
            toHost.Clear();
        }
        else Drain(1);
        Check(guest.Native.FullRevision != 0 && (host.Native.FullRevision == 0) == dropCheckpointAck,
            "native checkpoint received with or without delivery of its final acknowledgement");
        Check(host.Native.HasFullCheckpointTraffic && guest.Native.HasFullCheckpointTraffic,
            "both peers observe progress from the complete checkpoint transfer");

        host.Tick(15);
        guest.Tick(15);
        Check(host.State == LinkState.Connected && guest.State == LinkState.Connected,
            "a fifteen-second native scene stall cannot disconnect either peer");
        if (dropCheckpointAck)
        {
            host.Tick(62);
            guest.Tick(62);
            Check(host.State == LinkState.TimedOut && guest.State == LinkState.TimedOut,
                "an interrupted initial projection expires after the loading grace period");
            continue;
        }

        guest.MarkNativeProjectionReady();
        guest.Native.PublishPresence(new byte[8]);
        Drain(15.1);
        host.Tick(27.2);
        guest.Tick(27.2);
        Check(host.State == LinkState.TimedOut && guest.State == LinkState.TimedOut,
            "ready players return to the ordinary disconnect timeout");
    }
    Console.WriteLine("PASS native projection loading grace: delayed scene, lost acknowledgement, ready or interrupted peer");
}

static Process StartPeer(string role, string port = null)
{
    var info = new ProcessStartInfo("dotnet")
    {
        UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = role == "--fixture-guest"
    };
    info.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    info.ArgumentList.Add(role);
    if (port != null) info.ArgumentList.Add(port);
    return Process.Start(info);
}

static async Task PumpUntil(LoopbackLink host, Stopwatch clock, Func<bool> done, double limit,
    string purpose, Action<double> onTick = null)
{
    double deadline = clock.Elapsed.TotalSeconds + limit;
    while (!done())
    {
        double now = clock.Elapsed.TotalSeconds;
        Check(now < deadline, purpose);
        host.Tick(now);
        onTick?.Invoke(now);
        await Task.Delay(15);
    }
}

static async Task<string> RunHeadless(LoopbackLink host, Stopwatch clock, Action<double> onTick,
    params string[] commands)
{
    using var process = StartPeer("--fixture-guest", host.Code);
    try
    {
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        foreach (string command in commands) await process.StandardInput.WriteLineAsync(command);
        process.StandardInput.Close();
        await PumpUntil(host, clock, () => process.HasExited, 15, "headless process completed within timeout", onTick);
        Check(process.ExitCode == 0, await errors);
        host.Tick(clock.Elapsed.TotalSeconds);
        return await output;
    }
    finally { if (!process.HasExited) process.Kill(); }
}

static async Task CheckDirectPeers(string address)
{
    using var server = StartPeer("host", address);
    try
    {
        var listening = await server.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Check(listening?.StartsWith("READY ") == true, "local host ready");
        using var client = StartPeer("guest", listening[6..]);
        try
        {
            await Task.WhenAll(server.WaitForExitAsync(), client.WaitForExitAsync()).WaitAsync(TimeSpan.FromSeconds(15));
            var hostLog = await server.StandardOutput.ReadToEndAsync();
            var guestLog = await client.StandardOutput.ReadToEndAsync();
            Console.Write(hostLog);
            Console.Write(guestLog);
            Check(server.ExitCode == 0 && client.ExitCode == 0,
                $"local peers: {await server.StandardError.ReadToEndAsync()} {await client.StandardError.ReadToEndAsync()}");
            Console.WriteLine(address == null ? "PASS two processes over loopback UDP" :
                "PASS two processes over authenticated direct IPv4 UDP");
        }
        finally { if (!client.HasExited) client.Kill(); }
    }
    finally { if (!server.HasExited) server.Kill(); }
}

void RunPeer(bool isHost, string code)
{
    using var link = new LoopbackLink(build, 0, isHost ? null : code,
        isHost && code != null ? IPAddress.Parse(code) : null);
    if (isHost)
        Console.WriteLine($"READY {link.Code}");
    var session = link.Session;
    bool sawCursor = false;
    bool applied = false;
    session.RemoteCursor += packet => sawCursor |= packet.X == 314 && packet.Y == 271 && packet.Flags == 3;
    var timer = Stopwatch.StartNew();
    double doneAt = double.PositiveInfinity;
    while (timer.Elapsed.TotalSeconds < 9)
    {
        double now = timer.Elapsed.TotalSeconds;
        link.Tick(now);
        if (session.State == LinkState.Connected)
        {
            if (!applied && (isHost || session.Revision >= 1))
            {
                session.ChangeTile(isHost ? 4 : 3, now);
                applied = true;
            }
            session.MoveCursor(314, 271, true, true);
            if (session.Revision == 2 && session.Tiles == 24 && sawCursor)
                if (double.IsPositiveInfinity(doneAt)) doneAt = now;
        }
        if (now - doneAt >= 0.3)
        {
            Console.WriteLine($"PASS {(isHost ? "host" : "guest")} pid={Environment.ProcessId} rev=2 tiles=24 remoteCursor=true");
            return;
        }
        Thread.Sleep(15);
    }
    throw new InvalidOperationException($"local {isHost} stopped at {session.State}/{session.Revision}");
}

sealed class TestShop : IShopAuthority
{
    internal int Captures;
    internal int Cash = 550;
    internal int Moves;
    internal int Days;
    internal int Trades;
    internal bool EndDayOpen;
    internal bool Full;
    internal uint Held;
    internal bool Outside;
    internal byte Searches, LootPlace, CounterGoodsPlace;
    internal bool CounterOffer;
    internal uint DialogueId;
    internal int ChoicesMade;
    internal ShopPlacement LastPlacement;
    internal ulong NightServices = 1;

    public ShopView Capture()
    {
        Captures++;
        var view = new ShopView
        {
            Active = true,
            HeldItem = Held,
            CanGoOut = !Outside,
            GuestOutside = Outside,
            SearchesLeft = Outside ? Searches : (byte)0,
            Day = Days + 1,
            Cash = Cash,
            Phase = EndDayOpen ? ShopDay.Ready : Days > 0 ? ShopDay.Saved : ShopDay.Playing,
            NightServices = EndDayOpen ? NightServices : 0,
            NightCost = EndDayOpen ? 5 : 0,
            ServiceCosts = EndDayOpen ? new[] { 5 } : Array.Empty<int>(),
            MorningReport = !EndDayOpen && Days > 0 ? new[] { "夜间电费 -5" } : Array.Empty<string>(),
            OfferId = EndDayOpen || Trades >= 2 ? 0u : (uint)(99 + Trades),
            OfferAction = EndDayOpen || Trades >= 2 ? 0 : Trades == 0 ? ShopAction.Buy : ShopAction.Sell,
            OfferLabel = EndDayOpen || Trades >= 2 ? "" : "瓶装水 ×1",
            Price = EndDayOpen || Trades >= 2 ? 0 : Trades == 0 ? 38 : 18,
            Items = { new ShopItem { Id = 7, Quantity = 1, Place = (byte)(Moves % 2 == 0 ? 1 : 2), Owned = true, Movable = true } }
        };
        if (LootPlace != 0) view.Items.Add(new ShopItem { Id = 81, Quantity = 1, Place = LootPlace });
        if (CounterGoodsPlace != 0)
        {
            view.Shutter = 1;
            view.Customer = "顾客";
            view.CustomerSprite = "default";
            view.OfferId = CounterOffer ? 500u : 0u;
            view.OfferAction = CounterOffer ? ShopAction.Buy : 0;
            view.OfferLabel = CounterOffer ? "样品 ×1" : "";
            view.Price = CounterOffer ? 15 : 0;
            view.Items.Add(new ShopItem
            {
                Id = 82, Quantity = 1, Place = CounterGoodsPlace, Owned = CounterGoodsPlace == 1, Movable = true
            });
        }
        if (DialogueId != 0)
        {
            view.DialogueId = DialogueId;
            view.DialogueSpeaker = 1;
            view.DialogueLine = "继续交易吗？";
            view.DialogueChoices = new[] { "取消", "继续" };
            view.DialogueChoicesEnabled = 3;
        }
        if (Full)
        {
            for (uint id = 2; id <= 80; id++)
            {
                if (id == 7) continue;
                view.Items.Add(new ShopItem
                {
                    Id = id, Quantity = 1, Place = 1, Width = 1, Height = 1, Cells = 1,
                    Label = new string('L', 64), Atlas = new string('A', 48), Sprite = new string('S', 48)
                });
            }
            view.Items.Add(new ShopItem { Id = 1, Quantity = 1, Place = 1 });
        }
        return view;
    }

    public ShopResult Apply(ShopAction action, uint entity, ShopPlacement placement)
    {
        if (action == ShopAction.ChooseDialogue)
        {
            if (DialogueId == 0 || entity >> 8 != DialogueId || (byte)entity != 1) return ShopResult.Stale;
            DialogueId = 0;
            ChoicesMade++;
            return ShopResult.Applied;
        }
        if (action == ShopAction.GoScavenge && !Outside)
        {
            Outside = true;
            Searches = 2;
            return ShopResult.Applied;
        }
        if (action == ShopAction.Scavenge && Outside && Searches > 0)
        {
            Searches--;
            LootPlace = 3;
            return ShopResult.Applied;
        }
        if (action == ShopAction.TakeLoot && Outside && entity == 81 && LootPlace == 3)
        {
            LootPlace = 4;
            return ShopResult.Applied;
        }
        if (action == ShopAction.ReturnFromScavenge && Outside)
        {
            Outside = false;
            LootPlace = LootPlace == 4 ? (byte)1 : (byte)0;
            return ShopResult.Applied;
        }
        if (action == ShopAction.PickUp && entity == 82 && Held == 0 && CounterGoodsPlace == 6)
        {
            Held = entity;
            return ShopResult.Applied;
        }
        if (action == ShopAction.Place && entity == 82 && Held == entity && placement.Container == 2)
        {
            Held = 0;
            CounterGoodsPlace = 2;
            return ShopResult.Applied;
        }
        if (action == ShopAction.SelectOffer && entity == 82 && CounterGoodsPlace == 2)
        {
            CounterOffer = true;
            return ShopResult.Applied;
        }
        if (action == ShopAction.Buy && entity == 500 && CounterOffer)
        {
            CounterOffer = false;
            CounterGoodsPlace = 1;
            Cash -= 15;
            return ShopResult.Applied;
        }
        if (action == ShopAction.PickUp && entity == 7 && Held == 0)
        {
            Held = entity;
            return ShopResult.Applied;
        }
        if (action is ShopAction.Place or ShopAction.Release && Held == entity && entity != 0)
        {
            Held = 0;
            if (action == ShopAction.Place)
            {
                LastPlacement = placement;
                Moves++;
            }
            return ShopResult.Applied;
        }
        if (action == ShopAction.Move && entity == 7)
        {
            Moves++;
            return ShopResult.Applied;
        }
        if (!EndDayOpen && action == ShopAction.Buy && Trades == 0 && entity == 99)
        {
            Trades++;
            Cash -= 38;
            return ShopResult.Applied;
        }
        if (!EndDayOpen && action == ShopAction.Sell && Trades == 1 && entity == 100)
        {
            Trades++;
            Cash += 18;
            return ShopResult.Applied;
        }
        if (action == ShopAction.FinishDay && EndDayOpen)
        {
            EndDayOpen = false;
            Days++;
            Cash -= 5;
            return ShopResult.Applied;
        }
        return ShopResult.Unavailable;
    }
}
