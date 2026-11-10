using ProbablyCoop.Core;

internal static class PendingActionsTransportTests
{
    internal static void Run()
    {
        var replies = new List<Packet>();
        var outgoing = new List<Packet>();
        var ledger = new PendingActions();
        var positions = new Dictionary<byte, byte> { [1] = 0, [2] = 0 };
        var executed = new List<byte>();
        var host = new ReplicaLink(true, (k,s,r,v,p) => { replies.Add(new Packet(k,serial:s,revision:r,value:v,payload:p)); return true; });
        var guest = new ReplicaLink(false, (k,s,r,v,p) => { outgoing.Add(new Packet(k,serial:s,revision:r,value:v,payload:p)); return true; });
        host.Execute = bytes =>
        {
            byte source = bytes[0], expected = bytes[1], destination = bytes[2];
            if (positions[source] != expected) return (ushort)ShopResult.Stale;
            positions[source] = destination;
            executed.Add(source);
            return (ushort)ShopResult.Applied;
        };
        host.Publish(new byte[] { 1 }); host.Tick(0, true);
        guest.Receive(replies.Single()); host.Receive(outgoing.Single());
        outgoing.Clear(); replies.Clear();

        var first = ledger.Reserve(1, new uint[] { 1 });
        var second = ledger.Reserve(2, new uint[] { 1 });
        var third = ledger.Reserve(3, new uint[] { 1 });
        var other = ledger.Reserve(4, new uint[] { 2 });
        Dispatch(first, new byte[] { 1, 0, 1 });
        Dispatch(other, new byte[] { 2, 0, 1 });
        var original = outgoing.ToArray();
        host.Receive(original[1]); host.Receive(original[0]);
        if (positions[1] != 1 || positions[2] != 1 || executed.Count != 2) throw new Exception("independent host work failed");
        // Lose every direct ACK, recover individual outcomes from the native
        // checkpoint receipt list only after that graph is applied locally.
        ApplyCheckpoint();
        Take(first); Take(other);
        if (!second.CanSubmit || third.CanSubmit) throw new Exception("lost-ACK recovery released the wrong dependency");
        Dispatch(second, new byte[] { 1, 1, 0 });
        host.Receive(outgoing.Last());
        guest.Receive(replies.Last());
        Take(second);
        if (third.CanSubmit) throw new Exception("ACK released a source proof before its matching graph");
        ApplyCheckpoint();
        Dispatch(third, new byte[] { 1, 0, 1 });
        host.Receive(outgoing.Last());
        // A very late duplicate from the first gesture must not undo newer work.
        host.Receive(original[0]);
        ApplyCheckpoint();
        Take(third);
        if (ledger.Count != 0 || guest.HasUnacknowledgedRequests || positions[1] != 1 || executed.Count != 4)
            throw new Exception("repeated-pickup chain duplicated or stranded host work");

        var changed = ledger.Reserve(5, new uint[] { 1 });
        var dependent = ledger.Reserve(6, new uint[] { 1 });
        positions[1] = 9; // A real host mutation while local input awaits authority.
        Dispatch(changed, new byte[] { 1, 1, 0 });
        host.Receive(outgoing.Last());
        ApplyCheckpoint(); Take(changed);
        if (dependent.State != PendingActions.Status.Rejected || positions[1] != 9 || ledger.Count != 0)
            throw new Exception("host conflict silently rebased a local dependency");
        Console.WriteLine("PASS pending actions with ReplicaLink: proof-bound re-picks, unrelated work, lost ACK checkpoint recovery, late duplicates and host conflict");
        return;

        void Dispatch(PendingActions.Entry entry, byte[] bytes)
        {
            if (!entry.CanSubmit || !guest.SubmitIndependent(bytes, 0, out ulong serial))
                throw new Exception("valid dependent input did not dispatch");
            ledger.Sent(entry, serial);
        }
        void ApplyCheckpoint()
        {
            guest.ProjectionApplied(host.CompletedRequest, host.CompletedResult);
            foreach (var receipt in host.CaptureCommandReceipts()) guest.ConfirmSnapshotReceipt(receipt.Serial, receipt.Result);
            ledger.Projected(host.CompletedRequest);
        }
        void Take(PendingActions.Entry entry)
        {
            if (!guest.TryTakeIndependentResult(entry.Request, out ushort result)) throw new Exception("receipt lost");
            ledger.Result(entry, result == (ushort)ShopResult.Applied);
        }
    }
}
