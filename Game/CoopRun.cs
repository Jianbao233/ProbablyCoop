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
        internal Guid Run;
        internal int Day;
        internal int Cash;
        internal string Name;
        internal string Locale;
        internal DateTime SavedAt;
        internal bool HasActors;
        internal Guid Nonce;
        internal string File;
        internal string UnavailableReason;
        internal PreparedResume Prepared;
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
                ModHelper.WriteModDataFileText(ModId, Key(run), slot.ToString(CultureInfo.InvariantCulture));
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
            return ModHelper.ReadModDataFileText(ModId, Key(run)) ==
                store.saveSlotId.ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    internal static List<Checkpoint> ListCheckpoints(bool includeHistory = false, int? onlySlot = null)
    {
        var checkpoints = new List<Checkpoint>();
        foreach (string key in ModHelper.ListModDataFiles(ModId))
        {
            if (key.Length != 41 || !key.StartsWith(MarkerPrefix, StringComparison.Ordinal) ||
                !key.EndsWith(".txt", StringComparison.Ordinal) ||
                !Guid.TryParseExact(key.Substring(MarkerPrefix.Length, 32), "N", out var run))
                continue;
            string slotText = ModHelper.ReadModDataFileText(ModId, key);
            if (!int.TryParse(slotText, NumberStyles.None, CultureInfo.InvariantCulture, out int slot) || slot < 0 ||
                onlySlot.HasValue && slot!=onlySlot.Value) continue;
            if(TryRead(slot, run, out var checkpoint)) checkpoints.Add(checkpoint);
            if(includeHistory)
                foreach(string path in System.IO.Directory.EnumerateFiles(Application.persistentDataPath,"save_"+slot+"_day*.es3"))
                {
                    string file=Path.GetFileName(path);
                    if(ValidFile(slot,file) && TryRead(slot,run,out checkpoint,file) && checkpoint.HasActors)
                        checkpoints.Add(checkpoint);
                }
        }
        checkpoints.Sort((a, b) => b.SavedAt.CompareTo(a.SavedAt));
        return checkpoints;
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
        string slotText = ModHelper.ReadModDataFileText(ModId, Key(checkpoint.Run));
        return slotText == checkpoint.Slot.ToString(CultureInfo.InvariantCulture) &&
            TryRead(checkpoint.Slot, checkpoint.Run, out current,checkpoint.File,prepare) && current.HasActors &&
            current.Day==checkpoint.Day && current.Nonce==checkpoint.Nonce;
    }

    internal static HashSet<Guid> RegisteredRunsAtSlot(int slot)
    {
        var runs = new HashSet<Guid>();
        if (slot < 0) return runs;
        foreach (string key in ModHelper.ListModDataFiles(ModId))
            if (key.Length == 41 && key.StartsWith(MarkerPrefix, StringComparison.Ordinal) && key.EndsWith(".txt", StringComparison.Ordinal) &&
                Guid.TryParseExact(key.Substring(MarkerPrefix.Length, 32), "N", out var run) && run != Guid.Empty &&
                ModHelper.ReadModDataFileText(ModId, key) == slot.ToString(CultureInfo.InvariantCulture)) runs.Add(run);
        return runs;
    }

    private static bool ValidFile(int slot,string file) => file==SaveFiles.Slot(slot) ||
        !string.IsNullOrEmpty(file) && file==Path.GetFileName(file) &&
        Path.GetExtension(file)==".es3" && SaveFiles.IsHistoryStem(Path.GetFileNameWithoutExtension(file),out int historySlot) && historySlot==slot;

    private static bool TryRead(int slot, Guid expectedRun, out Checkpoint checkpoint,string file=null,bool prepare=false)
    {
        checkpoint = null;
        if (slot < 0 || expectedRun == Guid.Empty) return false;
        file ??= SaveFiles.Slot(slot);
        if(!ValidFile(slot,file)) return false;
        string path = SaveFiles.AbsolutePath(file);
        try
        {
            if (!File.Exists(path)) return false;
            var result = new Checkpoint { Slot = -1, Run = Guid.Empty, Day = -1, File=file, SavedAt = File.GetLastWriteTime(path) };
            var settings = new ES3Settings(path);
            if (settings.location != ES3.Location.File || settings.format != ES3.Format.JSON) return false;
            var bytes = NativeActorPersistence.ReadFileImage(path);
            var entries = new List<KeyValuePair<string, ES3Data>>();
            // Preview only scalar properties; never allocate a second PlayerStore
            // (even an uninitialized one) or run native gameplay hydration.
            using (var reader = ES3Reader.Create(bytes, settings))
                foreach (var entry in NativeActorCheckpoint.ReadNativeEntries(reader))
                {
                    entries.Add(entry);
                    if (entry.Key != "playerStore" && entry.Key != "storeStation") continue;
                    using var body = ES3Reader.Create(entry.Value.bytes, new ES3Settings(false));
                    foreach (string name in body.Properties)
                    {
                        if (entry.Key == "storeStation" && name == "dayCounter") result.Day = body.Read<int>();
                        else if (entry.Key != "playerStore") body.Skip();
                        else switch (name)
                        {
                            case "saveSlotId": result.Slot = body.Read<int>(); break;
                            case "runID": Guid.TryParse(body.Read<string>(), out result.Run); break;
                            case "playerCash": result.Cash = body.Read<int>(); break;
                            case "storeName": result.Name = body.Read<string>(); break;
                            case "startLocale": result.Locale = body.Read<string>(); break;
                            case "modData":
                                var data = body.Read<Dictionary<string, string>>();
                                result.HasActors = data?.ContainsKey(NativeActorCheckpoint.ModDataKey) == true;
                                break;
                            default: body.Skip(); break;
                        }
                    }
                }
            if (result.Slot != slot || result.Run != expectedRun || result.Day < 0) return false;
            if(result.HasActors)
                try
                {
                    var receipt=NativeActorCheckpoint.ReadReceiptEntries(entries);
                    var envelope=NativeActorCheckpoint.DecodeReceipt(receipt,expectedRun,slot,result.Day);
                    if(envelope.PartnerSetup!=null && !NativeActorPartnerSetup.IsComplete(envelope.PartnerSetup,expectedRun))
                        throw new InvalidDataException("伙伴角色创建记录未通过校验。");
                    result.Nonce=envelope.Checkpoint;
                    if(prepare) result.Prepared=new PreparedResume(path,bytes,envelope);
                }
                catch(Exception error)
                { result.HasActors=false; result.UnavailableReason=error.Message; }
            else result.UnavailableReason="旧原型存档没有完整双角色数据，请新建合作局。";
            if (string.IsNullOrEmpty(result.Name)) result.Name = "Cooperative store";
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

    private static string Key(Guid run) => MarkerPrefix + run.ToString("N") + ".txt";

    private static bool Identity(PlayerStore store, out Guid run)
    {
        run = Guid.Empty;
        return store != null && store.saveSlotId >= 0 && Guid.TryParse(store.runID, out run) && run != Guid.Empty;
    }
}
