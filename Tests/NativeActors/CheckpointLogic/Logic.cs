using System;
using System.IO;
using System.Text;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using ProbablyCoop;

// Native/ES3 test doubles. This suite tests our lifecycle, codec, graph checks
// and actual bounded file I/O; it does NOT prove Unity ES3 runtime behavior.
static class CheckpointLogicTests
{
    static int count;
    static readonly Guid Run = Guid.NewGuid();
    static JObject Personal() => new JObject { ["schema"] = 1, ["run"] = Run.ToString("D"),
        ["actors"] = new JArray(new JObject { ["name"] = "host" }, new JObject { ["name"] = "partner" }) };
    static JArray Roots() => new JArray(new[] { "actor/0/pocket-left", "actor/0/pocket-right", "actor/0/backpack",
        "actor/1/pocket-left", "actor/1/pocket-right", "actor/1/backpack" }.Select(k => new JObject { ["key"] = k, ["save"] = null }));
    static JObject Node(int uid, long uuid = 0) => new JObject { ["uniqueId"] = uid, ["uuid"] = uuid,
        ["identifier"] = "fixture", ["unitCount"] = 1, ["childItems"] = new JArray(), ["childItemInventoryNode"] = new JArray() };
    static JObject Save(params JObject[] nodes) => new JObject { ["saveItems"] = new JArray(nodes) };
    static NativeActorCheckpoint.Envelope Make(JArray roots = null, JArray selected = null, int[] shared = null, string setup = null)
        => NativeActorCheckpoint.Create(Run, 5, 7, Personal(), roots ?? Roots(), selected ?? new JArray(null, null), shared ?? Array.Empty<int>(), setup);
    static PlayerStore Store() => new PlayerStore { runID = Run.ToString("D"), saveSlotId = 5 };
    static NativeActorCheckpoint.Receipt Receipt(NativeActorCheckpoint.Envelope e) => new NativeActorCheckpoint.Receipt {
        Run = e.Run, Slot = e.Slot, Day = e.Day, CurrentUniqueId = 100, Encoded = e.Encoded, PartnerSetup = e.PartnerSetup, NativeHost = (JObject)e.Personal["actors"][0] };
    static void Check(bool b, string name) { if (!b) throw new Exception(name); count++; Console.WriteLine("PASS " + name); }
    static void Fail(Action a, string name) { try { a(); } catch { Check(true,name); return; } throw new Exception("Expected rejection: " + name); }
    static JObject Slot(NativeActorCheckpoint.Envelope e)
    {
        var body = new JObject { ["runID"] = e.Run.ToString("D"), ["saveSlotId"] = e.Slot,
            ["currentUniqueId"] = 100, ["modData"] = new JObject { [NativeActorCheckpoint.ModDataKey] = e.Encoded },
            ["irrelevant"] = new JArray(new JObject { ["unexecuted"] = true }) };
        body["playerName"]="host"; body["healthData"]=new JObject(); body["startingPerks"]=new JArray(); body["appraisalBlacklist"]=new JArray();
        body["appraisalPromotionDeclined"]=false;
        foreach(string key in "appraiserLevel appraisalProgress appraisalPromotionDay scavengingAttempts dumpingGroundMajorWoundImmunitySearchesLeft dumpingGroundMinorWoundImmunitySearchesLeft".Split(' ')) body[key]=0;
        foreach (string key in "mainInvJSON backInvJSON hiddenInvJSON showcaseInvJSON soldInvJSON hirelingInvJSON vendingMachineInvJSON vendingFountainInvJSON trashcanInvJSON docInvJSON cassettePlayerInvJSON".Split(' ')) body[key] = null;
        if (e.PartnerSetup != null) body["modData"][NativeActorCheckpoint.PartnerSetupDataKey] = e.PartnerSetup;
        return new JObject { ["playerStore"] = new JObject { ["__type"] = "PlayerStore", ["value"] = body },
            ["storeStation"] = new JObject { ["__type"] = "StoreStation", ["value"] = new JObject { ["dayCounter"] = e.Day } } };
    }
    static void WriteSlot(JObject data) => File.WriteAllText(SaveFiles.Slot(5), data.ToString(), Encoding.UTF8);
    internal static void RunAll()
    {
        var e = Make(); var store = Store();
        Check(NativeActorCheckpoint.Decode(e.Encoded, Array.Empty<int>()).Checkpoint == e.Checkpoint, "personal and six empty roots roundtrip");
        Check(Make().Checkpoint != e.Checkpoint, "fresh save nonce");
        var returned = e.Roots; returned.RemoveAt(0);
        Check(e.Roots.Count == 6, "detached envelope output");
        NativeActorCheckpoint.VerifyReceipt(e, Receipt(e)); Check(true,"matching native receipt");
        var old = Receipt(e); old.Encoded = Make().Encoded;
        Fail(() => NativeActorCheckpoint.VerifyReceipt(e, old), "same-day old save cannot acknowledge new attempt");
        var bad = Receipt(e); bad.Run = Guid.NewGuid(); Fail(()=>NativeActorCheckpoint.VerifyReceipt(e,bad),"wrong run");
        bad = Receipt(e); bad.Slot++; Fail(()=>NativeActorCheckpoint.VerifyReceipt(e,bad),"wrong slot");
        bad = Receipt(e); bad.Day++; Fail(()=>NativeActorCheckpoint.VerifyReceipt(e,bad),"wrong day");
        bad = Receipt(e); bad.NativeHost["name"]="changed during save"; Fail(()=>NativeActorCheckpoint.VerifyReceipt(e,bad),"native host changed after capture");
        var p = JObject.Parse(e.Encoded); p["personal"]["run"] = Guid.NewGuid().ToString("D"); Fail(()=>NativeActorCheckpoint.Decode(p.ToString(),Array.Empty<int>()),"wrong personal run");
        p = JObject.Parse(e.Encoded); ((JArray)p["personal"]["actors"]).Add(new JObject()); Fail(()=>NativeActorCheckpoint.Decode(p.ToString(),Array.Empty<int>()),"third actor");
        p = JObject.Parse(e.Encoded); p["surprise"] = 1; Fail(()=>NativeActorCheckpoint.Decode(p.ToString(),Array.Empty<int>()),"extra envelope field");
        Fail(()=>NativeActorCheckpoint.Decode(e.Encoded.TrimEnd('}')+",\"schema\":1}",Array.Empty<int>()),"duplicate JSON property");
        Fail(()=>NativeActorCheckpoint.Decode(e.Encoded+" {}",Array.Empty<int>()),"trailing JSON");
        Fail(()=>NativeActorCheckpoint.Decode(new string('x',NativeActorCheckpoint.MaximumEnvelopeCharacters+1),Array.Empty<int>()),"oversized envelope");
        var roots = Roots(); roots.RemoveAt(0); Fail(()=>Make(roots),"missing carry root");
        roots = Roots(); roots[1]["key"] = roots[0]["key"].DeepClone(); Fail(()=>Make(roots),"duplicate carry root");
        roots = Roots(); roots[0]["key"] = "actor/2/pocket-left"; Fail(()=>Make(roots),"old 1-based actor key rejected");
        roots = Roots(); roots[0]["save"] = Save(Node(1)); var occupied = Make(roots);
        Check(NativeActorCheckpoint.Decode(occupied.Encoded, Array.Empty<int>()).Roots[0]["save"]["saveItems"].Count()==1,"carried native node survives");
        var nullable=Roots();var placeholder=Node(7);placeholder["identifier"]=null;nullable[0]["save"]=Save(placeholder);
        Make(nullable);Check(true,"native nullable placeholder identifier preserved");
        Fail(()=>Make(roots,shared:new[]{1}),"shared/carry duplicate");
        roots[3]["save"] = Save(Node(1)); Fail(()=>Make(roots),"two-actor duplicate");
        roots = Roots(); roots[0]["save"] = Save(Node(0)); Fail(()=>Make(roots),"unassigned carry identity");
        roots = Roots(); roots[0]["save"] = Save(Node(1),Node(2)); Fail(()=>Make(roots),"duplicate per-root UUID");
        var root = Node(1); var child = Node(2,1); ((JArray)root["childItems"]).Add(1); ((JArray)root["childItemInventoryNode"]).Add(0);
        roots = Roots(); roots[0]["save"] = Save(root,child); e = Make(roots);
        Check(e.Roots[0]["save"]["saveItems"].Count()==2,"nested native item tree");
        ((JArray)child["childItems"]).Add(0); ((JArray)child["childItemInventoryNode"]).Add(0);
        roots[0]["save"] = Save(root,child); Fail(()=>Make(roots),"cycle rejected");
        root = Node(1); roots[0]["save"] = Save(root,Node(2,1)); Fail(()=>Make(roots),"orphan node");
        root = Node(1); ((JArray)root["childItems"]).Add(1); roots[0]["save"] = Save(root,Node(2,1)); Fail(()=>Make(roots),"mismatched placement lists");
        var chain = Enumerable.Range(0,33).Select(i=>Node(i+1,i)).ToArray();
        for(int i=0;i<32;i++){((JArray)chain[i]["childItems"]).Add(i+1);((JArray)chain[i]["childItemInventoryNode"]).Add(0);}
        roots=Roots(); roots[0]["save"]=Save(chain); Fail(()=>Make(roots),"carry graph respects ownership depth limit");
        roots = Roots(); roots[0]["save"] = Save(Node(1));
        Make(roots,new JArray(1,null)); Check(true,"owner selects own carried item");
        Fail(()=>Make(roots,new JArray(null,1)),"partner cannot select host-owned item");
        Make(selected:new JArray(9,null),shared:new[]{9}); Check(true,"selection may reference shared stock");
        Fail(()=>Make(selected:new JArray(9,9),shared:new[]{9}),"two actors cannot reserve one backpack");
        Fail(()=>Make(selected:new JArray(9,null)),"dangling selected backpack");
        e = Make(roots); bad=Receipt(e); bad.CurrentUniqueId=0; Fail(()=>NativeActorCheckpoint.VerifyReceipt(e,bad),"ID generator cannot regress");
        bad=Receipt(e); bad.NativeInventories.Add("mainInvJSON",Save(Node(1)).ToString()); Fail(()=>NativeActorCheckpoint.VerifyReceipt(e,bad),"readback rechecks carry/shared ownership");
        e=Make(); store=Store();
        Fail(()=>new NativeActorCheckpoint.Pending(store,7,e,()=>false),"singleplayer or unsettled rejected before staging");
        Check(store.modData.Count==0,"rejected stage leaves metadata untouched");
        Fail(()=>new NativeActorCheckpoint.Pending(store,8,e,()=>true),"stale current day rejected");
        store.ActorName="wrong"; Fail(()=>new NativeActorCheckpoint.Pending(store,7,e,()=>true),"host capture mismatch"); store.ActorName="host";
        using(var pending=new NativeActorCheckpoint.Pending(store,7,e,()=>true))
        {
            Check(store.modData[NativeActorCheckpoint.ModDataKey]==e.Encoded,"stage only requested metadata key");
            Fail(()=>new NativeActorCheckpoint.Pending(store,7,Make(),()=>true),"overlapping attempt rejected");
            WriteSlot(Slot(Make())); Fail(()=>pending.VerifyWritten(),"silent native failure restoring old save rejected by actual file read");
        }
        Check(!store.modData.ContainsKey(NativeActorCheckpoint.ModDataKey),"failed attempt restores missing key");
        Fail(()=>new NativeActorCheckpoint.Pending(store,7,e,()=>true),"failed attempt cannot reuse write ticket");
        var restored=NativeActorCheckpoint.Decode(Make().Encoded,Array.Empty<int>());
        Fail(()=>restored.AuthorizeFreshCapture(new object()),"internal caller cannot authorize decoded ticket");
        Fail(()=>new NativeActorCheckpoint.Pending(store,7,restored,()=>true),"restored envelope is not a fresh write ticket");
        e=Make(); using(var pending=new NativeActorCheckpoint.Pending(store,7,e,()=>true)) { WriteSlot(Slot(e)); }
        Fail(()=>new NativeActorCheckpoint.Pending(store,7,e,()=>true),"disk written without ack cannot be accepted by reusing envelope");
        var prior=Make(); store.modData[NativeActorCheckpoint.ModDataKey]=prior.Encoded;
        e=Make();
        using(var pending=new NativeActorCheckpoint.Pending(store,7,e,()=>true)) { }
        Check(store.modData[NativeActorCheckpoint.ModDataKey]==prior.Encoded,"failed attempt preserves prior checkpoint");
        e=Make();
        using(var pending=new NativeActorCheckpoint.Pending(store,7,e,()=>true))
        { WriteSlot(Slot(e)); pending.VerifyWritten(); Fail(()=>pending.VerifyWritten(),"duplicate acknowledgment rejected"); }
        Check(store.modData[NativeActorCheckpoint.ModDataKey]==e.Encoded,"verified readback retains metadata");
        Fail(()=>new NativeActorCheckpoint.Pending(store,7,e,()=>true),"nonce cannot be restaged");
        var newer=Make(); using(var pending=new NativeActorCheckpoint.Pending(store,7,newer,()=>true))
        { store.modData[NativeActorCheckpoint.ModDataKey]="another owner"; }
        Check(store.modData[NativeActorCheckpoint.ModDataKey]=="another owner","cleanup does not erase newer owner's data");
        var file = Slot(e); ((JObject)file["playerStore"]["value"]).Remove("modData");
        using(var reader=ES3Reader.Create(Encoding.UTF8.GetBytes(file.ToString()),new ES3Settings(false))) Fail(()=>NativeActorCheckpoint.ReadReceipt(reader),"missing native coop entry");
        using(var reader=ES3Reader.Create(Encoding.UTF8.GetBytes(file.ToString()),new ES3Settings(false)))
            Fail(()=>NativeActorCheckpoint.ReadReceipt(reader,run=>run==Run),"matching legacy cooperation marker still requires actor data");
        file["playerStore"]["value"]["runID"]=Guid.NewGuid().ToString("D");
        using(var reader=ES3Reader.Create(Encoding.UTF8.GetBytes(file.ToString()),new ES3Settings(false)))
            Check(NativeActorCheckpoint.ReadReceipt(reader,run=>run==Run)==null,"reused slot with stale cooperation marker can load as single-player");
        file=Slot(e);
        using(var reader=ES3Reader.Create(Encoding.UTF8.GetBytes(file.ToString()),new ES3Settings(false)))
        {
            var registered=NativeActorCheckpoint.ReadReceipt(reader,run=>run==Run);
            Check(NativeActorCheckpoint.DecodeReceipt(registered,Run,5,7,e.Checkpoint).Checkpoint==e.Checkpoint,
                "matching run restores the exact actor checkpoint");
            Fail(()=>NativeActorCheckpoint.DecodeReceipt(registered,Run,5,7,Guid.NewGuid()),
                "another checkpoint for the same run, slot and day does not satisfy the selected nonce");
        }
        file=Slot(e); ((JObject)file["storeStation"]["value"]).Remove("dayCounter");
        using(var reader=ES3Reader.Create(Encoding.UTF8.GetBytes(file.ToString()),new ES3Settings(false))) Fail(()=>NativeActorCheckpoint.ReadReceipt(reader),"missing native day");
        file=Slot(e); file["playerStore"]["__type"]="StoreStation";
        using(var reader=ES3Reader.Create(Encoding.UTF8.GetBytes(file.ToString()),new ES3Settings(false))) Fail(()=>NativeActorCheckpoint.ReadReceipt(reader),"wrong serialized native type");
        Check(PlayerStore.Constructions==2,"receipt extraction creates no PlayerStore");
        e=Make(); bad=Receipt(e);
        foreach(var fixture in new[]{("docInvJSON",-101,"dossier"),("trashcanInvJSON",-102,"trashcan"),("cassettePlayerInvJSON",-103,"cassette_player")})
        { var n=Node(fixture.Item2); n["identifier"]=fixture.Item3; bad.NativeInventories.Add(fixture.Item1,Save(n).ToString()); }
        NativeActorCheckpoint.VerifyReceipt(e,bad); Check(true,"native fixed negative fixture identities preserved");
        bad.NativeInventories["mainInvJSON"]=bad.NativeInventories["docInvJSON"];
        Fail(()=>NativeActorCheckpoint.VerifyReceipt(e,bad),"fixed fixture ID cannot migrate to arbitrary root");
        Check((int)JObject.Parse(e.Encoded)["schema"]==2,"new checkpoint schema binds partner setup");
        p=JObject.Parse(e.Encoded); p["schema"]=1; p.Remove("partnerSetup");
        Fail(()=>NativeActorCheckpoint.Decode(p.ToString(),Array.Empty<int>()),"old schema is explicitly refused without migration");
        // This suite validates opaque binding. Actual setup semantics are
        // exercised separately against NativeActorPartnerSetup by the native probe.
        const string setup="{\"fixture\":\"initial-perk-selection\"}";
        e=Make(setup:setup);
        Check(NativeActorCheckpoint.Decode(e.Encoded,Array.Empty<int>()).PartnerSetup==setup,"setup text roundtrips exactly");
        NativeActorCheckpoint.VerifyReceipt(e,Receipt(e)); Check(true,"matching setup and personal receipt accepted");
        bad=Receipt(e);bad.PartnerSetup=null;
        Fail(()=>NativeActorCheckpoint.VerifyReceipt(e,bad),"missing setup cannot acknowledge actor state");
        bad=Receipt(e);bad.PartnerSetup="{\"fixture\":\"older-choice\"}";
        Fail(()=>NativeActorCheckpoint.DecodeReceipt(bad,Run,5,7),"reload rejects substituted setup even with matching actor nonce");
        bad=Receipt(Make());bad.PartnerSetup=setup;
        Fail(()=>NativeActorCheckpoint.DecodeReceipt(bad,Run,5,7),"setup cannot be added independently to old personal checkpoint");
        p=JObject.Parse(e.Encoded);p["partnerSetup"]=new JObject();
        Fail(()=>NativeActorCheckpoint.Decode(p.ToString(),Array.Empty<int>()),"setup must be exact text or unset null");
        Fail(()=>Make(setup:""),"empty setup marker rejected");
        Fail(()=>Make(setup:new string('x',NativeActorCheckpoint.MaximumPartnerSetupCharacters+1)),"oversized setup marker rejected");
        store.modData.Clear();
        Fail(()=>new NativeActorCheckpoint.Pending(store,7,e,()=>true),"stage rejects setup omitted from native metadata");
        Check(store.modData.Count==0,"setup mismatch does not stage checkpoint or consume ticket");
        store.modData[NativeActorCheckpoint.PartnerSetupDataKey]=setup;
        using(var pending=new NativeActorCheckpoint.Pending(store,7,e,()=>true))
        {
            file=Slot(e);((JObject)file["playerStore"]["value"]["modData"]).Remove(NativeActorCheckpoint.PartnerSetupDataKey);WriteSlot(file);
            Fail(()=>pending.VerifyWritten(),"actual readback rejects native omission of setup marker");
            file=Slot(e);file["playerStore"]["value"]["modData"][NativeActorCheckpoint.PartnerSetupDataKey]="old";WriteSlot(file);
            Fail(()=>pending.VerifyWritten(),"actual readback rejects native substitution of setup marker");
            WriteSlot(Slot(e));pending.VerifyWritten();Check(true,"actual readback confirms both metadata entries in one file image");
        }
        e=Make(setup:setup);
        using(var pending=new NativeActorCheckpoint.Pending(store,7,e,()=>true))
        {
            WriteSlot(Slot(e));store.modData[NativeActorCheckpoint.PartnerSetupDataKey]="later owner";
            Fail(()=>pending.VerifyWritten(),"setup mutation after capture invalidates write context");
        }
        Check(store.modData[NativeActorCheckpoint.PartnerSetupDataKey]=="later owner","checkpoint rollback never erases another setup update");
        file=Slot(e);file["playerStore"]["value"]["modData"][NativeActorCheckpoint.PartnerSetupDataKey]=null;
        using(var reader=ES3Reader.Create(Encoding.UTF8.GetBytes(file.ToString()),new ES3Settings(false)))
            Fail(()=>NativeActorCheckpoint.ReadReceipt(reader),"present null setup marker is not silently treated as unselected");
        Console.WriteLine("PASS "+count+" isolated checkpoint assertions; ES3 runtime NOT_RUN.");
    }
}

public sealed class PlayerStore { public static int Constructions; public PlayerStore(){Constructions++;} public int saveSlotId; public string runID; public string ActorName="host"; public Dictionary<string,string> modData=new(); }
public sealed class StoreStation { }
public sealed class HealthData { }
public sealed class StartingPerk { }
public static class SaveFiles { public static string Slot(int slot)=>Path.Combine(AppContext.BaseDirectory,"synthetic-slot-"+slot+".es3"); }
public static class ES3 { public enum Location { File } public enum Format { JSON } }
public sealed class ES3Settings { public ES3.Location location; public ES3.Format format; public string FullPath; public ES3Settings(string path){FullPath=path;} public ES3Settings(bool defaults){} }
namespace ProbablyCoop {
internal sealed class NativeActorState {
 internal string playerName; internal HealthData healthData; internal List<StartingPerk> startingPerks; internal List<string> appraisalBlacklist;
 internal int appraiserLevel, appraisalPromotionDay, scavengingAttempts, dumpingGroundMajorWoundImmunitySearchesLeft, dumpingGroundMinorWoundImmunitySearchesLeft;
 internal double appraisalProgress; internal bool appraisalPromotionDeclined;
 internal static NativeActorState CreateDefault()=>new();
 internal static NativeActorState Capture(PlayerStore s)=>new(){playerName=s.ActorName};
 internal JObject Serialize()=>new(){["name"]=playerName};
 internal static NativeActorState Deserialize(JToken t){if(t is not JObject o || o.Count!=1 || o["name"]?.Type!=JTokenType.String)throw new InvalidDataException();return new(){playerName=(string)o["name"]};}
}
}
namespace ES3Internal { public sealed class TestType { public Type type; } public struct ES3Data { public TestType type; public byte[] bytes; } }
public sealed class ES3Reader : IDisposable
{
    readonly JObject data; JToken current;
    ES3Reader(JObject value){data=value;}
    public static ES3Reader Create(byte[] bytes,ES3Settings settings)=>new(JObject.Parse(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF')));
    public IEnumerable<string> Properties { get { foreach(var p in data.Properties()){current=p.Value;yield return p.Name;} } }
    public T Read<T>()=>current.Type==JTokenType.Null ? default : current.ToObject<T>();
    public void Skip(){}
    public void Dispose(){}
    public sealed class ES3ReaderRawEnumerator : IEnumerable
    {
        readonly ES3Reader reader; public ES3ReaderRawEnumerator(ES3Reader r){reader=r;}
        public IEnumerator GetEnumerator(){foreach(var p in reader.data.Properties()) yield return new KeyValuePair<string,ES3Internal.ES3Data>(p.Name,new(){type=new(){type=(string)p.Value["__type"]=="PlayerStore"?typeof(PlayerStore):typeof(StoreStation)},bytes=Encoding.UTF8.GetBytes(p.Value["value"].ToString())});}
    }
}
