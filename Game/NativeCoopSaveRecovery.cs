using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using ES3Internal;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ProbablyCoop;

// Recovery is a menu-selection transaction. Discovery and ordinary native
// loads never write files, allocate a slot, or silently start a solo session.
internal static class NativeCoopSaveRecovery
{
    private const string ModId="probably_coop", IndexFile="saves_index.es3";
    private static readonly MethodInfo ReadElement=AccessTools.Method(typeof(ES3Reader),"ReadElement",new[]{typeof(bool)});
    private static readonly MethodInfo WriteRaw=AccessTools.Method(typeof(ES3Writer),"WriteRawProperty",new[]{typeof(string),typeof(byte[])});
    private static readonly MethodInfo CreateWriter=AccessTools.Method(typeof(ES3Writer),"Create",
        new[]{typeof(Stream),typeof(ES3Settings),typeof(bool),typeof(bool)});
    private static readonly MethodInfo CreateScalarReader=AccessTools.Method(typeof(ES3Reader),"Create",
        new[]{typeof(Stream),typeof(ES3Settings),typeof(bool)});

    // Avoid ES3Writer's MemoryStream-specific Clone(), which needlessly
    // consults Unity defaults. Settings and outer stream codecs are explicit.
    private sealed class ImageStream : MemoryStream { }

    internal static CoopRun.Checkpoint Recover(CoopRun.Checkpoint selected)
    {
        if(selected==null || !selected.HasActors || !selected.NeedsRecovery ||
            !CoopRun.TryRead(selected.Slot,selected.Run,out var current,selected.File,true) ||
            !SameSelection(selected,current)) throw Invalid("所选合作档已变化，请刷新列表后重试。");
        string source=SaveFiles.AbsolutePath(current.File);
        byte[] sourceBytes=NativeActorPersistence.ReadFileImage(source);
        if(!SameDigest(sourceBytes,current.ImageDigest)) throw Invalid("恢复前存档已被替换。");
        return Publish(current, source, sourceBytes, false);
    }

    internal static CoopRun.Checkpoint ImportSolo(CoopRun.Checkpoint selected)
    {
        if (selected == null || selected.Historical || selected.ImageDigest == null ||
            !CoopRun.TryRead(selected.Slot, selected.Run, out var current, selected.File, soloOnly: true) ||
            !current.ImageDigest.AsSpan().SequenceEqual(selected.ImageDigest) ||
            ModHelper.ListModDataFiles(ModId).Any(key =>
                CoopRun.TryParseRegistration(key, ModHelper.ReadModDataFileText(ModId, key), out var registered, out _) &&
                registered == current.Run))
            throw Invalid("The selected solo save has changed or already contains co-op data.");
        string source = SaveFiles.AbsolutePath(current.File);
        byte[] sourceBytes = NativeActorPersistence.ReadFileImage(source);
        if (!SameDigest(sourceBytes, current.ImageDigest)) throw Invalid("The selected solo save has changed.");
        return Publish(current, source, sourceBytes, true);
    }

    private static CoopRun.Checkpoint Publish(CoopRun.Checkpoint current, string source, byte[] sourceBytes, bool importSolo)
    {
        int target=current.Slot;
        bool copy=importSolo || current.Historical || current.StoredSlot!=current.Slot;
        Guid run=importSolo ? Guid.NewGuid() : current.Run, nonce=current.Nonce;
        var active=ReadIndex("activeSaveSlotId",new List<int>()) ?? new List<int>();
        bool hadActive=ES3.KeyExists("activeSaveSlotId",IndexFile);
        if(copy) target=FindIndependentSlot(active);
        string file=copy ? SaveFiles.Slot(target) : current.File;
        string path=SaveFiles.AbsolutePath(file);
        string temporary=copy ? Path.Combine(Path.GetDirectoryName(path),".probably-coop-recovery-"+Guid.NewGuid().ToString("N")+".tmp") : null;
        var previousPreview=ReadIndex<SavePreviewData>("preview_"+target,null);
        bool registerNative=copy || !active.Contains(target) || previousPreview==null;
        string marker=CoopRun.RegistrationKey(run,target);
        bool hadMarker=ModHelper.ListModDataFiles(ModId).Contains(marker,StringComparer.Ordinal);
        string previousMarker=hadMarker ? ModHelper.ReadModDataFileText(ModId,marker) : null;
        bool created=false, temporaryCreated=false, nativeAttempted=false, markerAttempted=false;
        byte[] image=null;
        try
        {
            if(copy)
            {
                var settings=new ES3Settings(source);
                image=importSolo
                    ? RewriteSolo(sourceBytes,settings,current.Run,current.StoredSlot,current.Day,run,target,out nonce)
                    : RewriteSlot(sourceBytes,settings,current.StoredSlot,target);
                using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                {
                    temporaryCreated=true;
                    stream.Write(image,0,image.Length);
                    stream.Flush(true);
                }
                byte[] staged=NativeActorPersistence.ReadFileImage(temporary);
                if(!staged.AsSpan().SequenceEqual(image))
                    throw Invalid("恢复副本写入后读回不一致。");
                ValidateImage(staged,settings,run,target,current.Day,nonce);
                // A crash before publication leaves only a non-save temporary
                // file. A crash after it leaves a complete discoverable image.
                // Same-directory Move is atomic and refuses an existing target.
                File.Move(temporary,path);
                created=true;
                temporaryCreated=false;
            }
            // Copies were checked on disk before their atomic move. In-place
            // recovery still needs to validate the destination before indexing.
            if(!copy && (!CoopRun.TryRead(target,run,out var verified,file,true) || !verified.HasActors ||
                verified.StoredSlot!=target || verified.Nonce!=nonce || verified.Day!=current.Day))
                throw Invalid("恢复副本未通过完整双角色校验。");
            if(!SameDigest(NativeActorPersistence.ReadFileImage(source),current.ImageDigest))
                throw Invalid("验证时原档已变化，已取消恢复。");
            if(registerNative)
            {
                nativeAttempted=true;
                var preview=current.Preview.CloneForSlot(target);
                SaveFiles.RegisterSlot(target,preview);
                var observed=ReadIndex<SavePreviewData>("preview_"+target,null);
                if(!ReadIndex("activeSaveSlotId",new List<int>()).Contains(target) ||
                    observed==null || observed.slotId!=target || observed.dayCount!=current.Day || observed.credit!=current.Cash)
                    throw Invalid("原生存档登记未成功持久化。");
            }
            markerAttempted=true;
            CoopRun.WriteRegistration(run,target);
            if(!CoopRun.TryRead(target,run,out var completed,file,true) || !completed.HasActors ||
                completed.NeedsRecovery || completed.Nonce!=nonce || completed.Day!=current.Day ||
                !SameDigest(NativeActorPersistence.ReadFileImage(source),current.ImageDigest))
                throw Invalid("合作登记完成后校验失败。");
            Debug.Log($"[probably_coop] {(importSolo ? "imported solo save" : "recovered cooperative checkpoint")} source={current.Slot} stored={current.StoredSlot} target={target} copied={copy}");
            return completed;
        }
        catch(Exception failure)
        {
            // Roll back only our pair marker, our target index entries and
            // the file we created. Never delete a source/history or old marker.
            if(markerAttempted) Rollback(() => {
                if(hadMarker) ModHelper.WriteModDataFileText(ModId,marker,previousMarker);
                else ModHelper.DeleteModDataFile(ModId,marker);
            },"cooperative marker");
            if(nativeAttempted) Rollback(() => {
                var remaining=ReadIndex("activeSaveSlotId",new List<int>()) ?? new List<int>();
                if(!active.Contains(target)) remaining.Remove(target);
                if(hadActive || remaining.Count!=0) SaveFiles.IndexSave("activeSaveSlotId",remaining);
                else SaveFiles.IndexDeleteKey("activeSaveSlotId");
                if(previousPreview!=null)
                { SaveFiles.IndexSave("preview_"+target,previousPreview); SaveUIManager.UpdateCacheEntry(target,previousPreview); }
                else { SaveFiles.IndexDeleteKey("preview_"+target); SaveUIManager.RemoveCacheEntry(target); }
            },"native registration");
            if(created) Rollback(() => DeleteOwnedCopy(path,image),"recovery copy");
            if(temporaryCreated) Rollback(() => DeleteOwnedCopy(temporary,image),"temporary recovery image");
            if (importSolo && !temporaryCreated && !created && failure is InvalidDataException)
                throw new IOException("The selected solo save could not be converted: " + failure.Message, failure);
            throw new IOException(importSolo ? "Could not create a co-op copy." : "合作档恢复未完成，未载入游戏。",failure);
        }
    }

    internal static NativeActorCheckpoint.Envelope ValidateImage(byte[] image,ES3Settings settings,
        Guid run,int slot,int day,Guid nonce)
    {
        using var reader=ES3Reader.Create(image,settings);
        var receipt=NativeActorCheckpoint.ReadReceipt(reader);
        var value=NativeActorCheckpoint.DecodeReceipt(receipt,run,slot,day);
        if(value.Checkpoint!=nonce || value.PartnerSetup!=null && !NativeActorPartnerSetup.IsComplete(value.PartnerSetup,run))
            throw Invalid("恢复镜像的检查点或伙伴配置不匹配。");
        return value;
    }

    private static bool SameSelection(CoopRun.Checkpoint expected,CoopRun.Checkpoint value) =>
        value.HasActors && value.StoredSlot==expected.StoredSlot && value.Run==expected.Run &&
        value.Day==expected.Day && value.Nonce==expected.Nonce && expected.ImageDigest!=null &&
        value.ImageDigest.AsSpan().SequenceEqual(expected.ImageDigest);

    private static bool SameDigest(byte[] image,byte[] digest) =>
        digest!=null && CoopRun.Digest(image).AsSpan().SequenceEqual(digest);

    private static T ReadIndex<T>(string key,T fallback) => ES3.Load(key,IndexFile,fallback);

    private static int FindIndependentSlot(List<int> active)
    {
        var used=new HashSet<int>(active);
        foreach(string path in System.IO.Directory.EnumerateFiles(Application.persistentDataPath,"save_*.es3"))
            if(CoopRun.TryFileSlot(Path.GetFileName(path),out int slot,out _)) used.Add(slot);
        foreach(string key in ModHelper.ListModDataFiles(ModId))
            if(CoopRun.TryParseRegistration(key,ModHelper.ReadModDataFileText(ModId,key),out _,out int slot)) used.Add(slot);
        // Do not call NextFreeSlotId: it writes the generator before we own a
        // file. The native allocator already skips any existing canonical file.
        for(int slot=Math.Max(0,ReadIndex("slotIdGenerator",0));slot<int.MaxValue;slot++)
            if(!used.Contains(slot) && !ES3.KeyExists("preview_"+slot,IndexFile) &&
                !ES3.KeyExists("history_"+slot,IndexFile)) return slot;
        throw Invalid("没有可用的独立恢复槽位。");
    }

    private static void DeleteOwnedCopy(string path,byte[] image)
    {
        if(!File.Exists(path)) return;
        byte[] observed=File.ReadAllBytes(path);
        // Also recognize a failed write's exact prefix. A subsequently
        // replaced, unrelated file is preserved rather than deleted.
        if(image==null || observed.Length>image.Length ||
            !observed.AsSpan().SequenceEqual(image.AsSpan(0,observed.Length)))
            throw Invalid("恢复目标已被其他写入替换，保留该文件以免误删。");
        File.Delete(path);
    }

    private static void Rollback(Action rollback,string part)
    {
        try { rollback(); }
        catch(Exception error) { Debug.LogError("[probably_coop] recovery rollback failed for "+part+": "+error); }
    }

    // Narrow ES3 transformation: retain every raw outer entry (including
    // unresolved future types), wrapper, player field and other modData value.
    // Only the native slot scalar and the checkpoint's outer slot are changed.
    internal static byte[] RewriteSlot(byte[] source,ES3Settings settings,int storedSlot,int targetSlot)
    {
        if(source==null || source.Length==0 || source.Length>NativeActorCheckpoint.MaximumSlotBytes ||
            settings==null || settings.format!=ES3.Format.JSON || storedSlot<0 || targetSlot<0 || storedSlot==targetSlot)
            throw Invalid("无效的存档槽迁移请求。");
        var root=ReadObject(source,settings);
        byte[] player=Find(root,"playerStore");
        var wrapper=ReadObject(player,PlainSettings());
        var body=ReadObject(Find(wrapper,"value"),PlainSettings());
        if(ReadScalar<int>(Find(body,"saveSlotId"))!=storedSlot) throw Invalid("原生内部槽位已变化。");
        var modData=ReadObject(Find(body,"modData"),PlainSettings());
        string encoded=ReadScalar<string>(Find(modData,NativeActorCheckpoint.ModDataKey));
        string migrated=RewriteCheckpointSlot(encoded,storedSlot,targetSlot);
        Replace(modData,NativeActorCheckpoint.ModDataKey,Scalar(migrated));
        Replace(body,"saveSlotId",Scalar(targetSlot));
        Replace(body,"modData",WriteObject(modData));
        Replace(wrapper,"value",WriteObject(body));
        Replace(root,"playerStore",WriteObject(wrapper));
        return WriteCheckedImage(root,settings);
    }

    internal static byte[] RewriteSolo(byte[] source, ES3Settings settings, Guid sourceRun, int storedSlot, int day,
        Guid run, int targetSlot, out Guid nonce)
    {
        if (source == null || source.Length == 0 || source.Length > NativeActorCheckpoint.MaximumSlotBytes ||
            settings == null || settings.format != ES3.Format.JSON || sourceRun == Guid.Empty ||
            run == Guid.Empty || run == sourceRun || storedSlot < 0 || targetSlot < 0)
            throw Invalid("Invalid solo save identity.");
        var root = ReadObject(source, settings);
        using var reader = ES3Reader.Create(source, settings);
        var receipt = NativeActorCheckpoint.ReadReceiptEntries(NativeActorCheckpoint.ReadNativeEntries(reader), fromSolo: true);
        if (receipt.Run != sourceRun || receipt.Slot != storedSlot || receipt.Day != day)
            throw Invalid("The selected solo save no longer matches its preview.");
        var envelope = NativeActorCheckpoint.CreateFromSolo(receipt, run, targetSlot);
        nonce = envelope.Checkpoint;

        var wrapper = ReadObject(Find(root, "playerStore"), PlainSettings());
        var body = ReadObject(Find(wrapper, "value"), PlainSettings());
        byte[] nativeModData = Find(body, "modData");
        var modData = ReadScalar<Dictionary<string, string>>(nativeModData) == null
            ? new List<KeyValuePair<string, byte[]>>()
            : ReadObject(nativeModData, PlainSettings());
        modData.Add(new KeyValuePair<string, byte[]>(NativeActorCheckpoint.ModDataKey, Scalar(envelope.Encoded)));
        Replace(body, "runID", Scalar(run.ToString("D")));
        Replace(body, "saveSlotId", Scalar(targetSlot));
        Replace(body, "modData", WriteObject(modData));
        Replace(wrapper, "value", WriteObject(body));
        Replace(root, "playerStore", WriteObject(wrapper));
        return WriteCheckedImage(root, settings);
    }

    private static byte[] WriteCheckedImage(List<KeyValuePair<string, byte[]>> root, ES3Settings settings)
    {
        byte[] image=WriteObject(root,settings);
        if(image.Length>NativeActorCheckpoint.MaximumSlotBytes) throw Invalid("恢复副本超出存档大小限制。");
        // Prove even unknown/unresolved outer entries survived byte-for-byte.
        var reread=ReadObject(image,settings);
        if(root.Count!=reread.Count) throw Invalid("恢复副本丢失原生条目。");
        for(int i=0;i<root.Count;i++)
            if(root[i].Key!=reread[i].Key || !root[i].Value.AsSpan().SequenceEqual(reread[i].Value))
                throw Invalid("恢复副本改变了原生条目。");
        return image;
    }

    internal static string RewriteCheckpointSlot(string encoded,int storedSlot,int targetSlot)
    {
        using var text=new StringReader(encoded ?? throw Invalid("缺少合作检查点。"));
        using var reader=new JsonTextReader(text) { DateParseHandling=DateParseHandling.None };
        var value=JObject.Load(reader,new JsonLoadSettings { DuplicatePropertyNameHandling=DuplicatePropertyNameHandling.Error });
        if(reader.Read() || value["slot"]?.Type!=JTokenType.Integer ||
            (int)value["slot"]!=storedSlot || storedSlot<0 || targetSlot<0 || storedSlot==targetSlot)
            throw Invalid("合作检查点内部槽位不匹配。");
        value["slot"]=targetSlot;
        return value.ToString(Formatting.None);
    }

    internal static bool HasActorPayload(byte[] image,ES3Settings settings,out Guid run,out int storedSlot)
    {
        run=Guid.Empty; storedSlot=-1;
        // Inspect raw, uniquely named fields before deciding a native load is
        // solo. Returning after the first typed player entry could otherwise
        // hide a second player/modData entry carrying cooperative state.
        var root=ReadObject(image,settings);
        byte[] player=null;
        foreach(var field in root)
            if(field.Key=="playerStore") player=field.Value;
        if(player==null) return false;
        var wrapper=ReadObject(player,PlainSettings());
        var body=ReadObject(Find(wrapper,"value"),PlainSettings());
        bool actors=false;
        foreach(var field in body)
        {
            switch(field.Key)
                {
                    case "runID": Guid.TryParse(ReadScalar<string>(field.Value),out run); break;
                    case "saveSlotId": storedSlot=ReadScalar<int>(field.Value); break;
                    case "modData": actors=ReadScalar<Dictionary<string,string>>(field.Value)?.ContainsKey(NativeActorCheckpoint.ModDataKey)==true; break;
                }
        }
        return actors;
    }

    internal static void ValidateRootNames(byte[] image,ES3Settings settings)
    {
        // ES3's typed raw enumerator drops unresolved types. Check the original
        // property stream first so unknown entries cannot hide duplicate keys.
        var names=new HashSet<string>(StringComparer.Ordinal);
        using var reader=ES3Reader.Create(image,settings);
        foreach(string name in reader.Properties)
        {
            if(!names.Add(name)) throw Invalid("存档包含重复字段："+name);
            reader.Skip();
        }
    }

    private static ES3Settings PlainSettings() => new ES3Settings(false) { location=ES3.Location.InternalMS,prettyPrint=false };

    private static List<KeyValuePair<string,byte[]>> ReadObject(byte[] bytes,ES3Settings settings)
    {
        var values=new List<KeyValuePair<string,byte[]>>();
        var names=new HashSet<string>(StringComparer.Ordinal);
        using var reader=ES3Reader.Create(bytes,settings);
        foreach(string name in reader.Properties)
        {
            if(!names.Add(name)) throw Invalid("存档包含重复字段："+name);
            values.Add(new KeyValuePair<string,byte[]>(name,(byte[])ReadElement.Invoke(reader,new object[]{false})));
        }
        return values;
    }

    private static byte[] WriteObject(List<KeyValuePair<string,byte[]>> fields,ES3Settings outer=null)
    {
        using var stream=new ImageStream();
        Stream output=outer==null ? stream : ES3Stream.CreateStream(stream,outer,ES3FileMode.Write);
        using(var writer=(ES3Writer)CreateWriter.Invoke(null,new object[]{output,PlainSettings(),true,false}))
        {
            foreach(var field in fields) WriteRaw.Invoke(writer,new object[]{field.Key,field.Value});
            writer.Save(false);
        }
        return stream.ToArray();
    }

    private static T ReadScalar<T>(byte[] bytes)
    {
        using var stream=new MemoryStream(bytes,false);
        using var reader=(ES3Reader)CreateScalarReader.Invoke(null,new object[]{stream,PlainSettings(),false});
        return reader.Read<T>();
    }

    private static byte[] Scalar<T>(T value)
    {
        using var stream=new ImageStream();
        using(var writer=(ES3Writer)CreateWriter.Invoke(null,new object[]{stream,PlainSettings(),true,false}))
        { writer.WriteProperty<T>("v",value); writer.Save(false); }
        return Find(ReadObject(stream.ToArray(),PlainSettings()),"v");
    }

    private static byte[] Find(List<KeyValuePair<string,byte[]>> fields,string name)
    {
        foreach(var field in fields) if(field.Key==name) return field.Value;
        throw Invalid("存档缺少必要字段："+name);
    }

    private static void Replace(List<KeyValuePair<string,byte[]>> fields,string name,byte[] value)
    {
        for(int i=0;i<fields.Count;i++) if(fields[i].Key==name)
        { fields[i]=new KeyValuePair<string,byte[]>(name,value); return; }
        throw Invalid("存档缺少必要字段："+name);
    }

    private static InvalidDataException Invalid(string message) => new InvalidDataException(message);
}
