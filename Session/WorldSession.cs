using System;
using System.Collections.Generic;
using ProbablyCoop.Net;
using ProbablyCoop.Sync;

namespace ProbablyCoop;

// Binds the world protocol to one negotiated connection. The caller owns the
// actual world, including the transition that finishes loading a checkpoint.
internal sealed class WorldSession : IDisposable
{
    private readonly NetRoom room;
    private readonly ISyncWorld world;
    private readonly Func<byte[]> capture;
    private readonly Func<TeamTransition, byte, bool> canTransition;
    private readonly Func<byte, int, bool> canCarry;
    private bool needsCheckpoint;
    private bool failed;
    private byte[] incoming;
    private ulong connection;
    private ulong nextLoad;
    private ulong loading;
    private byte initiator;
    private ulong proposalSerial;
    private TeamVoteFailure voteFailure;
    private readonly Queue<AuthorityRequest> authorityRequests = new Queue<AuthorityRequest>();
    private ulong requestSerial;

    internal WorldSession(NetRoom room, ISyncWorld world, Func<byte[]> capture,
        Func<TeamTransition, byte, bool> canTransition = null, Func<byte, int, bool> canCarry = null)
    {
        this.room = room ?? throw new ArgumentNullException(nameof(room));
        this.world = world ?? throw new ArgumentNullException(nameof(world));
        this.capture = capture ?? throw new ArgumentNullException(nameof(capture));
        this.canTransition = canTransition;
        this.canCarry = canCarry;
        room.Ready += OnReady;
        room.Ended += OnEnded;
        room.Message += OnMessage;
        if (room.IsReady) OnReady();
    }

    internal SyncHostChannel Host { get; private set; }
    internal SyncGuestChannel Guest { get; private set; }
    internal TeamTransition Proposal { get; private set; }
    internal ulong ProposalRevision { get; private set; }
    internal byte ReadyMask { get; private set; }
    internal int HostBackpack { get; private set; }
    internal int GuestBackpack { get; private set; }

    internal bool Request(AuthorityAction action, int day, int item = 0, string category = "", bool allowed = false,
        BusinessIntent business = default, MachineIntent machine = default, ItemIntent itemUse = default, ActivityIntent activity = default)
    {
        if (canTransition == null || day <= 0 || failed || Guest?.Aligned != true || Guest.Closed ||
            Guest.PendingCount != 0 || requestSerial == ulong.MaxValue) return false;
        var request = new AuthorityRequest(action, day, Guest.Barrier, requestSerial + 1, item, category, allowed, business, machine,
            itemUse, activity);
        var message = request.Encode();
        if (!AuthorityRequest.TryRead(message, out _)) return false;
        requestSerial = request.Serial;
        return Send(connection, message);
    }

    internal bool TryTakeRequest(out AuthorityRequest request)
    {
        while (!failed && Host != null && authorityRequests.TryDequeue(out request))
            if (request.Epoch == Host.Barrier && !Host.Closed) return true;
        request = default;
        return false;
    }

    internal bool Vote(TeamTransition action, int backpack = 0) => TryVote(action, backpack) == TeamVoteFailure.None;

    internal TeamVoteFailure TryVote(TeamTransition action, int backpack = 0, ulong revision = 0)
    {
        if (failed || canTransition == null || !action.Valid || backpack < 0 ||
            backpack != 0 && action.Kind != TeamTransitionKind.Depart) return TeamVoteFailure.Unavailable;
        if (Host != null)
        {
            var result = RecordVote(action, SyncHost.HostPeer, backpack, revision);
            return Host.IsAttached(NetSession.GuestPeer) && !Host.Closed && !PublishReadiness()
                ? TeamVoteFailure.Busy : result;
        }
        if (Guest?.Aligned != true || Guest.Closed || Guest.PendingCount != 0) return TeamVoteFailure.Busy;
        return Send(connection, action.EncodeVote(Guest.Barrier, backpack, revision))
            ? TeamVoteFailure.None : TeamVoteFailure.Busy;
    }

    internal TeamVoteFailure TakeVoteFailure()
    {
        var result = voteFailure;
        voteFailure = TeamVoteFailure.None;
        return result;
    }

    internal TeamVoteFailure CancelProposal(TeamTransition action, ulong revision)
    {
        if (failed || canTransition == null) return TeamVoteFailure.Busy;
        if (revision == 0 || ProposalRevision != revision || !Proposal.Equals(action)) return TeamVoteFailure.Stale;
        if (Host != null)
        {
            var result = CancelOnHost(action, revision);
            if (result == TeamVoteFailure.None && !PublishReadiness()) return TeamVoteFailure.Busy;
            return result;
        }
        if (Guest?.Aligned != true || Guest.Closed) return TeamVoteFailure.Busy;
        return Send(connection, action.EncodeVote(Guest.Barrier, revision: revision, cancel: true))
            ? TeamVoteFailure.None : TeamVoteFailure.Busy;
    }

    internal void WithdrawDeparture()
    {
        if (failed || canTransition == null) return;
        if (Host != null) RemoveDepartureVote(SyncHost.HostPeer);
        else if (Guest?.Aligned == true && !Guest.Closed)
            Send(connection, default(TeamTransition).EncodeVote(Guest.Barrier));
    }

    internal bool TryBeginTransition(out TeamTransition action, out byte actor)
    {
        action = default;
        actor = 0;
        if (Host == null || Host.Closed || ReadyMask != 3 || failed) return false;
        if (!ValidProposal())
        {
            ClearProposal();
            voteFailure = TeamVoteFailure.Unavailable;
            PublishReadiness(voteFailure);
            return false;
        }
        if (Host.HasReservations) return false;
        action = Proposal;
        actor = initiator;
        Host.Close();
        return true;
    }

    // Native work may finish after a disconnect. In that case the new link's
    // initial checkpoint will publish the settled world when Capture is ready.
    internal bool CompleteTransition(byte[] checkpoint)
    {
        if (failed) return false;
        if (Host?.Closed == true && !Host.Complete(checkpoint, NetStream.MaxMessage)) return false;
        ClearProposal();
        return true;
    }

    private TeamVoteFailure RecordVote(TeamTransition action, byte actor, int backpack, ulong revision)
    {
        if (!Host.IsAttached(NetSession.GuestPeer) || Host.Closed) return TeamVoteFailure.Busy;
        if (ReadyMask != 0 && !ValidProposal()) ClearProposal();
        bool fromHost = actor == SyncHost.HostPeer;
        if (revision != 0 && (revision != ProposalRevision || !Proposal.Equals(action))) return TeamVoteFailure.Stale;
        if (ReadyMask != 0 && !Proposal.Equals(action)) return TeamVoteFailure.OtherProposal;
        if (ReadyMask == 0 && !canTransition(action, actor)) return TeamVoteFailure.Unavailable;
        if (backpack != 0 && backpack == (fromHost ? GuestBackpack : HostBackpack)) return TeamVoteFailure.BackpackTaken;
        if (!ValidBackpack(action, actor, backpack)) return TeamVoteFailure.BackpackUnavailable;
        if (ReadyMask == 0)
        {
            if (proposalSerial == ulong.MaxValue) return TeamVoteFailure.Busy;
            Proposal = action;
            ProposalRevision = ++proposalSerial;
            initiator = actor;
        }
        if (fromHost) HostBackpack = backpack;
        else GuestBackpack = backpack;
        ReadyMask |= (byte)(fromHost ? 1 : 2);
        return TeamVoteFailure.None;
    }

    private TeamVoteFailure CancelOnHost(TeamTransition action, ulong revision)
    {
        if (!Host.IsAttached(NetSession.GuestPeer) || Host.Closed) return TeamVoteFailure.Busy;
        if (ProposalRevision != revision || !Proposal.Equals(action)) return TeamVoteFailure.Stale;
        ClearProposal();
        return TeamVoteFailure.None;
    }

    private void RemoveDepartureVote(byte actor)
    {
        if (!Host.IsAttached(NetSession.GuestPeer) || Host.Closed || Proposal.Kind != TeamTransitionKind.Depart) return;
        bool fromHost = actor == SyncHost.HostPeer;
        byte bit = fromHost ? (byte)1 : (byte)2;
        if ((ReadyMask & bit) == 0) return;
        ReadyMask = (byte)(ReadyMask & ~bit);
        if (ReadyMask == 0) ClearProposal();
        else
        {
            if (fromHost) HostBackpack = 0;
            else GuestBackpack = 0;
            if (initiator == actor) initiator = fromHost ? NetSession.GuestPeer : SyncHost.HostPeer;
        }
        PublishReadiness();
    }

    private bool ValidBackpack(TeamTransition action, byte actor, int backpack) =>
        action.Kind != TeamTransitionKind.Depart ||
        (canCarry == null ? backpack == 0 : canCarry(actor, backpack));

    private bool ValidProposal() => canTransition(Proposal, initiator) &&
        ((ReadyMask & 1) == 0 || ValidBackpack(Proposal, SyncHost.HostPeer, HostBackpack)) &&
        ((ReadyMask & 2) == 0 || ValidBackpack(Proposal, NetSession.GuestPeer, GuestBackpack));

    private bool PublishReadiness(TeamVoteFailure result = TeamVoteFailure.None) =>
        Send(connection, Proposal.EncodeReadiness(Host.Barrier, ReadyMask, ProposalRevision, HostBackpack, GuestBackpack, result));

    private void ClearProposal()
    {
        Proposal = default;
        ProposalRevision = 0;
        ReadyMask = 0;
        HostBackpack = GuestBackpack = 0;
        initiator = 0;
    }

    // Call after polling, before flushing. Never tear down a transport from
    // inside its own message delivery callback.
    internal void Tick()
    {
        if (failed)
        {
            Reset();
            room.Leave("incompatible");
            return;
        }
        if (!needsCheckpoint || Host == null || !room.IsReady) return;
        byte[] save = capture();
        if (save == null) return; // The host has not entered a supported world yet.
        needsCheckpoint = false;
        if (!Host.Attach(NetSession.GuestPeer, save, NetStream.MaxMessage)) failed = true;
        else if (ReadyMask != 0) PublishReadiness();
    }

    internal bool TryTakeCheckpoint(out byte[] save, out ulong ticket)
    {
        save = failed ? null : incoming;
        ticket = 0;
        if (save == null) return false;
        incoming = null;
        loading = ticket = nextLoad;
        return true;
    }

    // Late completion from a previous load must not enable input in a newer
    // world or after the room closed. On a failed load, end the session.
    internal bool FinishLoading(ulong ticket, bool success)
    {
        if (failed || ticket == 0 || ticket != loading || Guest == null) return false;
        loading = 0;
        failed = !success || !Guest.Loaded();
        return !failed;
    }

    private void OnReady()
    {
        Reset();
        ulong current = connection;
        if (room.IsHost)
        {
            Host = new SyncHostChannel((_, message) => Send(current, message), world);
            Host.Resync = _ => { if (current == connection) needsCheckpoint = true; };
            needsCheckpoint = true;
        }
        else
        {
            Guest = new SyncGuestChannel(room.Peer, message => Send(current, message), world);
            Guest.Load += save =>
            {
                if (current != connection) return;
                nextLoad++;
                loading = 0;
                incoming = save;
                ClearProposal();
            };
        }
    }

    private bool Send(ulong current, byte[] message)
    {
        if (failed || current != connection) return false;
        failed = !room.Send(WorldWire.Channel, message);
        return !failed;
    }

    private void OnMessage(byte channel, byte[] message)
    {
        if (channel != WorldWire.Channel || failed) return;
        if (message.Length != 0 && message[0] == WorldWire.AuthorityRequest)
        {
            failed = !ReceiveAuthorityRequest(message);
            return;
        }
        if (message.Length != 0 && message[0] is WorldWire.TeamVote or WorldWire.TeamCancel or WorldWire.TeamReadiness)
        {
            failed = !ReceiveTeamMessage(message);
            return;
        }
        bool accepted = Host != null
            ? Host.Receive(NetSession.GuestPeer, message)
            : Guest != null && Guest.Receive(message);
        if (!accepted) failed = true;
    }

    private bool ReceiveTeamMessage(byte[] message)
    {
        if (canTransition == null) return false;
        if (Host != null)
        {
            if (!TeamTransition.TryReadVote(message, out uint epoch, out var action, out int backpack, out ulong revision))
                return false;
            if (epoch > Host.Barrier) return false;
            if (epoch != Host.Barrier || Host.Closed) return true;
            if (action.Kind == TeamTransitionKind.None)
            {
                RemoveDepartureVote(NetSession.GuestPeer);
                return !failed;
            }
            var result = message[0] == WorldWire.TeamCancel ? CancelOnHost(action, revision) :
                RecordVote(action, NetSession.GuestPeer, backpack, revision);
            return PublishReadiness(result);
        }
        if (Guest == null || !TeamTransition.TryReadReadiness(message, out uint barrier, out var proposal, out byte mask,
            out ulong proposalRevision, out int hostBackpack, out int guestBackpack, out var failure) || barrier != Guest.Barrier)
            return false;
        Proposal = proposal;
        ProposalRevision = proposalRevision;
        ReadyMask = mask;
        HostBackpack = hostBackpack;
        GuestBackpack = guestBackpack;
        if (failure != TeamVoteFailure.None) voteFailure = failure;
        return true;
    }

    private bool ReceiveAuthorityRequest(byte[] message)
    {
        if (canTransition == null || Host == null || !Host.IsAttached(NetSession.GuestPeer) ||
            !AuthorityRequest.TryRead(message, out var request) || request.Epoch > Host.Barrier) return false;
        if (request.Serial <= requestSerial) return true;
        if (request.Serial != requestSerial + 1) return false;
        requestSerial = request.Serial;
        if (request.Epoch != Host.Barrier || Host.Closed) return true;
        if (authorityRequests.Count >= 32) return false;
        authorityRequests.Enqueue(request);
        return true;
    }

    private void OnEnded(NetClose reason) => Reset();

    private void Reset()
    {
        connection++;
        Host = null;
        Guest = null;
        needsCheckpoint = false;
        failed = false;
        incoming = null;
        loading = 0;
        ClearProposal();
        voteFailure = TeamVoteFailure.None;
        proposalSerial = 0;
        authorityRequests.Clear();
        requestSerial = 0;
    }

    public void Dispose()
    {
        room.Ready -= OnReady;
        room.Ended -= OnEnded;
        room.Message -= OnMessage;
        Reset();
    }
}
