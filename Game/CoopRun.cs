using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using ES3Internal;
using ES3Types;
using HarmonyLib;
using UnityEngine;

namespace ProbablyCoop;

internal static class CoopRun
{
    private const string ModId = "probably_coop";
    private const string MarkerPrefix = "coop_";
    internal static bool Armed { get; set; }
    private static PlayerStore creating;
    private static PreparedResume pendingResume;
    private sealed class ReadCacheEntry
    {
        internal long Length;
        internal long LastWriteUtcTicks;
        internal Checkpoint Checkpoint;
    }
    private static readonly Dictionary<string,ReadCacheEntry> readCache =
        new Dictionary<string,ReadCacheEntry>(StringComparer.OrdinalIgnoreCase);
    internal static bool IsCreating(PlayerStore store) => ReferenceEquals(creating, store) && store != null;

    internal sealed class Checkpoint
    {
        internal int Slot;
        internal int StoredSlot;
        internal Guid Run;
        internal int Day;
        internal int Cash;
        internal string Name;
        internal string Locale;
        internal DateTime SavedAt;
        internal bool HasActors;
        internal bool HasCoopData;
        internal bool NeedsRecovery;
        internal string RecoveryReason;
        internal bool UsesEffectReplication;
        internal Guid Nonce;
        internal string File;
        internal string UnavailableReason;
        internal PreparedResume Prepared;
        internal byte[] ImageDigest;
        internal SavePreviewData Preview;
        internal bool PreviewOnly;
        internal bool Historical => File != SaveFiles.Slot(Slot);
    }

    internal sealed class PreparedResume
    {
        private readonly string path;
        private readonly byte[] digest;
        private readonly NativeActorCheckpoint.Envelope envelope;
        internal NewGameData Request;

        internal PreparedResume(string filePath, byte[] imageDigest, NativeActorCheckpoint.Envelope envelope)
        {
            path=Path.GetFullPath(filePath);
            digest=(byte[])imageDigest.Clone();
            this.envelope=envelope;
        }

        internal NativeActorCheckpoint.Envelope Match(string filePath, int slot, HashSet<Guid> registered, byte[] bytes)
        {
            if (slot!=envelope.Slot || !registered.Contains(envelope.Run) ||
                !string.Equals(Path.GetFullPath(filePath),path,StringComparison.OrdinalIgnoreCase)) return null;
            return digest.AsSpan().SequenceEqual(Digest(bytes)) ? envelope : null;
        }
    }

    // Only InitialSave allocates a new native slot and assigns its new run ID.
    [HarmonyPatch(typeof(PlayerStore), nameof(PlayerStore.InitialSave))]
    private static class RegisterNewRun
    {
        private static void Prefix(PlayerStore __instance, out bool __state)
        {
            __state = !NativeGuest.Active && Armed && NewGameData.Instance != null && NewGameData.Instance.saveSlot < 0;
            Armed = false;
            if (__state)
            {
                if (creating != null) throw new InvalidOperationException("Overlapping cooperative InitialSave.");
                creating = __instance;
            }
        }

        private static void Postfix(PlayerStore __instance, bool __state)
        {
            if (!__state || !Identity(__instance, out var run)) return;
            try
            {
                int slot = __instance.saveSlotId;
                if (!NativeActorPersistence.VerifyObservedSave(__instance, StoreStation.Instance.dayCounter))
                    throw new IOException("The two-actor checkpoint was not confirmed on disk.");
                WriteRegistration(run,slot);
                Debug.Log($"[probably_coop] registered new cooperative slot {slot}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[probably_coop] cooperative registration failed: {ex.Message}");
                NativeActorSession.Current?.MarkFault(ex);
                throw;
            }
        }

        private static Exception Finalizer(PlayerStore __instance, Exception __exception)
        {
            if (ReferenceEquals(creating, __instance)) creating = null;
            return __exception;
        }
    }

    internal static bool IsRegistered(PlayerStore store)
    {
        if (!Identity(store, out var run)) return false;
        try
        {
            return IsRegistered(run,store.saveSlotId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    internal static List<Checkpoint> ListCheckpoints(bool includeHistory = false, int? onlySlot = null)
    {
        var checkpoints = new List<Checkpoint>();
        foreach(var checkpoint in ScanCheckpoints(includeHistory,onlySlot))
            if(checkpoint!=null) checkpoints.Add(checkpoint);
        checkpoints.Sort((a, b) => b.SavedAt.CompareTo(a.SavedAt));
        return checkpoints;
    }

    // Null steps still consume a scan budget: a large directory of solo or
    // damaged saves must not become one uninterruptible MoveNext call.
    internal static IEnumerable<Checkpoint> ScanCheckpoints(bool includeHistory,int? onlySlot=null,bool previews=false)
    {
        var registered = new Dictionary<int,HashSet<Guid>>();
        foreach (string key in ModHelper.ListModDataFiles(ModId))
        {
            if(key.StartsWith(MarkerPrefix,StringComparison.Ordinal) && key.EndsWith(".txt",StringComparison.Ordinal) &&
                TryParseRegistration(key,ModHelper.ReadModDataFileText(ModId,key),out var run,out int slot) &&
                (!onlySlot.HasValue || onlySlot.Value==slot))
            {
                if(!registered.TryGetValue(slot,out var runs)) registered[slot]=runs=new HashSet<Guid>();
                runs.Add(run);
            }
            yield return null;
        }
        foreach(var entry in registered)
        {
            string file=SaveFiles.Slot(entry.Key);
            Checkpoint checkpoint=null;
            if(previews && !includeHistory && entry.Value.Count==1)
                foreach(var run in entry.Value) TryBuildRegisteredPreview(run,entry.Key,out checkpoint);
            if(checkpoint!=null) yield return checkpoint;
            else foreach(var parsed in ScanPreview(entry.Key,file,previews))
                yield return parsed!=null && (parsed.HasActors || entry.Value.Contains(parsed.Run)) ? parsed : null;
        }
        // Discovery is deliberately read-only: native import/duplicate can
        // retain a valid two-actor payload while losing its external marker.
        foreach(string path in System.IO.Directory.EnumerateFiles(Application.persistentDataPath,"save_*.es3"))
        {
            string file=Path.GetFileName(path);
            if(TryFileSlot(file,out int slot,out bool history) && (!history || includeHistory) &&
                (!onlySlot.HasValue || onlySlot.Value==slot) && (history || !registered.ContainsKey(slot)))
            {
                foreach(var checkpoint in ScanPreview(slot,file,previews))
                    yield return checkpoint?.HasActors==true ? checkpoint : null;
            }
            else yield return null;
        }
    }

    private static IEnumerable<Checkpoint> ScanPreview(int slot,string file,bool previews)
    {
        if(!previews)
        {
            yield return TryRead(slot,Guid.Empty,out var full,file) ? full : null;
            yield break;
        }
        string path=SaveFiles.AbsolutePath(file);
        var info=new FileInfo(path);
        if(!info.Exists) { yield return null; yield break; }
        long length=info.Length, modified=info.LastWriteTimeUtc.Ticks;
        if(readCache.TryGetValue(path,out var cached) && cached.Length==length && cached.LastWriteUtcTicks==modified)
        {
            if(cached.Checkpoint?.HasActors==true) SetRecovery(cached.Checkpoint);
            yield return cached.Checkpoint;
            yield break;
        }
        var settings=new ES3Settings(path);
        if(settings.location!=ES3.Location.File || settings.format!=ES3.Format.JSON)
        { yield return null; yield break; }
        // Resolve game serializers here. The worker uses only ES3's lexical reader
        // and these primitive readers, avoiding its shared type cache and Unity state.
        var stringType=ES3TypeMgr.GetOrCreateES3Type(typeof(string));
        var intType=ES3TypeMgr.GetOrCreateES3Type(typeof(int));
        var preview=new Checkpoint { Slot=slot,StoredSlot=-1, Day=-1, File=file, SavedAt=info.LastWriteTime,
            PreviewOnly=true };
        using var cancel=new CancellationTokenSource();
        var work=CoopBackgroundWork.Start(cancel.Token, token => ReadMenuPreview(path,settings,preview,stringType,intType,token));
        try
        {
            while(!work.IsCompleted) yield return null;
            if(work.GetAwaiter().GetResult() is Exception error)
                Debug.LogWarning($"[probably_coop] slot {slot} checkpoint preview could not be read: {error.Message}");
        }
        finally { cancel.Cancel(); }
        Checkpoint result=preview.Day>=0 && preview.StoredSlot>=0 && preview.Run!=Guid.Empty ? preview : null;
        info.Refresh();
        if(!info.Exists || info.Length!=length || info.LastWriteTimeUtc.Ticks!=modified) { yield return null; yield break; }
        if(result?.HasActors==true) SetRecovery(result);
        if(readCache.Count>=512) readCache.Clear();
        readCache[path]=new ReadCacheEntry { Length=length, LastWriteUtcTicks=modified, Checkpoint=result };
        yield return result;
    }

    private static Exception ReadMenuPreview(string path,ES3Settings settings,Checkpoint result,ES3Type stringType,
        ES3Type intType,CancellationToken token)
    {
        try
        {
            using var reader=(ES3JSONReader)ES3Reader.Create(NativeActorPersistence.ReadFileImage(path),settings);
            foreach(string key in reader.Properties)
            {
                token.ThrowIfCancellationRequested();
                if(key!="playerStore" && key!="storeStation") { reader.Skip(); continue; }
                ReadPreviewBrace(reader,'{');
                string property=reader.ReadPropertyName();
                if(property=="__type") { reader.Skip(); property=reader.ReadPropertyName(); }
                if(property!="value") throw new InvalidDataException("Missing preview payload.");
                ReadPreviewBrace(reader,'{');
                foreach(string name in reader.Properties)
                {
                    token.ThrowIfCancellationRequested();
                    if(key=="storeStation")
                    {
                        if(name=="dayCounter") result.Day=reader.Read<int>(intType);
                        else reader.Skip();
                    }
                    else switch(name)
                    {
                        case "saveSlotId": result.StoredSlot=reader.Read<int>(intType); break;
                        case "runID": Guid.TryParse(reader.Read<string>(stringType),out result.Run); break;
                        case "playerCash": result.Cash=reader.Read<int>(intType); break;
                        case "storeName": result.Name=reader.Read<string>(stringType); break;
                        case "startLocale": result.Locale=reader.Read<string>(stringType); break;
                        case "modData":
                            if(reader.baseReader.Peek()=='n') { reader.Skip(); break; }
                            ReadPreviewBrace(reader,'{');
                            foreach(string modKey in reader.Properties)
                            {
                                if(modKey==NativeActorCheckpoint.ModDataKey) result.HasActors=true;
                                reader.Skip();
                            }
                            ReadPreviewBrace(reader,'}');
                            break;
                        default: reader.Skip(); break;
                    }
                }
                ReadPreviewBrace(reader,'}');
                ReadPreviewBrace(reader,'}');
                if(result.Day>=0 && result.StoredSlot>=0 && result.Run!=Guid.Empty) break;
            }
            if(string.IsNullOrEmpty(result.Name)) result.Name="Cooperative store";
            return null;
        }
        catch(OperationCanceledException) { return null; }
        catch(Exception error) { result.Day=-1; return error; }
    }

    private static void ReadPreviewBrace(ES3JSONReader reader,char expected)
    {
        int next;
        do { next=reader.baseReader.Read(); } while(next>=0 && char.IsWhiteSpace((char)next));
        if(next!=expected) throw new InvalidDataException("Invalid preview object.");
    }

    private static bool TryBuildRegisteredPreview(Guid run,int slot,out Checkpoint checkpoint)
    {
        checkpoint=null;
        if(run==Guid.Empty || slot<0) return false;
        // The game's GetPreview also loads every other slot and can rebuild a damaged index.
        SavePreviewData preview;
        try { preview=ES3.Load<SavePreviewData>("preview_"+slot,SaveFiles.Index,default(SavePreviewData)); }
        catch(Exception ex) when(ex is IOException or FormatException or ArgumentException) { return false; }
        if(preview==null) return false;
        string path=SaveFiles.AbsolutePath(SaveFiles.Slot(slot));
        if(!File.Exists(path)) return false;
        var modified=File.GetLastWriteTime(path);
        if(preview.lastSaveDateTime.Year>1 &&
            Math.Abs((modified-preview.lastSaveDateTime).TotalMinutes)>10) return false;
        checkpoint=new Checkpoint {
            Slot=slot,StoredSlot=slot,Run=run,Day=preview.dayCount,Cash=preview.credit,
            Name=string.IsNullOrEmpty(preview.startName) ? "Cooperative store" : preview.startName,
            Locale=preview.startLocale,SavedAt=modified,
            HasActors=true,HasCoopData=true,File=SaveFiles.Slot(slot),Preview=preview,
            PreviewOnly=true
        };
        return true;
    }

    internal static List<Checkpoint> ListSoloCheckpoints()
    {
        var registered = new HashSet<Guid>();
        foreach (string key in ModHelper.ListModDataFiles(ModId))
            if (TryParseRegistration(key, ModHelper.ReadModDataFileText(ModId, key), out var run, out _))
                registered.Add(run);
        var checkpoints = new List<Checkpoint>();
        foreach (string path in System.IO.Directory.EnumerateFiles(Application.persistentDataPath, "save_*.es3"))
        {
            string file = Path.GetFileName(path);
            if (TryFileSlot(file, out int slot, out bool history) && !history &&
                TryRead(slot, Guid.Empty, out var checkpoint, file, soloOnly: true) &&
                !registered.Contains(checkpoint.Run))
                checkpoints.Add(checkpoint);
        }
        checkpoints.Sort((a, b) => b.SavedAt.CompareTo(a.SavedAt));
        return checkpoints;
    }

    internal static Checkpoint ImportSolo(Checkpoint selected, out string reason)
    {
        try
        {
            var imported = NativeCoopSaveRecovery.ImportSolo(selected);
            reason = null;
            return imported;
        }
        catch (Exception error)
        {
            Debug.LogWarning("[probably_coop] solo checkpoint import failed: " + error);
            reason = error.Message;
            return null;
        }
    }

    internal static bool VerifyCheckpoint(PlayerStore store, int day)
    {
        return IsRegistered(store) && NativeActorPersistence.VerifyObservedSave(store, day);
    }

    internal static bool CanResume(Checkpoint checkpoint) => ConfirmResume(checkpoint,false,out _);

    internal static bool PrepareResume(Checkpoint checkpoint) => PrepareResume(checkpoint, out _);

    internal static bool PrepareResume(Checkpoint checkpoint, out Checkpoint confirmed)
    {
        using var timing = NativePerformance.MeasureCheckpoint("prepare");
        pendingResume=null;
        if (!ConfirmResume(checkpoint,true,out confirmed)) return false;
        try { if(confirmed.NeedsRecovery) confirmed=NativeCoopSaveRecovery.Recover(confirmed); }
        catch(Exception error)
        { Debug.LogError("[probably_coop] cooperative recovery failed: "+error); return false; }
        Armed=false;
        NewGameData.HardReset();
        var data=NewGameData.Instance;
        data.saveSlot=confirmed.Slot;
        data.saveFile=confirmed.Historical ? confirmed.File : null;
        confirmed.Prepared.Request=data;
        pendingResume=confirmed.Prepared;
        return true;
    }

    internal static PreparedResume TakePreparedResume(NewGameData data)
    {
        var prepared=pendingResume;
        pendingResume=null;
        return ReferenceEquals(prepared?.Request,data) ? prepared : null;
    }

    private static bool ConfirmResume(Checkpoint checkpoint,bool prepare,out Checkpoint current)
    {
        current=null;
        if (checkpoint == null) return false;
        if(checkpoint.PreviewOnly)
            return TryRead(checkpoint.Slot,checkpoint.Run,out current,checkpoint.File,prepare) && current.HasActors &&
                current.Day==checkpoint.Day && current.SavedAt==checkpoint.SavedAt;
        return TryRead(checkpoint.Slot,checkpoint.Run,out current,checkpoint.File,prepare) && current.HasActors &&
            current.Day==checkpoint.Day && current.Nonce==checkpoint.Nonce &&
            checkpoint.ImageDigest!=null && current.ImageDigest.AsSpan().SequenceEqual(checkpoint.ImageDigest);
    }

    internal static HashSet<Guid> RegisteredRunsAtSlot(int slot)
    {
        var runs = new HashSet<Guid>();
        if (slot < 0) return runs;
        foreach (string key in ModHelper.ListModDataFiles(ModId))
            if(TryParseRegistration(key,ModHelper.ReadModDataFileText(ModId,key),out var run,out int registeredSlot) &&
                registeredSlot==slot) runs.Add(run);
        return runs;
    }

    internal static bool TryParseRegistration(string key,string content,out Guid run,out int slot)
    {
        run=Guid.Empty; slot=-1;
        if(key==null || !key.StartsWith(MarkerPrefix,StringComparison.Ordinal) ||
            !key.EndsWith(".txt",StringComparison.Ordinal) || key.Length<41 ||
            !Guid.TryParseExact(key.Substring(MarkerPrefix.Length,32),"N",out run) || run==Guid.Empty ||
            !int.TryParse(content,NumberStyles.None,CultureInfo.InvariantCulture,out slot) || slot<0) return false;
        return key==Key(run) || key==RegistrationKey(run,slot);
    }

    internal static string RegistrationKey(Guid run,int slot) =>
        MarkerPrefix+run.ToString("N")+"_"+slot.ToString(CultureInfo.InvariantCulture)+".txt";

    internal static bool IsRegistered(Guid run,int slot)
    {
        string value=slot.ToString(CultureInfo.InvariantCulture);
        return ModHelper.ReadModDataFileText(ModId,RegistrationKey(run,slot))==value ||
            ModHelper.ReadModDataFileText(ModId,Key(run))==value;
    }

    internal static void WriteRegistration(Guid run,int slot)
    {
        string key=RegistrationKey(run,slot), value=slot.ToString(CultureInfo.InvariantCulture);
        ModHelper.WriteModDataFileText(ModId,key,value);
        if(ModHelper.ReadModDataFileText(ModId,key)!=value) throw new IOException("Cooperative registration was not persisted.");
    }

    internal static bool TryFileSlot(string file,out int slot,out bool history)
    {
        slot=-1; history=false;
        if(string.IsNullOrEmpty(file) || file!=Path.GetFileName(file) || Path.GetExtension(file)!=".es3") return false;
        string stem=Path.GetFileNameWithoutExtension(file);
        try { if(SaveFiles.IsHistoryStem(stem,out slot)) { history=true; return slot>=0; } }
        catch(Exception error) when(error is OverflowException or FormatException) { return false; }
        return stem.StartsWith("save_",StringComparison.Ordinal) &&
            int.TryParse(stem.Substring(5),NumberStyles.None,CultureInfo.InvariantCulture,out slot) &&
            slot>=0 && file==SaveFiles.Slot(slot);
    }

    private static bool ValidFile(int slot,string file) => TryFileSlot(file,out int fileSlot,out _) && fileSlot==slot;

    internal static bool TryRead(int slot, Guid expectedRun, out Checkpoint checkpoint,string file=null,bool prepare=false,
        bool soloOnly=false)
    {
        checkpoint = null;
        if (slot < 0) return false;
        file ??= SaveFiles.Slot(slot);
        if(!ValidFile(slot,file)) return false;
        string path = SaveFiles.AbsolutePath(file);
        try
        {
            if (!File.Exists(path)) return false;
            var result = new Checkpoint { Slot=slot,StoredSlot=-1,Run=Guid.Empty,Day=-1,File=file,
                SavedAt=File.GetLastWriteTime(path),Preview=new SavePreviewData { slotId=slot } };
            var settings = new ES3Settings(path);
            if (settings.location != ES3.Location.File || settings.format != ES3.Format.JSON) return false;
            var bytes = NativeActorPersistence.ReadFileImage(path);
            if (prepare && !soloOnly)
            {
                var receipt=ReadPreparedReceipt(bytes,settings,result);
                if(expectedRun!=Guid.Empty && receipt.Run!=expectedRun) return false;
                result.Run=receipt.Run;
                result.StoredSlot=receipt.Slot;
                result.Day=receipt.Day;
                result.ImageDigest=Digest(bytes);
                result.HasActors=result.HasCoopData=true;
                result.UsesEffectReplication=receipt.UsesEffectReplication;
                var envelope=ValidateReceipt(result,receipt);
                result.Prepared=new PreparedResume(path,result.ImageDigest,envelope);
                checkpoint=FinishPreview(result);
                return true;
            }
            // The solo menu only lists candidates. Conversion still validates
            // the full player/station graph, raw keys and selected file hash.
            if(!soloOnly) NativeCoopSaveRecovery.ValidateRootNames(bytes,settings);
            var entries = soloOnly ? null : new List<KeyValuePair<string, ES3Data>>();
            // Preview only scalar properties; never allocate a second PlayerStore
            // (even an uninitialized one) or run native gameplay hydration.
            using (var reader = ES3Reader.Create(bytes, settings))
                foreach (var entry in NativeActorCheckpoint.ReadNativeEntries(reader))
                {
                    entries?.Add(entry);
                    if (entry.Key != "playerStore" && entry.Key != "storeStation") continue;
                    using var body = ES3Reader.Create(entry.Value.bytes, new ES3Settings(false));
                    foreach (string name in body.Properties)
                    {
                        if (entry.Key == "storeStation" && name == "dayCounter") result.Day = body.Read<int>();
                        else if (entry.Key != "playerStore") body.Skip();
                        else switch (name)
                        {
                            case "saveSlotId": result.StoredSlot = body.Read<int>(); break;
                            case "runID": Guid.TryParse(body.Read<string>(), out result.Run); break;
                            case "modData":
                                var data = body.Read<Dictionary<string, string>>();
                                result.HasCoopData = NativeActorCheckpoint.HasCoopData(data);
                                if(soloOnly && result.HasCoopData) return false;
                                result.HasActors = data?.ContainsKey(NativeActorCheckpoint.ModDataKey) == true;
                                result.UsesEffectReplication = data != null &&
                                    data.TryGetValue(NativeEffectCampaign.SaveKey, out string schema) &&
                                    schema == NativeEffectCampaign.SaveSchema;
                                break;
                            default: if(!ReadSelectionField(body,name,result)) body.Skip(); break;
                        }
                    }
                }
            if(result.StoredSlot<0 || result.Run==Guid.Empty || expectedRun!=Guid.Empty && result.Run!=expectedRun ||
                result.Day<0) return false;
            result.ImageDigest=Digest(bytes);
            if(result.HasActors)
                try
                {
                    var receipt=NativeActorCheckpoint.ReadReceiptEntries(entries);
                    ValidateReceipt(result,receipt);
                }
                catch(Exception error)
                { result.HasActors=false; result.UnavailableReason=error.Message; }
            else result.UnavailableReason="旧原型存档没有完整双角色数据，请新建合作局。";
            checkpoint=FinishPreview(result);
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[probably_coop] slot {slot} checkpoint could not be read: {ex.Message}");
            return false;
        }
    }

    // A selected checkpoint needs both the preview fields and actor receipt.
    // Read both directly from the same ES3 object instead of copying its large
    // player body into another stream and parsing it again.
    internal static NativeActorCheckpoint.Receipt ReadPreparedReceipt(byte[] bytes, ES3Settings settings, Checkpoint result)
    {
        using var reader=(ES3JSONReader)ES3Reader.Create(bytes,settings);
        var receipt=new NativeActorCheckpoint.Receipt();
        var names=new HashSet<string>(StringComparer.Ordinal);
        foreach(string key in reader.Properties)
        {
            if(!names.Add(key)) throw new InvalidDataException("存档包含重复字段："+key);
            ReadPreviewBrace(reader,'{');
            string property=reader.ReadPropertyName();
            string type=null;
            if(property=="__type")
            {
                type=reader.Read<string>();
                property=reader.ReadPropertyName();
            }
            if(property!="value") throw new InvalidDataException("Missing native save value.");

            Type expected=key=="playerStore" ? typeof(PlayerStore) : key=="storeStation" ? typeof(StoreStation) : null;
            if(expected==null) reader.Skip();
            else
            {
                if(string.IsNullOrEmpty(type) || ES3Reflection.GetType(type)!=expected)
                    throw new InvalidDataException("Native save type changed: "+key);
                ReadPreviewBrace(reader,'{');
                if(key=="playerStore")
                    NativeActorCheckpoint.ReadPlayerFields(reader,receipt,readPreview:(body,name) => ReadSelectionField(body,name,result));
                else
                    NativeActorCheckpoint.ReadStationFields(reader,receipt);
                ReadPreviewBrace(reader,'}');
            }
            ReadPreviewBrace(reader,'}');
        }
        if(!names.Contains("playerStore") || !names.Contains("storeStation"))
            throw new InvalidDataException("Missing native checkpoint entries.");
        return receipt;
    }

    private static bool ReadSelectionField(ES3Reader reader, string name, Checkpoint result)
    {
        switch(name)
        {
            case "playerCash": result.Cash=reader.Read<int>(); break;
            case "storeName": result.Name=reader.Read<string>(); break;
            case "startLocale": result.Locale=reader.Read<string>(); break;
            case "startType": result.Preview.startType=reader.Read<NewGameData.StartType>(); break;
            case "startScenarioId": result.Preview.startScenarioId=reader.Read<string>(); break;
            case "version":
            case "saveVersion": result.Preview.version=reader.Read<string>(); break;
            default: return false;
        }
        return true;
    }

    private static NativeActorCheckpoint.Envelope ValidateReceipt(Checkpoint result, NativeActorCheckpoint.Receipt receipt)
    {
        var envelope=NativeActorCheckpoint.DecodeReceipt(receipt,result.Run,result.StoredSlot,result.Day);
        if(envelope.PartnerSetup!=null && !NativeActorPartnerSetup.IsComplete(envelope.PartnerSetup,result.Run))
            throw new InvalidDataException("伙伴角色创建记录未通过校验。");
        result.Nonce=envelope.Checkpoint;
        SetRecovery(result);
        return envelope;
    }

    private static Checkpoint FinishPreview(Checkpoint result)
    {
        if (string.IsNullOrEmpty(result.Name)) result.Name = "Cooperative store";
        result.Preview.credit=result.Cash; result.Preview.dayCount=result.Day;
        result.Preview.lastSaveDateTime=result.SavedAt; result.Preview.startName=result.Name;
        result.Preview.startLocale=result.Locale;
        return result;
    }

    private static void SetRecovery(Checkpoint result)
    {
        result.NeedsRecovery=result.Historical || result.StoredSlot!=result.Slot || !IsRegistered(result.Run,result.Slot);
        result.RecoveryReason=result.Historical
            ? "历史合作检查点将在独立新槽位继续，保留历史档和当前档。"
            : result.StoredSlot!=result.Slot
                ? "文件槽位与内部身份不一致；继续时将创建独立的合作恢复副本，保留原档。"
                : result.NeedsRecovery ? "合作数据完整，但本机缺少合作登记；继续时验证并补登记。" : null;
    }

    internal static string SlotPath(PlayerStore store) => SaveFiles.AbsolutePath(SaveFiles.Slot(store.saveSlotId));

    internal static byte[] Digest(byte[] bytes)
    { using var hash=SHA256.Create(); return hash.ComputeHash(bytes); }

    private static string Key(Guid run) => MarkerPrefix + run.ToString("N") + ".txt";

    private static bool Identity(PlayerStore store, out Guid run)
    {
        run = Guid.Empty;
        return store != null && store.saveSlotId >= 0 && Guid.TryParse(store.runID, out run) && run != Guid.Empty;
    }
}
