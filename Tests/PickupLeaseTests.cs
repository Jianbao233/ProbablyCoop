using System.Buffers.Binary;
using ProbablyCoop.Core;

internal static class PickupLeaseTests
{
    internal static void Run()
    {
        var lease=new PickupLease();
        Check(lease.Acquire(0,new uint[]{1},0,_=>true)==ShopResult.Stale,"zero hand accepted");
        Check(lease.Acquire(1,new uint[]{1,1},0,_=>true)==ShopResult.Unavailable && lease.ActiveSerial==0,"duplicate entity reserved");
        Check(lease.Acquire(2,new uint[]{1,2},0,id=>id!=2)==ShopResult.Unavailable && lease.Items.Count==0,"partial group reservation");
        uint[] items={3,4};
        Check(lease.Acquire(3,items,1,_=>true)==ShopResult.Applied,"acquire failed");
        items[0]=99;
        Check(lease.Items.SequenceEqual(new uint[]{3,4}),"caller changed active hand through array alias");
        Check(lease.Acquire(4,new uint[]{7},2,_=>true)==ShopResult.Busy && lease.ActiveSerial==3,"new request stole occupied hand");
        Check(lease.Release(2,2)==ShopResult.Stale && lease.ActiveSerial==3,"old release cleared current hand");
        Check(lease.Renew(3,10,_=>true)==ShopResult.Applied && !lease.Tick(24.999),"renewed hand expired early");
        Check(lease.Tick(25) && lease.ActiveSerial==0,"expiry boundary missed");
        Check(lease.Acquire(3,new uint[]{3,4},25,_=>true)==ShopResult.Stale,"expired acquire resurrected hand");
        Check(lease.Acquire(4,new uint[]{7},25,_=>true)==ShopResult.Stale,"previously rejected busy acquire revived later");
        Check(lease.Acquire(5,new uint[]{7},26,_=>true)==ShopResult.Applied,"next hand unavailable");
        Check(lease.Renew(5,27,_=>false)==ShopResult.Stale && lease.ActiveSerial==0,"changed source kept reservation");
        Check(lease.Acquire(6,new uint[]{8,9},28,_=>true)==ShopResult.Applied,"group acquire failed");
        int executions=0;
        Check(lease.Commit(6,new uint[]{8},28,()=>{executions++;return ShopResult.Applied;})==ShopResult.Stale &&
            lease.ActiveSerial==6 && executions==0,"subset/mismatched drop committed");
        Check(lease.Commit(6,new uint[]{9,8},29,()=>{executions++;return ShopResult.NoSpace;})==ShopResult.NoSpace &&
            lease.ActiveSerial==0 && executions==1,"failed complete drop retained hand");
        Check(lease.Commit(6,new uint[]{8,9},29,()=>{executions++;return ShopResult.Applied;})==ShopResult.Stale && executions==1,
            "completed hand committed twice");
        Check(lease.Acquire(7,new uint[]{8},30,_=>true)==ShopResult.Applied,"exception fixture acquire");
        try { lease.Commit(7,new uint[]{8},30,()=>throw new ArithmeticException("native mutation failed")); }
        catch(ArithmeticException) { }
        Check(lease.ActiveSerial==0,"exception leaked hand");
        lease.Reset();
        Check(lease.Acquire(1,new uint[]{8},31,_=>true)==ShopResult.Applied,"fresh authenticated epoch cannot start at hand1");
        Check(lease.Release(1,32)==ShopResult.Applied && lease.Release(1,32)==ShopResult.Applied,"idempotent release");
        Check(lease.Acquire(2,Enumerable.Range(1,33).Select(i=>(uint)i).ToArray(),33,_=>true)==ShopResult.Unavailable &&
            lease.Acquire(3,new uint[]{0},33,_=>true)==ShopResult.Unavailable,"hand size/entity bounds");
        Check(lease.Acquire(4,new uint[]{55},34,_=>true)==ShopResult.Applied,"renew stale fixture");
        Check(lease.Renew(3,35,_=>true)==ShopResult.Stale && lease.ActiveSerial==4 &&
            lease.Release(99,35)==ShopResult.Stale && lease.ActiveSerial==4,"wrong serial changed active hand");
        Check(lease.Renew(4,49,_=>true)==ShopResult.Stale && lease.ActiveSerial==0,"expired renewal revived hand");
        CheckTransportRetry();
        Console.WriteLine("PASS pickup lease ledger: atomic groups, immutable IDs, monotonic hands, cancellation, renewal/expiry, source invalidation, exact-set drop, exception release and ReplicaLink duplicate/lost-reply retry");
    }
    private static void CheckTransportRetry()
    {
        var fromHost=new List<Packet>(); var fromGuest=new List<Packet>();
        var host=new ReplicaLink(true,(k,s,r,v,p)=>{fromHost.Add(new Packet(k,serial:s,revision:r,value:v,payload:p));return true;});
        var guest=new ReplicaLink(false,(k,s,r,v,p)=>{fromGuest.Add(new Packet(k,serial:s,revision:r,value:v,payload:p));return true;});
        var lease=new PickupLease(); int acquires=0,commits=0;
        host.Execute=payload=> {
            ulong serial=BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(1));
            if(payload[0]==1) { acquires++; return (ushort)lease.Acquire(serial,new uint[]{42},0,_=>true); }
            return (ushort)lease.Commit(serial,new uint[]{42},1,()=>{commits++;return ShopResult.Applied;});
        };
        host.Publish(new byte[]{1}); host.Tick(0,true);
        guest.Receive(fromHost.Single()); host.Receive(fromGuest.Single()); fromHost.Clear();fromGuest.Clear();
        var acquire=new byte[9]; acquire[0]=1; BinaryPrimitives.WriteUInt64LittleEndian(acquire.AsSpan(1),1);
        Check(guest.Submit(acquire,0),"acquire transport submit"); guest.Tick(0,true);
        host.Receive(fromGuest.Single()); fromGuest.Clear(); fromHost.Clear(); // drop first reply
        guest.Tick(1,true); host.Receive(fromGuest.Single()); host.Receive(fromGuest.Single());
        guest.Receive(fromHost.Last()); fromGuest.Clear();fromHost.Clear();
        Check(acquires==1 && lease.ActiveSerial==1 && guest.Result==(ushort)ShopResult.Applied,"lost grant reply reacquired or lost reservation");
        var drop=(byte[])acquire.Clone(); drop[0]=2;
        Check(guest.Submit(drop,2),"drop transport submit"); guest.Tick(2,true);
        host.Receive(fromGuest.Single()); host.Receive(fromGuest.Single());
        guest.Receive(fromHost.Last()); fromGuest.Clear();fromHost.Clear();
        Check(commits==1 && lease.ActiveSerial==0,"duplicate native drop mutated twice");
        Check(guest.Submit(drop,3),"replay transport submit"); guest.Tick(3,true); host.Receive(fromGuest.Single()); guest.Receive(fromHost.Last());
        Check(commits==1 && guest.Result==(ushort)ShopResult.Stale,"later serial reused finished lease");
    }
    private static void Check(bool value,string message) { if(!value) throw new Exception(message); }
}
