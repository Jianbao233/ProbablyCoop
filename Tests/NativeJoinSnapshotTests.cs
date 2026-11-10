using Newtonsoft.Json.Linq;
using ProbablyCoop;
using ProbablyCoop.Core;

internal static class NativeJoinSnapshotTests
{
    internal static void Run()
    {
        var run=Guid.NewGuid();
        var state=new JObject { ["actors"]=new JObject { ["run"]=run.ToString("D") }, ["partnerSetupRequired"]=true,
            ["roots"]=new JArray(new JObject { ["items"]=new JArray(new JObject { ["data"]=123 }) }) };
        var full=NativeSnapshotBinary.Pack(state);
        Require(NativeJoinSnapshot.Read(full,full,7)==(run,true));
        state["partnerSetupRequired"]=false;
        var delta=NativeRecordFrame.Delta(NativeSnapshotBinary.Pack(state),7);
        Require(NativeJoinSnapshot.Read(delta,full,7)==(run,false));
        Reject(()=>NativeJoinSnapshot.Read(delta,full,8));
        Reject(()=>NativeJoinSnapshot.Read(delta,null,7));
        Reject(()=>NativeJoinSnapshot.Read(delta,delta,7));
        state["partnerSetupRequired"]="false";
        Reject(()=>NativeJoinSnapshot.Read(NativeSnapshotBinary.Pack(state),null,0));
        state["partnerSetupRequired"]=false; state["actors"]["run"]=Guid.Empty.ToString("D");
        Reject(()=>NativeJoinSnapshot.Read(NativeSnapshotBinary.Pack(state),null,0));
        Console.WriteLine("PASS join metadata: full/delta menu frames, matching basis, strict run and setup types (7 checks)");
    }
    private static void Require(bool condition) { if(!condition) throw new Exception("Join metadata mismatch"); }
    private static void Reject(Action action)
    { try { action(); } catch(InvalidDataException) { return; } throw new Exception("Invalid join frame accepted"); }
}
