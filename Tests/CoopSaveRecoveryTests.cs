using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

// Runs the supplied compiled Mod with actual ES3, without Unity gameplay,
// native save registration, personal save writes, or a linked fake ES3 codec.
internal static class CoopSaveRecoveryTests
{
    private const BindingFlags Static=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static;
    private const BindingFlags Instance=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance;

    internal static void Run(string assemblyPath,string gameManagedPath,string sourcePath=null)
    {
        var context=new References(Path.GetFullPath(gameManagedPath));
        int count=0;
        void Require(bool ok,string name)
        { count++; if(!ok) throw new InvalidOperationException("FAIL cooperative recovery: "+name); }
        void Reject(Action action,string name)
        {
            try { action(); }
            catch(TargetInvocationException error) when(error.InnerException is InvalidDataException || error.InnerException is IOException ||
                error.InnerException?.GetType().FullName?.StartsWith("Newtonsoft.Json.",StringComparison.Ordinal)==true)
            { count++; return; }
            throw new InvalidOperationException("FAIL cooperative recovery rejection: "+name);
        }
        try
        {
            var mod=context.LoadFromAssemblyPath(Path.GetFullPath(assemblyPath));
            var coop=mod.GetType("ProbablyCoop.CoopRun",true);
            var recovery=mod.GetType("ProbablyCoop.NativeCoopSaveRecovery",true);
            Guid run=Guid.Parse("0cd319c8-2cda-416e-b98b-8da529698ed5");
            string legacy="coop_"+run.ToString("N")+".txt";
            var parse=coop.GetMethod("TryParseRegistration",Static);
            bool Marker(string key,string value,int slot)
            {
                object[] args={key,value,Guid.Empty,-1};
                return (bool)parse.Invoke(null,args) && (Guid)args[2]==run && (int)args[3]==slot;
            }
            Require(Marker(legacy,"10",10),"old single-run marker still accepted");
            Require(Marker("coop_"+run.ToString("N")+"_10.txt","10",10),"new run-slot marker accepted");
            Require(Marker("coop_"+run.ToString("N")+"_14.txt","14",14),"same run independently registers second slot");
            foreach(var bad in new[]{"-1","2147483648","10\n","10 ","../10",""})
                Require(!Marker(legacy,bad,10),"invalid marker content rejected");
            Require(!Marker("coop_"+run.ToString("N")+"_14.txt","10",10),"marker key and content must agree");
            Require(!Marker("coop_"+Guid.Empty.ToString("N")+".txt","10",10),"empty run rejected");
            Require(!Marker("../"+legacy,"10",10),"marker traversal rejected");
            var parseFile=coop.GetMethod("TryFileSlot",Static);
            bool SaveFile(string file,int slot,bool historical)
            {
                object[] args={file,-1,false};
                return (bool)parseFile.Invoke(null,args) && (int)args[1]==slot && (bool)args[2]==historical;
            }
            Require(SaveFile("save_10.es3",10,false),"canonical file identity parsed");
            Require(SaveFile("save_14_day125_20261113-130457.es3",14,true),"historical file identity parsed");
            Require(!SaveFile("save_2147483648_day125_20261113-130457.es3",0,false),"overflowing historical slot rejected without escaping");
            Require(!SaveFile("save_14_day2147483648_20261113-130457.es3",0,false),"overflowing historical day rejected without escaping");
            Require(!SaveFile("../save_10.es3",10,false),"save filename traversal rejected");
            var remap=recovery.GetMethod("RewriteCheckpointSlot",Static);
            string sample="{\"slot\":10,\"run\":\""+run+"\",\"partnerSetup\":{\"slot\":1},\"text\":\"kept\"}";
            string changed=(string)remap.Invoke(null,new object[]{sample,10,77});
            var value=JObject.Parse(changed);
            Require((int)value["slot"]==77 && (int)value["partnerSetup"]["slot"]==1,"only outer save slot changes, partner role stays one");
            Require((string)value["run"]==run.ToString() && (string)value["text"]=="kept","run and checkpoint data retained");
            Reject(()=>remap.Invoke(null,new object[]{sample,14,77}),"wrong expected source slot");
            Reject(()=>remap.Invoke(null,new object[]{sample,10,10}),"same-slot migration is not a copy");
            Reject(()=>remap.Invoke(null,new object[]{"{\"slot\":10,\"slot\":10}",10,77}),"duplicate checkpoint fields");
            if(sourcePath!=null)
            {
                byte[] original=File.ReadAllBytes(Path.GetFullPath(sourcePath));
                byte[] originalDigest=SHA256.HashData(original);
                var settingsType=context.LoadFromAssemblyName(new AssemblyName("Assembly-CSharp-firstpass")).GetType("ES3Settings",true);
                object settings=Activator.CreateInstance(settingsType,new object[]{false});
                var rewrite=recovery.GetMethod("RewriteSlot",Static);
                var readObject=recovery.GetMethod("ReadObject",Static);
                var readScalar=recovery.GetMethod("ReadScalar",Static);
                object plain=recovery.GetMethod("PlainSettings",Static).Invoke(null,null);
                List<KeyValuePair<string,byte[]>> Fields(byte[] bytes,object options=null) =>
                    (List<KeyValuePair<string,byte[]>>)readObject.Invoke(null,new object[]{bytes,options??plain});
                byte[] Field(List<KeyValuePair<string,byte[]>> fields,string name) => fields.Single(x=>x.Key==name).Value;
                T Scalar<T>(byte[] bytes) => (T)readScalar.MakeGenericMethod(typeof(T)).Invoke(null,new object[]{bytes});
                void Unchanged(List<KeyValuePair<string,byte[]>> beforeFields,List<KeyValuePair<string,byte[]>> afterFields,
                    string label,params string[] changedFields)
                {
                    Require(beforeFields.Select(x=>x.Key).SequenceEqual(afterFields.Select(x=>x.Key)),label+" keys and order retained");
                    foreach(var field in beforeFields)
                        if(!changedFields.Contains(field.Key,StringComparer.Ordinal))
                            Require(field.Value.SequenceEqual(Field(afterFields,field.Key)),label+" raw value retained: "+field.Key);
                }
                var source=Fields(original,settings);
                var player=Fields(Field(source,"playerStore"));
                var body=Fields(Field(player,"value"));
                var modData=Fields(Field(body,"modData"));
                const string payloadKey="probably_coop:actor_checkpoint_v1";
                int stored=Scalar<int>(Field(body,"saveSlotId"));
                int target=checked(stored+10000);
                byte[] image=(byte[])rewrite.Invoke(null,new object[]{original,settings,stored,target});
                var result=Fields(image,settings);
                var resultPlayer=Fields(Field(result,"playerStore"));
                var resultBody=Fields(Field(resultPlayer,"value"));
                var resultMods=Fields(Field(resultBody,"modData"));
                var before=JObject.Parse(Scalar<string>(Field(modData,payloadKey)));
                var after=JObject.Parse(Scalar<string>(Field(resultMods,payloadKey)));
                before["slot"]=target;
                Require(JToken.DeepEquals(before,after),"actual ES3 checkpoint changes exactly its outer slot");
                Require(Scalar<int>(Field(resultBody,"saveSlotId"))==target,"actual ES3 native save identity migrated");
                Unchanged(source,result,"native root","playerStore");
                Unchanged(player,resultPlayer,"player wrapper","value");
                Unchanged(body,resultBody,"player body","saveSlotId","modData");
                Unchanged(modData,resultMods,"other mod data",payloadKey);
                Require(originalDigest.SequenceEqual(SHA256.HashData(File.ReadAllBytes(sourcePath))),"original attachment remains byte-identical");
                string withUnknown=Encoding.UTF8.GetString(original).TrimEnd();
                withUnknown=withUnknown.Substring(0,withUnknown.Length-1)+
                    ",\"future-extension\":{\"__type\":\"Missing.FutureType, MissingAssembly\",\"value\":{\"literal\":\"kept\\“smart\\”\",\"nested\":[1,2,3]}}}";
                byte[] unknownImage=Encoding.UTF8.GetBytes(withUnknown);
                byte[] future=(byte[])rewrite.Invoke(null,new object[]{unknownImage,settings,stored,target});
                Require(Field(Fields(unknownImage,settings),"future-extension").SequenceEqual(
                    Field(Fields(future,settings),"future-extension")),"unknown ES3 type entry is retained without type resolution");
                Reject(()=>rewrite.Invoke(null,new object[]{original,settings,stored+1,target}),"actual native source identity mismatch");
                Require(image.SequenceEqual((byte[])rewrite.Invoke(null,new object[]{original,settings,stored,target})),"uncompressed ES3 migration is deterministic");

                var readerType=settingsType.Assembly.GetType("ES3Reader",true);
                var createReader=readerType.GetMethod("Create",Static,null,new[]{typeof(byte[]),settingsType},null);
                var checkpointType=coop.GetNestedType("Checkpoint",BindingFlags.NonPublic);
                var readPrepared=coop.GetMethod("ReadPreparedReceipt",Static);
                var checkpointCodec=mod.GetType("ProbablyCoop.NativeActorCheckpoint",true);
                var readReceipt=checkpointCodec.GetMethod("ReadReceipt",Static);
                var previewField=checkpointType.GetField("Preview",Instance);
                object Prepare(byte[] bytes)
                {
                    var checkpoint=Activator.CreateInstance(checkpointType,true);
                    previewField.SetValue(checkpoint,Activator.CreateInstance(previewField.FieldType));
                    return readPrepared.Invoke(null,new[]{bytes,settings,checkpoint});
                }
                using var legacyReader=(IDisposable)createReader.Invoke(null,new[]{original,settings});
                object legacyReceipt=readReceipt.Invoke(null,new object[]{legacyReader,null});
                object prepared=Prepare(original);
                foreach(string key in new[]{"Run","Slot","Day","Encoded","CurrentUniqueId","PartnerSetup","UsesEffectReplication"})
                {
                    var field=prepared.GetType().GetField(key,Instance);
                    Require(Equals(field.GetValue(legacyReceipt),field.GetValue(prepared)),"one-pass receipt retains native "+key);
                }
                var inventories=prepared.GetType().GetField("NativeInventories",Instance);
                Require(((Dictionary<string,string>)inventories.GetValue(legacyReceipt)).OrderBy(x=>x.Key)
                    .SequenceEqual(((Dictionary<string,string>)inventories.GetValue(prepared)).OrderBy(x=>x.Key)),
                    "one-pass receipt retains every native inventory");
                Require(Prepare(unknownImage)!=null,"one-pass receipt skips unresolved unrelated wrappers");
                byte[] repeated=Encoding.UTF8.GetBytes(withUnknown.Substring(0,withUnknown.Length-1)+
                    ",\"future-extension\":{\"value\":{}}}");
                Reject(()=>Prepare(repeated),"one-pass receipt rejects duplicate unresolved root key");
                // Some ES3 strings have game-specific escapes. Change only the
                // top-level type header and leave the original image intact.
                string es3=Encoding.UTF8.GetString(original);
                var header=new System.Text.RegularExpressions.Regex(
                    @"(""playerStore""\s*:\s*\{\s*""__type""\s*:\s*)""[^""]+""");
                string incorrect=header.Replace(es3,"$1\"StoreStation, Assembly-CSharp\"",1);
                Require(incorrect!=es3,"fixture contains the native player type header");
                Reject(()=>Prepare(Encoding.UTF8.GetBytes(incorrect)),"one-pass receipt rejects mismatched player type");
            }
            string folder=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(assemblyPath)),"recovery-test-"+Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(folder);
            try
            {
                var remove=recovery.GetMethod("DeleteOwnedCopy",Static);
                string file=Path.Combine(folder,"owned-copy.es3");
                byte[] expected={11,22,33,44};
                foreach(byte[] data in new[]{expected,new byte[]{11,22},Array.Empty<byte>()})
                {
                    File.WriteAllBytes(file,data);
                    remove.Invoke(null,new object[]{file,expected});
                    Require(!File.Exists(file),"rollback deletes only its own full/partial/empty created image");
                }
                File.WriteAllBytes(file,new byte[]{99,22,33,44});
                Reject(()=>remove.Invoke(null,new object[]{file,expected}),"rollback refuses unrelated replacement");
                Require(File.ReadAllBytes(file)[0]==99,"unrelated replacement remains intact");
                File.Delete(file);
            }
            finally { System.IO.Directory.Delete(folder,false); }
            Console.WriteLine($"PASS compiled cooperative save recovery: {count} checks; actualES3={sourcePath!=null}; no gameplay or native index writes");
        }
        finally { context.Unload(); }
    }

    private sealed class References : AssemblyLoadContext
    {
        private readonly string folder;
        internal References(string folder):base("CoopRecovery-"+Guid.NewGuid().ToString("N"),true) => this.folder=folder;
        protected override Assembly Load(AssemblyName name)
        {
            if(name.Name=="mscorlib" || name.Name=="netstandard" || name.Name=="System" ||
                name.Name.StartsWith("System.",StringComparison.Ordinal)) return null;
            string file=Path.Combine(folder,name.Name+".dll");
            return File.Exists(file) ? LoadFromAssemblyPath(file) : null;
        }
    }
}
