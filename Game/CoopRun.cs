using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using ES3Internal;
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
        internal bool Historical => File != SaveFiles.Slot(Slot);
    }

    internal sealed class PreparedResume
    {
        private readonly string path;
        private readonly byte[] digest;
        private readonly NativeActorCheckpoint.Envelope envelope;
        internal NewGameData Request;

        internal PreparedResume(string filePath, byte[] bytes, NativeActorCheckpoint.Envelope envelope)
        {
            path=Path.GetFullPath(filePath);
            digest=Hash(bytes);
            this.envelope=envelope;
        }

        internal NativeActorCheckpoint.Envelope Match(string filePath, int slot, HashSet<Guid> registered, byte[] bytes)
        {
            if (slot!=envelope.Slot || !registered.Contains(envelope.Run) ||
                !string.Equals(Path.GetFullPath(filePath),path,StringComparison.OrdinalIgnoreCase)) return null;
            return digest.AsSpan().SequenceEqual(Hash(bytes)) ? envelope : null;
        }

        private static byte[] Hash(byte[] bytes)
        {
            using var hash=SHA256.Create();
            return hash.ComputeHash(bytes);
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
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string key in ModHelper.ListModDataFiles(ModId))
        {
            if (!TryParseRegistration(key,ModHelper.ReadModDataFileText(ModId,key),out var run,out int slot) ||
                onlySlot.HasValue && slot!=onlySlot.Value) continue;
            if(TryRead(slot,run,out var checkpoint) && seen.Add(checkpoint.File)) checkpoints.Add(checkpoint);
        }
        // Discovery is deliberately read-only: native import/duplicate can
        // retain a valid two-actor payload while losing its external marker.
        foreach(string path in System.IO.Directory.EnumerateFiles(Application.persistentDataPath,"save_*.es3"))
        {
            string file=Path.GetFileName(path);
            if (!TryFileSlot(file,out int slot,out bool history) || history && !includeHistory ||
                onlySlot.HasValue && slot!=onlySlot.Value || seen.Contains(file)) continue;
            if(TryRead(slot,Guid.Empty,out var checkpoint,file) && checkpoint.HasActors && seen.Add(file))
                checkpoints.Add(checkpoint);
        }
        checkpoints.Sort((a, b) => b.SavedAt.CompareTo(a.SavedAt));
        return checkpoints;
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

    internal static bool PrepareResume(Checkpoint checkpoint)
    {
        pendingResume=null;
        if (!ConfirmResume(checkpoint,true,out var confirmed)) return false;
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
                            case "playerCash": result.Cash = body.Read<int>(); break;
                            case "storeName": result.Name = body.Read<string>(); break;
                            case "startLocale": result.Locale = body.Read<string>(); break;
                            case "startType": result.Preview.startType=body.Read<NewGameData.StartType>(); break;
                            case "startScenarioId": result.Preview.startScenarioId=body.Read<string>(); break;
                            case "version":
                            case "saveVersion": result.Preview.version=body.Read<string>(); break;
                            case "modData":
                                var data = body.Read<Dictionary<string, string>>();
                                result.HasCoopData = NativeActorCheckpoint.HasCoopData(data);
                                if(soloOnly && result.HasCoopData) return false;
                                result.HasActors = data?.ContainsKey(NativeActorCheckpoint.ModDataKey) == true;
                                result.UsesEffectReplication = data != null &&
                                    data.TryGetValue(NativeEffectCampaign.SaveKey, out string schema) &&
                                    schema == NativeEffectCampaign.SaveSchema;
                                break;
                            default: body.Skip(); break;
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
                    var envelope=NativeActorCheckpoint.DecodeReceipt(receipt,result.Run,result.StoredSlot,result.Day);
                    if(envelope.PartnerSetup!=null && !NativeActorPartnerSetup.IsComplete(envelope.PartnerSetup,result.Run))
                        throw new InvalidDataException("伙伴角色创建记录未通过校验。");
                    result.Nonce=envelope.Checkpoint;
                    result.NeedsRecovery=result.Historical || result.StoredSlot!=slot || !IsRegistered(result.Run,slot);
                    result.RecoveryReason=result.Historical
                        ? "历史合作检查点将在独立新槽位继续，保留历史档和当前档。"
                        : result.StoredSlot!=slot
                        ? "文件槽位与内部身份不一致；继续时将创建独立的合作恢复副本，保留原档。"
                        : result.NeedsRecovery ? "合作数据完整，但本机缺少合作登记；继续时验证并补登记。" : null;
                    if(prepare) result.Prepared=new PreparedResume(path,bytes,envelope);
                }
                catch(Exception error)
                { result.HasActors=false; result.UnavailableReason=error.Message; }
            else result.UnavailableReason="旧原型存档没有完整双角色数据，请新建合作局。";
            if (string.IsNullOrEmpty(result.Name)) result.Name = "Cooperative store";
            result.Preview.credit=result.Cash; result.Preview.dayCount=result.Day;
            result.Preview.lastSaveDateTime=result.SavedAt; result.Preview.startName=result.Name;
            result.Preview.startLocale=result.Locale;
            checkpoint = result;
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[probably_coop] slot {slot} checkpoint could not be read: {ex.Message}");
            return false;
        }
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
