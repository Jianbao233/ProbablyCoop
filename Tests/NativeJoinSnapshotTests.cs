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
        var providers = RoomModManifest.Write(new[] { new RoomModManifest.Entry("brewer", "Brewer", "1", 83) });
        var menu = new JObject { ["openingDraftV1"] = new JObject(), [RoomModManifest.Field] = providers };
        var menuReview = NativeJoinSnapshot.ReadForReview(new NativeSnapshotFrame(new[] { NativeSnapshotCodec.Pack(menu) }), null, 0);
        Require(menuReview.Run == Guid.Empty && JToken.DeepEquals(menuReview.Mods, providers) && menuReview.Mods.Parent?.Parent == null);
        state["actors"]["run"] = run.ToString("D");
        state[RoomModManifest.OpeningField] = providers;
        var review = NativeJoinSnapshot.ReadForReview(new NativeSnapshotFrame(new[] { NativeSnapshotCodec.Pack(state) }), null, 0);
        Require(review.Run == run && JToken.DeepEquals(review.OpeningMods, providers) && review.OpeningMods.Parent?.Parent == null);
        Console.WriteLine("PASS join metadata: full/delta menu frames, matching basis, strict run and setup types, opening Mod requirements (9 checks)");
    }
    private static void Require(bool condition) { if(!condition) throw new Exception("Join metadata mismatch"); }
    private static void Reject(Action action)
    { try { action(); } catch(InvalidDataException) { return; } throw new Exception("Invalid join frame accepted"); }
}
