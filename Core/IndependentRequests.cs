using System;
using System.Collections.Generic;
using System.Linq;

namespace ProbablyCoop.Core;

internal sealed partial class ReplicaLink
{
    internal const int RequestWindow = 32;
    private sealed class InFlightRequest
    {
        internal byte[] Bytes;
        internal ulong Revision;
        internal double RetryAt;
    }

    private readonly Dictionary<ulong, InFlightRequest> independentRequests = new Dictionary<ulong, InFlightRequest>();
    private readonly Dictionary<ulong, ushort> independentResults = new Dictionary<ulong, ushort>();
    private readonly SortedDictionary<ulong, byte[]> orderedCommands = new SortedDictionary<ulong, byte[]>();
    private readonly Dictionary<ulong, ushort> commandResults = new Dictionary<ulong, ushort>();
    private ulong nextIssuedRequest;
    internal int IndependentInFlight => independentRequests.Count;
    internal bool HasUnacknowledgedRequests => Waiting || IndependentInFlight > 0;
    internal ulong LastIndependentCompletion { get; private set; }
    internal ushort LastIndependentResult { get; private set; }

    internal bool SubmitIndependent(byte[] command, double now, out ulong serial)
    {
        serial = 0;
        if (host || Revision == 0 || command == null || command.Length == 0 ||
            command.Length > MaxCommand || !CanIssueRequest())
            return false;
        serial = ++nextIssuedRequest;
        var pendingRequest = new InFlightRequest { Bytes = (byte[])command.Clone(), Revision = Revision };
        independentRequests.Add(serial, pendingRequest);
        SendIndependent(serial, pendingRequest, now);
        return true;
    }

    private bool CanIssueRequest()
    {
        if (nextIssuedRequest == ulong.MaxValue ||
            independentRequests.Count + independentResults.Count + (Waiting ? 1 : 0) >= RequestWindow) return false;
        // Both command APIs share the same sequence space and cache horizon.
        // A lost independent receipt cannot be evicted by fast legacy requests.
        ulong oldest = pending;
        foreach (ulong serial in independentRequests.Keys)
            if (oldest == 0 || serial < oldest) oldest = serial;
        return oldest == 0 || nextIssuedRequest - oldest < RequestWindow - 1;
    }

    internal bool TryTakeIndependentResult(ulong serial, out ushort result)
    {
        if (!independentResults.TryGetValue(serial, out result)) return false;
        independentResults.Remove(serial);
        return true;
    }

    private void SendIndependent(ulong serial, InFlightRequest pendingRequest, double now)
    {
        bool sent = send(Kind.NativeCommand, serial, pendingRequest.Revision, 0, pendingRequest.Bytes);
        pendingRequest.RetryAt = now + (sent ? 0.5 : 0.05);
    }

    private void RetryIndependentRequests(double now)
    {
        foreach (var row in independentRequests.ToArray())
            if (now >= row.Value.RetryAt) SendIndependent(row.Key, row.Value, now);
    }

    private bool ReceiveIndependentReply(Packet packet)
        => CompleteIndependentRequest(packet.Serial, packet.Value);

    internal (ulong Serial, ushort Result)[] CaptureCommandReceipts() => commandResults
        .OrderBy(row=>row.Key).TakeLast(RequestWindow).Select(row=>(row.Key,row.Value)).ToArray();

    internal bool ConfirmSnapshotReceipt(ulong serial, ushort result)
    {
        if(host || serial==0 || result is 0 or > (ushort)ShopResult.Failed)
            throw new ArgumentException("Invalid native snapshot request receipt");
        return CompleteIndependentRequest(serial,result);
    }

    private bool CompleteIndependentRequest(ulong serial, ushort result)
    {
        if (!independentRequests.Remove(serial)) return false;
        independentResults.Add(serial, result);
        if (serial >= LastIndependentCompletion)
        {
            LastIndependentCompletion = serial;
            LastIndependentResult = result;
        }
        if (result == (ushort)ShopResult.Applied)
        {
            wantProjection = Math.Max(wantProjection, serial);
            progressed = true;
        }
        return true;
    }

    private bool ReceiveOrderedCommand(Packet packet)
    {
        if (packet.Serial == 0 || packet.Payload == null || packet.Payload.Length is 0 or >MaxCommand)
            return false;
        if (commandResults.TryGetValue(packet.Serial, out ushort prior))
        {
            send(Kind.NativeReply, packet.Serial, Revision, prior, null);
            return true;
        }
        if (packet.Serial <= lastRequest || packet.Serial - lastRequest > RequestWindow) return false;
        if (!orderedCommands.ContainsKey(packet.Serial))
            orderedCommands.Add(packet.Serial, (byte[])packet.Payload.Clone());
        while (orderedCommands.TryGetValue(lastRequest + 1, out var command))
        {
            // Advance and cache a failure before invoking business code, including exceptions.
            lastRequest++;
            orderedCommands.Remove(lastRequest);
            lastResult = (ushort)ShopResult.Failed;
            commandResults[lastRequest] = lastResult;
            try { lastResult = Execute?.Invoke(command) ?? (ushort)ShopResult.Unavailable; }
            finally
            {
                commandResults[lastRequest] = lastResult;
                send(Kind.NativeReply, lastRequest, Revision, lastResult, null);
                if (lastRequest > RequestWindow * 2) commandResults.Remove(lastRequest - RequestWindow * 2);
            }
        }
        return true;
    }

    private void ClearIndependentRequests()
    {
        nextIssuedRequest = 0;
        LastIndependentCompletion = 0;
        LastIndependentResult = 0;
        independentRequests.Clear();
        independentResults.Clear();
        orderedCommands.Clear();
        commandResults.Clear();
    }
}
