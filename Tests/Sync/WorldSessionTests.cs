using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using ProbablyCoop.Net;
using ProbablyCoop.Sync;

namespace ProbablyCoop.SyncTests;

internal static class WorldSessionTests
{
    private const int ItemCount = 1200;
    private static readonly EntityKey Cash = EntityKey.Scalar("playerCash");

    internal static int Run()
    {
        try
        {
            RoundTrip();
            TeamBoundaries();
            AuthorityIntents();
            Limits();
            Console.WriteLine("PASS world-session");
            return 0;
        }
        catch (InvalidOperationException error)
        {
            Console.WriteLine("FAIL world-session: " + error.Message);
            return 1;
        }
    }

    private static void AuthorityIntents()
    {
        var offer = new BusinessIntent(BusinessAction.Estimate, customer: 14, revision: 27, item: 55, value: 600);
        var business = new AuthorityRequest(AuthorityAction.Business, 3, 2, 41, business: offer);
        Check(AuthorityRequest.TryRead(business.Encode(), out var decoded) && decoded.Day == 3 &&
            decoded.Epoch == 2 && decoded.Serial == 41 && decoded.Business.Action == BusinessAction.Estimate &&
            decoded.Business.Customer == 14 && decoded.Business.Revision == 27 &&
            decoded.Business.Item == 55 && decoded.Business.Value == 600, "appraisal retains the native offer and quote");
        var shot = new AuthorityRequest(AuthorityAction.Business, 3, 2, 42,
            business: new BusinessIntent(BusinessAction.Fire, 14, 0));
        Check(!AuthorityRequest.TryRead(shot.Encode(), out _), "a shot requires the current incident revision");
        var recipe = new MachineIntent(44, 0, MachineControl.Recipe, 138, choice: "dream_dust");
        var machine = new AuthorityRequest(AuthorityAction.Machine, 3, 2, 43, machine: recipe);
        Check(AuthorityRequest.TryRead(machine.Encode(), out decoded) && decoded.Machine.Owner == 44 &&
            decoded.Machine.Proof == 138 && decoded.Machine.Choice == "dream_dust", "machine option keeps its owner and proof");
        var invalid = new AuthorityRequest(AuthorityAction.Machine, 3, 2, 44,
            machine: new MachineIntent(44, 0, MachineControl.Range, 138, float.NaN, 25));
        Check(!AuthorityRequest.TryRead(invalid.Encode(), out _) &&
            !AuthorityRequest.TryRead(machine.Encode().Concat(new byte[] { 1 }).ToArray(), out _),
            "invalid range and trailing data are refused");
        var activation = new AuthorityRequest(AuthorityAction.Item, 3, 2, 45, itemUse: new ItemIntent(ItemUse.Activate, 50, 134));
        Check(AuthorityRequest.TryRead(activation.Encode(), out decoded) && decoded.ItemUse.Item == 50 && decoded.ItemUse.Proof == 134 &&
            decoded.Epoch == 2 && decoded.Serial == 45, "stock activation carries its exact item and context");
        var missingProof = new AuthorityRequest(AuthorityAction.Item, 3, 2, 45, itemUse: new ItemIntent(ItemUse.Activate, 50, 0));
        Check(!AuthorityRequest.TryRead(missingProof.Encode(), out _), "stock activation requires a context proof");
        var invalidSlot = new AuthorityRequest(AuthorityAction.Item, 3, 2, 46, itemUse: new ItemIntent(ItemUse.RevealSymbol, 50, 134, 4));
        Check(!AuthorityRequest.TryRead(invalidSlot.Encode(), out _), "a scratch action must name one of the three symbols");
        var dial = new AuthorityRequest(AuthorityAction.Activity, 3, 2, 47,
            activity: new ActivityIntent(ActivityAction.Dial, 12, value: 8376));
        Check(AuthorityRequest.TryRead(dial.Encode(), out decoded) && decoded.Activity.Value == 8376 &&
            decoded.Activity.Action == ActivityAction.Dial && decoded.Activity.Proof == 12, "native phone action keeps its number and context");
        var preset = new string('x', 4096);
        var settings = new AuthorityRequest(AuthorityAction.Activity, 3, 2, 48,
            activity: new ActivityIntent(ActivityAction.LogisticsSetting, 13, owner: 110, text: preset));
        Check(AuthorityRequest.TryRead(settings.Encode(), out decoded) && decoded.Activity.Owner == 110 &&
            decoded.Activity.Text == preset, "native coordinator settings keep their item and full bounded text");
        var oversized = new AuthorityRequest(AuthorityAction.Activity, 3, 2, 49,
            activity: new ActivityIntent(ActivityAction.LogisticsSetting, 13, owner: 110, text: new string('界', 3000)));
        var invalidChoice = new AuthorityRequest(AuthorityAction.Activity, 3, 2, 50,
            activity: new ActivityIntent(ActivityAction.PhoneChoice, 12, value: 16));
        Check(!AuthorityRequest.TryRead(oversized.Encode(), out _) && !AuthorityRequest.TryRead(invalidChoice.Encode(), out _),
            "settings use a byte limit and phone choices use a bounded index");
        var production = new AuthorityRequest(AuthorityAction.Activity, 3, 2, 51,
            activity: new ActivityIntent(ActivityAction.ProductionTarget, 14, owner: 31, value: 32));
        Check(AuthorityRequest.TryRead(production.Encode(), out decoded) &&
            decoded.Activity.Action == ActivityAction.ProductionTarget && decoded.Activity.Owner == 31 &&
            decoded.Activity.Value == 32 && !new ActivityIntent(ActivityAction.ProductionTarget, 14, owner: 31, value: 31).Valid,
            "production uses two distinct original objects");
    }

    private static void TeamBoundaries()
    {
        int day = 1;
        bool hostMayDepart = true;
        byte acceptedActor = byte.MaxValue;
        using var host = new Endpoint((action, actor) =>
        {
            acceptedActor = actor;
            return action.Day == day &&
                (action.Kind != TeamTransitionKind.Depart || actor != SyncHost.HostPeer || hostMayDepart);
        }, (_, backpack) => backpack is 0 or 31 or 32);
        using var guest = new Endpoint((_, _) => false);
        var clock = Stopwatch.StartNew();
        void Wait(Func<bool> condition)
        {
            double deadline = clock.Elapsed.TotalSeconds + 5;
            while (!condition())
            {
                double now = clock.Elapsed.TotalSeconds;
                Check(now < deadline, "team transition timed out");
                host.Tick(now);
                guest.Tick(now);
                Thread.Sleep(2);
            }
        }
        void Load()
        {
            byte[] data = null;
            ulong ticket = 0;
            Wait(() => guest.Session.TryTakeCheckpoint(out data, out ticket));
            guest.World.Load(data);
            Check(guest.Session.FinishLoading(ticket, true), "install team boundary checkpoint");
        }
        host.World.Items.Add(1, 1);
        host.Room.Create(0);
        guest.Room.Join(0, host.Room.Code);
        Load();
        var trip = new TeamTransition(TeamTransitionKind.Depart, day, "dumping_ground");
        var night = new TeamTransition(TeamTransitionKind.Sleep, day);
        Check(!new TeamTransition(TeamTransitionKind.Depart, day, "../shop").Valid, "invalid scene identifier");
        var appraisal = new AuthorityRequest(AuthorityAction.SetAppraisalCategory, day, 0, 1, 32, "hand", true);
        Check(guest.Session.Request(appraisal.Action, day, appraisal.Item, appraisal.Category, appraisal.Allowed),
            "submit the typed native action");
        AuthorityRequest received = default;
        Wait(() => host.Session.TryTakeRequest(out received));
        Check(received.Serial == appraisal.Serial && received.Item == 32 && received.Allowed && received.Category == "hand",
            "decode the authenticated authority request");
        Check(guest.Room.Send(WorldWire.Channel, appraisal.Encode()), "queue duplicate action");
        Check(guest.Session.Guest.Hold(EntityKey.Item(1)), "hold the pending item");
        Check(guest.Session.Vote(trip, 31), "guest proposes a departure with a backpack");
        Wait(() => host.Session.ReadyMask == 2 && guest.Session.ReadyMask == 2);
        Check(!host.Session.TryTakeRequest(out _), "a duplicate cannot execute twice");
        Check(!host.Session.TryBeginTransition(out _, out _) && !host.Session.Vote(night),
            "one vote or a competing action cannot start a transition");
        guest.Session.WithdrawDeparture();
        Wait(() => host.Session.ReadyMask == 0 && guest.Session.ReadyMask == 0);
        Check(host.Session.GuestBackpack == 0 && host.Session.Proposal.Kind == TeamTransitionKind.None,
            "closing the guest map withdraws its prepared trip and releases the backpack");
        Check(host.Session.Vote(trip, 31), "host can invite the guest");
        Wait(() => guest.Session.ReadyMask == 1);
        ulong cancelled = guest.Session.ProposalRevision;
        Check(cancelled != 0 && guest.Session.HostBackpack == 31, "guest can see the host's reserved backpack");
        Check(guest.Session.TryVote(trip, 31, cancelled) == TeamVoteFailure.None, "send conflicting guest selection");
        TeamVoteFailure refusal = default;
        Wait(() => (refusal = guest.Session.TakeVoteFailure()) != TeamVoteFailure.None);
        Check(refusal == TeamVoteFailure.BackpackTaken && guest.Session.ReadyMask == 1,
            "guest gets the backpack conflict and can still choose");
        Check(guest.Session.CancelProposal(trip, cancelled) == TeamVoteFailure.None, "an undecided partner can decline");
        Wait(() => host.Session.ReadyMask == 0 && guest.Session.ReadyMask == 0);
        hostMayDepart = false;
        Check(guest.Session.Vote(trip, 31), "guest can choose a trip again after cancelling");
        Wait(() => host.Session.ReadyMask == 2 && guest.Session.ReadyMask == 2);
        ulong current = host.Session.ProposalRevision;
        Check(current != cancelled && host.Session.TryVote(trip, 32, cancelled) == TeamVoteFailure.Stale &&
            host.Session.CancelProposal(trip, cancelled) == TeamVoteFailure.Stale,
            "a cancelled invitation cannot confirm or cancel a new invitation to the same destination");
        Check(host.Session.TryVote(trip, 31, current) == TeamVoteFailure.BackpackTaken,
            "two players cannot select the same physical backpack");
        Check(host.Session.TryVote(trip, 32, current) == TeamVoteFailure.None && !host.Session.TryBeginTransition(out _, out _),
            "both votes still wait for inventory reservations");
        host.Session.WithdrawDeparture();
        Wait(() => guest.Session.ReadyMask == 2);
        Check(host.Session.HostBackpack == 0 && host.Session.GuestBackpack == 31,
            "closing the host map keeps the guest's vote");
        Check(host.Session.Vote(trip, 32), "host can confirm again");
        guest.Session.Guest.Release(EntityKey.Item(1));
        TeamTransition started = default;
        byte initiator = byte.MaxValue;
        Wait(() => host.Session.TryBeginTransition(out started, out initiator));
        Check(started.Equals(trip) && initiator == 1 && acceptedActor == initiator,
            "guest's restrictions apply when they initiated the trip");
        Check(host.Session.HostBackpack == 32 && host.Session.GuestBackpack == 31, "each vote binds its actor's own selection");
        Wait(() => guest.Session.Guest.Closed);
        Check(!guest.Session.Vote(trip) && !host.Session.TryBeginTransition(out _, out _) &&
            host.Session.CancelProposal(trip, current) == TeamVoteFailure.Busy,
            "a closed barrier rejects repeated confirmation");
        Check(host.Session.CompleteTransition(host.World.Save()), "complete one shared departure");
        Load();
        Check(guest.Session.Guest.Barrier == 1 && guest.Session.ReadyMask == 0, "new scene resets confirmations");
        Check(guest.Room.Send(WorldWire.Channel, trip.EncodeVote(0)), "queue stale trip packet");
        var stale = new AuthorityRequest(AuthorityAction.Scavenge, day, 0, 2);
        Check(guest.Room.Send(WorldWire.Channel, stale.Encode()), "queue request from an expired scene");
        day++;
        Check(!host.Session.Vote(night), "old-day votes do not advance the clock");
        night = new TeamTransition(TeamTransitionKind.Sleep, day);
        Check(host.Session.Vote(night), "new day accepts sleep proposal");
        Wait(() => guest.Session.ReadyMask == 1 && guest.Session.Proposal.Equals(night));
        Check(!host.Session.TryTakeRequest(out _), "old scene command cannot execute in the new scene");
        Check(guest.Session.Vote(night), "guest confirms same night in new epoch");
        Wait(() => host.Session.TryBeginTransition(out started, out initiator));
        Check(started.Equals(night) && initiator == 0, "stale trip did not overwrite sleep");
        guest.Room.Leave();
        Wait(() => host.Session.Host == null);
        Check(host.Session.CompleteTransition(host.World.Save()), "a native transition may finish after disconnect");
        guest.Room.Join(clock.Elapsed.TotalSeconds, host.Room.Code);
        Load();
        Check(guest.Session.ReadyMask == 0, "reconnection starts without stale confirmations");
    }

    private static void RoundTrip()
    {
        using var host = new Endpoint();
        using var guest = new Endpoint();
        for (int item = 1; item <= ItemCount; item++) host.World.Items.Add(item, item);
        var clock = Stopwatch.StartNew();
        double Now() => clock.Elapsed.TotalSeconds;
        void Pump() { host.Tick(Now()); guest.Tick(Now()); }
        void Wait(Func<bool> done, string label)
        {
            double until = Now() + 5;
            while (!done())
            {
                Check(Now() < until, label);
                Pump();
                Thread.Sleep(2);
            }
        }
        void Load(byte[] bytes, ulong ticket)
        {
            guest.World.Load(bytes);
            Check(guest.Session.FinishLoading(ticket, true), "guest load completion");
        }

        host.Room.Create(Now());
        guest.Room.Join(Now(), host.Room.Code);
        Wait(() => host.Room.IsReady && guest.Room.IsReady, "room handshake");
        Check(!guest.Session.Guest.Aligned, "room readiness is not world readiness");
        byte[] initial = null;
        ulong initialTicket = 0;
        Wait(() => guest.Session.TryTakeCheckpoint(out initial, out initialTicket), "initial checkpoint");
        int checkpointBytes = guest.LastCheckpointBytes;
        HostAction(host, Move(1, 2001));
        Wait(() => guest.LastCommitBytes != 0, "commit received during load");
        Check(guest.World.Items.Count == 0 && !guest.Session.Guest.Aligned, "deltas wait for load");
        Load(initial, initialTicket);
        Check(guest.World.At(1) == 2001 && guest.Session.Guest.Sequence == 1, "queued delta applied after load");

        GuestAction(guest, Move(2, 2002));
        Check(guest.World.At(2) == 2002 && host.World.At(2) == 2, "guest updates before the network is polled");
        int guestApplies = guest.World.Applies;
        Wait(() => guest.Session.Guest.PendingCount == 0, "guest action acknowledged");
        Check(host.World.At(2) == 2002 && guest.World.Applies == guestApplies, "own commit is not applied twice");
        double idleUntil = Now() + 1;
        Wait(() => Now() >= idleUntil, "idle traffic sample");
        Check(host.Captures == 1, "idle and ordinary moves do not recapture a checkpoint");
        Console.WriteLine($"info world-wire simulatedItems={ItemCount} checkpoint={checkpointBytes} B " +
            $"singleMoveRequest={host.LastRequestBytes} B commit={guest.LastCommitBytes} B");

        GuestAction(guest, Move(3, 2003));
        HostAction(host, Move(4, 2004));
        Wait(() => guest.Session.Guest.PendingCount == 0, "independent concurrent moves");
        Check(guest.World.At(4) == 2004 && host.World.At(3) == 2003, "disjoint changes both applied");

        var bounces = new List<SyncVerdict>();
        guest.Session.Guest.Bounced += (_, reason) => bounces.Add(reason);
        GuestAction(guest, Move(5, 2005));
        HostAction(host, Move(5, 3005));
        Wait(() => bounces.Count == 1 && guest.Session.Guest.Sequence == host.Session.Host.Sequence, "same-item conflict");
        Check(bounces[0] == SyncVerdict.Stale && guest.World.At(5) == 3005, "host value repairs stale local value");

        host.World.CashFloor = 75;
        GuestAction(guest, Move(6, 2006), SyncOp.Add(Cash, -40));
        Wait(() => bounces.Count == 2 && guest.Session.Guest.Sequence == host.Session.Host.Sequence, "atomic refusal");
        Check(bounces[1] == SyncVerdict.Refused && host.World.At(6) == 6 && guest.World.At(6) == 6 &&
            guest.World.Balance == 100 && host.World.Balance == 100, "refused movement and cash were both repaired");
        host.World.CashFloor = 0;

        Check(guest.Session.Guest.Hold(EntityKey.Item(7)), "hold request sent");
        Wait(() => host.Session.Host.IsHeldByOther(EntityKey.Item(7)), "peer can see hold");
        Check(!host.Session.Host.Hold(EntityKey.Item(7)), "cannot acquire another player's held item");
        guest.Session.Guest.Release(EntityKey.Item(7));
        Wait(() => !host.Session.Host.IsHeldByOther(EntityKey.Item(7)), "release visible to peer");
        Check(guest.Session.Guest.Hold(EntityKey.Item(-1)) && guest.Session.Guest.Hold(EntityKey.Item(7)),
            "exercise unknown and valid hold requests");
        Wait(() => host.Session.Host.IsHeldByOther(EntityKey.Item(7)), "valid hold arrives after unknown one");
        Check(!host.Session.Host.IsHeldByOther(EntityKey.Item(-1)), "unknown entity cannot reserve a grip");

        var gap = new SyncTx { Serial = 1, Ops = new List<SyncOp> { Move(8, 2008) } };
        int captures = host.Captures;
        Check(host.Room.Send(WorldWire.Channel, SyncCodec.Commit(host.Session.Host.Sequence + 2, gap)), "inject lost commit gap");
        byte[] repair = null;
        ulong repairTicket = 0;
        Wait(() => guest.Session.TryTakeCheckpoint(out repair, out repairTicket), "request and receive recovery checkpoint");
        Check(host.Captures == captures + 1 && !guest.Session.Guest.Aligned &&
            !host.Session.Host.IsHeldByOther(EntityKey.Item(7)) &&
            !guest.Session.Guest.Local(new List<SyncOp> { Move(8, 3008) }), "one recovery request and local edits blocked");
        Load(repair, repairTicket);

        // Both frontends can optimistically use one empty slot. If the winning
        // commit cannot be applied over the guest's provisional placement,
        // the guest must converge before accepting further local edits.
        captures = host.Captures;
        GuestAction(guest, Move(8, 2008));
        HostAction(host, Move(11, 2008));
        Wait(() => guest.Session.TryTakeCheckpoint(out repair, out repairTicket), "same-slot race realigns");
        Load(repair, repairTicket);
        Check(host.Captures == captures + 1 && guest.World.At(8) == 8 && guest.World.At(11) == 2008,
            "same-slot race keeps only the authoritative placement");
        GuestAction(guest, Move(8, 2018));
        Wait(() => guest.Session.Guest.PendingCount == 0, "guest can retry after same-slot realign");

        host.Session.Host.Close();
        Wait(() => guest.Session.Guest.Closed, "barrier visible");
        uint before = host.Session.Host.Barrier;
        Check(!host.Session.Host.Complete(host.World.Save(), 4) && host.Session.Host.Closed &&
            host.Session.Host.Barrier == before, "oversized checkpoint keeps barrier closed");
        Check(host.Session.Host.Complete(host.World.Save(), NetStream.MaxMessage), "barrier checkpoint sent");
        Wait(() => guest.Session.TryTakeCheckpoint(out repair, out repairTicket), "barrier checkpoint received");
        Check(!guest.Session.FinishLoading(initialTicket, true), "old load completion ignored");
        Load(repair, repairTicket);
        Check(guest.Session.Guest.Barrier == before + 1 && !guest.Session.Guest.Closed, "both sides left barrier");

        SyncGuestChannel previousGuest = guest.Session.Guest;
        guest.Room.Leave();
        Wait(() => guest.Session.Guest == null && host.Session.Host == null && host.Room.HasLobby, "guest leaves and host relistens");
        host.World.Items[9] = 2009;
        guest.Room.Join(Now(), host.Room.Code);
        Wait(() => guest.Session.TryTakeCheckpoint(out repair, out repairTicket), "rejoin takes current checkpoint");
        Load(repair, repairTicket);
        Check(!previousGuest.Hold(EntityKey.Item(9)), "stale channel cannot send into the new room");
        GuestAction(guest, Move(10, 2010));
        Wait(() => guest.Session.Guest.PendingCount == 0, "new connection accepts first serial");
        Check(host.World.Save().SequenceEqual(guest.World.Save()), "worlds converge after rejoin");

        Check(guest.Room.Send(WorldWire.Channel, new byte[] { WorldWire.Checkpoint }), "wrong-direction checkpoint sent");
        Wait(() => !host.Room.HasLobby && !guest.Room.IsReady, "protocol fault closes the session");
    }

    private static void Limits()
    {
        var world = new TestWorld();
        var receiver = new SyncGuestChannel(1, _ => true, world);
        Check(receiver.Receive(WorldWire.EncodeCheckpoint(0, 0, world.Save())), "prepare loading queue");
        byte[] frame = SyncCodec.Hold(0, EntityKey.Item(1), true);
        for (int i = 0; i < SyncGuestChannel.MaxQueued; i++) Check(receiver.Receive(frame), "queue capacity");
        Check(!receiver.Receive(frame), "loading queue is bounded");
        Check(receiver.Receive(WorldWire.EncodeCheckpoint(0, 0, world.Save())), "replace loading queue");
        var large = new SyncTx { Serial = 1, Ops = new List<SyncOp> { SyncOp.Put(EntityKey.Item(1), new byte[SyncCodec.MaxData]) } };
        byte[] delta = SyncCodec.Commit(1, large);
        for (int i = 0; i < SyncCodec.MaxFrameBytes / delta.Length; i++) Check(receiver.Receive(delta), "loading bytes capacity");
        Check(!receiver.Receive(delta), "loading byte budget");

        var stream = new NetStream(1, 1200, _ => true, (_, _) => { });
        var block = new byte[NetStream.MaxMessage];
        Check(stream.Send(1, block) && stream.Send(1, block) && !stream.Send(1, frame), "transport bytes are bounded");
        stream.Stop();
        stream = new NetStream(1, 1200, _ => true, (_, _) => { });
        for (int i = 0; i < NetStream.MaxBufferedMessages; i++) Check(stream.Send(1, frame), "send queue capacity");
        Check(!stream.Send(1, frame), "transport message count is bounded");
        stream.Pump(0);
        var ack = new SyncWriter();
        ack.ULong(1);
        ack.ULong(0);
        Check(stream.OnAckFrame(new SyncReader(ack.ToArray()), 0) && stream.Send(1, frame),
            "acknowledgement frees capacity");

        var rejected = new SyncHostChannel((_, _) => false, world);
        Check(!rejected.Attach(1, world.Save(), NetStream.MaxMessage) && !rejected.IsAttached(1),
            "failed checkpoint send does not attach guest");
    }

    private static void HostAction(Endpoint endpoint, params SyncOp[] ops)
    {
        var tx = new SyncTx { Ops = new List<SyncOp>(ops) };
        Check(endpoint.World.TryApply(tx), "host local action");
        endpoint.Session.Host.Local(tx.Ops);
    }

    private static void GuestAction(Endpoint endpoint, params SyncOp[] ops)
    {
        var tx = new SyncTx { Ops = new List<SyncOp>(ops) };
        Check(endpoint.World.TryApply(tx) && endpoint.Session.Guest.Local(tx.Ops), "guest local action");
    }

    private static SyncOp Move(long id, int slot) => SyncOp.Put(EntityKey.Item(id), BitConverter.GetBytes(slot));

    private static void Check(bool ok, string label)
    {
        if (!ok) throw new InvalidOperationException(label);
    }

    private sealed class Endpoint : IDisposable
    {
        internal readonly NetRoom Room = new NetRoom(() => SessionManifest.Of("test", null), null, true);
        internal readonly TestWorld World = new TestWorld();
        internal readonly WorldSession Session;
        internal int Captures, LastCheckpointBytes, LastRequestBytes, LastCommitBytes;

        internal Endpoint(Func<TeamTransition, byte, bool> canTransition = null, Func<byte, int, bool> canCarry = null)
        {
            Session = new WorldSession(Room, World, () => { Captures++; return World.Save(); }, canTransition, canCarry);
            Room.Message += (channel, data) =>
            {
                if (channel != WorldWire.Channel) return;
                if (data[0] == WorldWire.Checkpoint) LastCheckpointBytes = data.Length;
                if (data[0] == (byte)SyncMessage.Tx) LastRequestBytes = data.Length;
                if (data[0] == (byte)SyncMessage.Commit) LastCommitBytes = data.Length;
            };
        }

        internal void Tick(double now)
        {
            Room.Poll(now);
            Session.Tick();
            Room.Flush(now);
        }

        public void Dispose()
        {
            Session.Dispose();
            Room.Dispose();
        }
    }

    private sealed class TestWorld : ISyncWorld
    {
        internal Dictionary<long, int> Items = new Dictionary<long, int>();
        internal long Balance = 100, CashFloor;
        internal int Applies;

        internal int At(long id) => Items[id];

        public byte[] Read(EntityKey key) => Items.TryGetValue(key.Id, out int slot) ? BitConverter.GetBytes(slot) : null;

        public bool CanHold(byte actor, EntityKey key) => key.Space == SyncSpace.Item && Items.ContainsKey(key.Id);

        public bool TryApply(SyncTx tx)
        {
            var changed = new Dictionary<long, int>(Items);
            long nextBalance = Balance;
            foreach (var op in tx.Ops)
            {
                if (op.Key.Equals(Cash) && op.Kind == SyncOpKind.Add) nextBalance += op.Delta;
                else if (op.Key.Space != SyncSpace.Item) return false;
                else if (op.Kind == SyncOpKind.Put && op.Data?.Length == sizeof(int)) changed[op.Key.Id] = BitConverter.ToInt32(op.Data);
                else if (op.Kind == SyncOpKind.Remove) changed.Remove(op.Key.Id);
                else return false;
            }
            if (nextBalance < CashFloor || changed.Values.Distinct().Count() != changed.Count) return false;
            Balance = nextBalance;
            Items = changed;
            Applies++;
            return true;
        }

        public void Revert(SyncTx tx)
        {
            foreach (var op in tx.Ops)
                if (op.Key.Equals(Cash) && op.Kind == SyncOpKind.Add) Balance -= op.Delta;
        }

        public bool TryConfirm(SyncTx tx) => true;

        internal byte[] Save()
        {
            using var output = new MemoryStream();
            using var writer = new BinaryWriter(output);
            writer.Write(Balance);
            foreach (var item in Items.OrderBy(pair => pair.Key))
            {
                writer.Write(item.Key);
                writer.Write(item.Value);
            }
            return output.ToArray();
        }

        internal void Load(byte[] data)
        {
            using var input = new MemoryStream(data);
            using var reader = new BinaryReader(input);
            Balance = reader.ReadInt64();
            Items.Clear();
            while (input.Position < input.Length) Items.Add(reader.ReadInt64(), reader.ReadInt32());
        }
    }
}
