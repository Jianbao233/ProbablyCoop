using ProbablyCoop.Core;

internal static class ActivityRequestTests
{
    internal static void Run()
    {
        Require(DisposableProduct.BlocksAccountWrites("Probably Stolen Coop QA Host") &&
            DisposableProduct.BlocksAccountWrites("Probably Stolen Coop Test Guest") &&
            DisposableProduct.BlocksAccountWrites("Probably Stolen Coop RegressionNative Host"), "all disposable players isolate account writes");
        Require(!DisposableProduct.BlocksAccountWrites(null) && !DisposableProduct.BlocksAccountWrites("Probably Stolen") &&
            !DisposableProduct.BlocksAccountWrites("Other Game") && !DisposableProduct.BlocksAccountWrites("Probably Stolen Coop"), "normal products retain original account behavior");
        var requests=new ActivityRequests();
        Require(requests.Track(4,"phone","phone",10) && requests.Track(7,"control","machine/25",11),"independent activities coexist across hand request ids");
        Require(!requests.Track(4,"control","machine/26",12),"no duplicate identity");
        Require(requests.Reply(7,(ushort)ShopResult.Applied,out var machine) && machine.Kind=="control" && machine.Started==11,"out of order reply associated with its own activity");
        Require(requests.Pending("phone") && requests.PendingActivity("machine/25"),"ACK alone does not mean graph applied");
        requests.Project(6); requests.Collect();
        Require(requests.Count==2,"older snapshot cannot retire newer reply");
        requests.Project(7); requests.Collect();
        Require(requests.Count==1 && !requests.Pending("control") && requests.Pending("phone"),"one activity completes without releasing another");
        Require(requests.Reply(4,(ushort)ShopResult.Busy,out var phone) && phone.Kind=="phone" &&
            !requests.Reply(4,(ushort)ShopResult.Applied,out _),"late rejection belongs to phone and duplicate cannot change it");
        requests.Collect(); Require(requests.Count==0,"projection before reply is also safe");
        Require(requests.Track(9,"phone","phone",15),"new view request"); requests.Retire();
        Require(!requests.Pending("phone") && requests.Count==1,"retired view no longer blocks new view but retains result drainage");
        Require(requests.Track(10,"phone","phone",16) && requests.Reply(9,(ushort)ShopResult.Stale,out var retired) && !retired.Active,"old reply stays retired");
        requests.Project(9); requests.Collect(); Require(requests.Pending("phone") && requests.Count==1,"old view reply cannot finish replacement view");
        requests.Clear();
        for(ulong serial=1;serial<=ActivityRequests.Limit;serial++) Require(requests.Track(serial,"control","machine/"+serial,serial),"bounded window fills");
        Require(!requests.HasRoom && !requests.Track(100,"control","machine/100",1),"backpressure is explicit");
        requests.Clear(); Require(requests.HasRoom && requests.Count==0,"lifetime reset");
        Console.WriteLine("PASS native activity requests: concurrent scopes, per-id out-of-order results, receipt/projection ordering, retired views and bounded backpressure");
    }
    private static void Require(bool value,string message) { if(!value) throw new Exception(message); }
}
