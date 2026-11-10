using System;
using System.Buffers.Binary;

namespace ProbablyCoop.Core;

internal sealed class ShopLink
{
    private readonly bool host;
    private readonly Func<Kind, ulong, ulong, ushort, byte[], bool> send;
    private IShopAuthority authority;
    private byte[] lastCapture;
    private ulong lastCommand;
    private ShopResult lastCommandResult;
    private ulong pendingCommand;
    private byte[] pendingEntity;
    private ShopAction pendingAction;
    private ulong pendingRevision;
    private double nextRefresh;
    private double nextRetry;
    private byte ready;
    private bool acknowledged;
    private double nextPublish;

    internal ShopView View { get; private set; } = new ShopView();
    internal ulong Revision { get; private set; }
    internal ShopResult Result { get; private set; }
    internal bool Waiting => pendingCommand != 0;

    internal ShopLink(bool host, Func<Kind, ulong, ulong, ushort, byte[], bool> send)
    {
        this.host = host;
        this.send = send;
    }

    internal void SetAuthority(IShopAuthority value)
    {
        if (!host || ReferenceEquals(authority, value)) return;
        authority = value;
        nextRefresh = 0;
    }

    internal void Reset()
    {
        Revision = lastCommand = pendingCommand = 0;
        pendingEntity = lastCapture = null;
        Result = ShopResult.None;
        lastCommandResult = ShopResult.None;
        ready = 0;
        acknowledged = false;
        nextPublish = 0;
        nextRefresh = 0;
        View = new ShopView();
    }

    internal void Tick(double now, bool connected)
    {
        if (host)
        {
            if(now>=nextRefresh)
            {
                nextRefresh=now+0.20;
                Refresh();
            }
            if (!acknowledged && connected && now >= nextPublish)
            {
                nextPublish=now+(SendView() ? 0.6 : 0.2);
            }
        }
        if (!host && connected && Waiting && now >= nextRetry) SendPending(now);
    }

    internal void Submit(ShopAction action, uint entity, double now, ShopPlacement placement = default)
    {
        if (!View.Active || Waiting) return;
        if (host)
        {
            if (Refresh())
            {
                Result = ShopResult.Stale;
                SendView();
                return;
            }
            Result = Apply(action, entity, true, placement);
            SendView();
        }
        else if (action != ShopAction.FinishDay)
        {
            pendingAction = action;
            pendingEntity = new byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(pendingEntity, entity);
            pendingEntity[4] = placement.Container;
            pendingEntity[5] = placement.X;
            pendingEntity[6] = placement.Y;
            pendingEntity[7] = placement.Transform;
            pendingCommand = lastCommand + 1;
            pendingRevision = Revision;
            SendPending(now);
        }
    }

    internal bool Receive(Packet packet)
    {
        if (host && packet.Kind == Kind.ShopAck)
        {
            if (packet.Revision != Revision || packet.Serial != lastCommand) return false;
            acknowledged = true;
            return true;
        }
        if (host && packet.Kind == Kind.ShopCommand)
        {
            if (packet.Serial > lastCommand + 1) return false;
            if (packet.Serial == lastCommand + 1)
            {
                lastCommand = packet.Serial;
                Refresh();
                var action = (ShopAction)packet.Value;
                var payload = packet.Payload;
                uint item = BinaryPrimitives.ReadUInt32LittleEndian(payload);
                // A valid hold identifies the same item even if unrelated inventory changed.
                bool holding = View.HeldItem != 0 && View.HeldItem == item &&
                    action is ShopAction.Place or ShopAction.Release;
                Result = packet.Revision == Revision || holding
                    ? Apply(action, item, false, new ShopPlacement(payload[4], payload[5], payload[6], payload[7]))
                    : ShopResult.Stale;
                lastCommandResult = Result;
            }
            SendView();
            return true;
        }
        if (host || packet.Kind != Kind.ShopState || packet.Revision < Revision || packet.Serial < lastCommand ||
            packet.Serial > (Waiting ? pendingCommand : lastCommand) ||
            !ShopProtocol.TryDecode(packet.Payload, out var view)) return false;
        Revision = packet.Revision;
        View = view;
        Result = (ShopResult)packet.Value;
        if (Waiting && packet.Serial == pendingCommand)
        {
            lastCommand = pendingCommand;
            pendingCommand = 0;
        }
        send(Kind.ShopAck, packet.Serial, Revision, 0, null);
        return true;
    }

    private ShopResult Apply(ShopAction action, uint entity, bool local, ShopPlacement placement)
    {
        if (authority == null || !View.Active) return ShopResult.Unavailable;
        if (View.Phase is ShopDay.Settling or ShopDay.SaveUnverified or ShopDay.Ended) return ShopResult.Busy;
        if (action == ShopAction.Ready)
        {
            if (View.Phase != ShopDay.Ready || entity != 0) return ShopResult.Unavailable;
            ready ^= (byte)(local ? 1 : 2);
            View.ReadyMask = ready;
            Revision++;
            return ShopResult.Applied;
        }
        if (action == ShopAction.FinishDay && (!local || ready != 3 || View.Phase != ShopDay.Ready || entity != 0))
            return ShopResult.Unavailable;

        if (local && action >= ShopAction.PickUp)
            return ShopResult.Unavailable;
        var result = authority.Apply(action, entity, placement);
        if (result == ShopResult.Applied)
        {
            ready = 0;
            View.ReadyMask = 0;
            Revision++;
        }
        Refresh();
        return result;
    }

    private bool Refresh()
    {
        var view = authority?.Capture() ?? new ShopView();
        return AcceptView(view,ShopProtocol.Encode(view));
    }
    // The complete native publication already froze these exact metadata.
    // Input validation still calls Refresh() directly and sees live business state.
    internal void ObserveAuthority(IShopAuthority source,ShopView view,byte[] bytes,double now)
    {
        if(!host || authority==null || !ReferenceEquals(source,authority)) return;
        AcceptView(view,bytes);
        nextRefresh=now+0.30;
    }
    private bool AcceptView(ShopView view,byte[] bytes)
    {
        if (lastCapture != null && lastCapture.AsSpan().SequenceEqual(bytes)) return false;
        lastCapture = bytes;
        acknowledged = false;
        nextPublish = 0;
        ready = 0;
        View = view;
        Revision++;
        return true;
    }

    private void SendPending(double now)
    {
        nextRetry = now + 1;
        send(Kind.ShopCommand, pendingCommand, pendingRevision, (ushort)pendingAction, pendingEntity);
    }

    private bool SendView()
    {
        acknowledged = false;
        return send(Kind.ShopState, lastCommand, Revision, (ushort)lastCommandResult, ShopProtocol.Encode(View));
    }
}
