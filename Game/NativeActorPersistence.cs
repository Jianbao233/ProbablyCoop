using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ProbablyCoop;

// Only the original native SaveGame writes a slot. Our one capture is inserted
// after its feature-list reset, before inventory encoding and before its IO
// retry/catch block. A successful native return alone is not a save receipt.
internal static class NativeActorPersistence
{
    private static readonly NativeActorInventory.CarrySlot[] Places = {
        NativeActorInventory.CarrySlot.LeftPocket, NativeActorInventory.CarrySlot.RightPocket,
        NativeActorInventory.CarrySlot.Backpack
    };
    private static readonly string[] Names = { "pocket-left", "pocket-right", "backpack" };
    [ThreadStatic] private static SaveAttempt saving;
    [ThreadStatic] private static LoadAttempt loading;
    [ThreadStatic] private static DecodeAttempt decoding;

    private sealed class SaveAttempt : IDisposable
    {
        internal readonly NativeActorSession Session;
        internal NativeActorCheckpoint.Pending Pending;
        internal bool Verified;
        internal SaveAttempt(NativeActorSession session) { Session = session; }
        public void Dispose() { Pending?.Dispose(); if (ReferenceEquals(saving, this)) saving = null; }
    }
    private sealed class LoadAttempt
    {
        internal PlayerStore Store;
        internal NativeActorCheckpoint.Envelope Envelope;
        internal bool Hydrated;
    }
    private sealed class DecodeAttempt { internal GameItem Root; }

    [HarmonyPatch(typeof(PlayerStore), nameof(PlayerStore.SaveGame))]
    private static class SaveBoundary
    {
        [HarmonyPriority(Priority.First)]
        private static void Prefix(PlayerStore __instance, out SaveAttempt __state)
        {
            __state = null;
            if (NativeGuest.Active || !(CoopRun.IsCreating(__instance) || CoopRun.IsRegistered(__instance))) return;
            if (saving != null) throw new InvalidOperationException("Nested cooperative native save.");
            var session = CoopRun.IsCreating(__instance) ? NativeActorSession.EnsureCreating(__instance) : NativeActorSession.EnsureForExisting(__instance);
            if (!session.CanCommit) throw new InvalidOperationException("Cannot save while cooperative actions, holds, hydration or night are unsettled.");
            __state = saving = new SaveAttempt(session);
        }
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> source) => InsertCapture(source);
        [HarmonyPriority(Priority.First)]
        private static void Postfix(PlayerStore __instance, SaveAttempt __state)
        {
            if (__state != null && !VerifyObservedSave(__instance, StoreStation.Instance.dayCounter))
                throw new IOException("Native save returned without a matching cooperative checkpoint.");
        }
        private static Exception Finalizer(SaveAttempt __state, Exception __exception)
        {
            if (__state == null) return __exception;
            if (__exception != null) __state.Session.MarkFault(__exception);
            __state.Dispose();
            return __exception;
        }
    }

    internal static IEnumerable<CodeInstruction> InsertCapture(IEnumerable<CodeInstruction> source)
    {
        var code = source.ToList();
        var featureField = AccessTools.Field(typeof(PlayerStore), nameof(PlayerStore.savedItemFeatureList));
        var locations = code.Select((value, index) => (value, index)).Where(pair =>
            pair.value.opcode == OpCodes.Stfld && Equals(pair.value.operand, featureField)).ToArray();
        if (locations.Length != 1) throw new InvalidOperationException("Native save feature reset boundary changed.");
        code.InsertRange(locations[0].index + 1, new[] {
            new CodeInstruction(OpCodes.Ldarg_0),
            new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(NativeActorPersistence), nameof(CaptureAfterFeatureReset)))
        });
        return code;
    }

    private static void CaptureAfterFeatureReset(PlayerStore store)
    {
        var attempt = saving;
        if (attempt == null) return;
        if (!ReferenceEquals(attempt.Session.Store, store) || attempt.Pending != null || !attempt.Session.CanCommit)
            throw new InvalidOperationException("Cooperative save capture was repeated or is no longer settled.");
        var session = attempt.Session;
        var carried = new HashSet<GameItem>(session.Carry.GetAllCarriedItems());
        var shared = session.Carry.Source.GetAllItems().Where(item => item != null && !carried.Contains(item) && item.uniqueId > 0)
            .Select(item => item.uniqueId).ToArray();
        var roots = new JArray();
        var selected = new JArray();
        for (int actor = 0; actor < 2; actor++)
        {
            var slot = (NativeActorRun.Slot)actor;
            for (int place = 0; place < Places.Length; place++)
            {
                var inventory = session.Carry.Get(slot, Places[place]);
                var item = inventory.childItem;
                if (item != null && !ReferenceEquals(item.parentInventory, inventory))
                    throw new InvalidOperationException("Carried root is not owned by its native slot.");
                JToken save = item == null ? JValue.CreateNull() : JObject.Parse(store.EncodeItem(item));
                roots.Add(new JObject { ["key"] = "actor/" + actor + "/" + Names[place], ["save"] = save });
            }
            var backpack = session.Carry.Selected(slot);
            selected.Add(backpack == null ? JValue.CreateNull() : new JValue(backpack.uniqueId));
        }
        var partnerSetup = NativeActorCheckpoint.ReadPartnerSetup(store.modData);
        ValidatePartnerSetup(partnerSetup, session.Run.Run);
        var value = NativeActorCheckpoint.Create(session.Run.Run, store.saveSlotId, StoreStation.Instance.dayCounter,
            session.Run.CapturePersonalCheckpoint(), roots, selected, shared, partnerSetup);
        attempt.Pending = new NativeActorCheckpoint.Pending(store, value.Day, value, () =>
            ReferenceEquals(NativeActorSession.Current, session) && session.CanCommit &&
            (CoopRun.IsCreating(store) || CoopRun.IsRegistered(store)) && StoreStation.Instance.dayCounter == value.Day);
    }

    // Called by both the native SaveGame postfix and the existing night receipt
    // gate. The pending write is read back exactly once regardless of patch order.
    internal static bool VerifyObservedSave(PlayerStore store, int day)
    {
        var session = NativeActorSession.Current;
        if (session == null || !ReferenceEquals(session.Store, store) || session.Faulted || session.Terminal) return false;
        var attempt = saving;
        if (attempt != null && ReferenceEquals(attempt.Session, session))
        {
            if (attempt.Pending == null) return false;
            if (!attempt.Verified)
            {
                attempt.Pending.VerifyWritten();
                session.SetVerified(attempt.Pending.Expected);
                attempt.Verified = true;
            }
        }
        return session.VerifiedCheckpoint != null && session.VerifiedCheckpoint.Day == day &&
            session.VerifiedCheckpoint.Slot == store.saveSlotId && session.VerifiedCheckpoint.Run == session.Run.Run;
    }

    [HarmonyPatch(typeof(PlayerStore), nameof(PlayerStore.LoadGame))]
    private static class LoadBoundary
    {
        [HarmonyPriority(Priority.First)]
        private static void Prefix(PlayerStore __instance, out LoadAttempt __state)
        {
            __state = null;
            var data=NewGameData.Instance;
            var prepared=CoopRun.TakePreparedResume(data);
            if (NativeGuest.Active || data == null) return;
            int slot = data.saveSlot;
            var registered = CoopRun.RegisteredRunsAtSlot(slot);
            if (registered.Count == 0) return;
            if (loading != null) throw new InvalidOperationException("Nested cooperative native load.");
            var value = ReadLoadEnvelope(SaveFiles.ResolveLoadFile(data), slot, registered, prepared);
            if (value == null) return; // Rev12 repair or rediscovery may reuse the numeric slot.
            ValidatePartnerSetup(value.PartnerSetup, value.Run);
            NativeActorSession.Current?.Dispose();
            __state = loading = new LoadAttempt { Store = __instance, Envelope = value };
        }
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> source) => ReplaceLoadEnumeration(source);
        private static void Postfix(LoadAttempt __state)
        {
            if (__state != null && !__state.Hydrated) throw new InvalidOperationException("Native load missed actor hydration.");
        }
        private static Exception Finalizer(LoadAttempt __state, Exception __exception)
        {
            if (__state == null) return __exception;
            if (__exception != null) NativeActorSession.Current?.MarkFault(__exception);
            if (ReferenceEquals(loading, __state)) loading = null;
            return __exception;
        }
    }

    internal static IEnumerable<CodeInstruction> ReplaceLoadEnumeration(IEnumerable<CodeInstruction> source) => ReplaceOne(source,
        AccessTools.Method(typeof(EmporiumEntry), nameof(EmporiumEntry.GetAllItems)),
        AccessTools.Method(typeof(NativeActorPersistence), nameof(HydrateAndGetItems)), "native feature restoration");

    private static List<GameItem> HydrateAndGetItems(EmporiumEntry entry)
    {
        var attempt = loading;
        if (attempt == null) return entry.GetAllItems();
        if (attempt.Hydrated || attempt.Envelope.Day != StoreStation.Instance.dayCounter ||
            !attempt.Store.modData.TryGetValue(NativeActorCheckpoint.ModDataKey, out var encoded) || encoded != attempt.Envelope.Encoded ||
            !string.Equals(NativeActorCheckpoint.ReadPartnerSetup(attempt.Store.modData), attempt.Envelope.PartnerSetup, StringComparison.Ordinal))
            throw new InvalidOperationException("Native world and actor checkpoint changed during hydration.");
        var session = NativeActorSession.Restore(attempt.Store, attempt.Envelope);
        try
        {
            var roots = attempt.Envelope.Roots.ToDictionary(row => (string)row["key"], row => row["save"], StringComparer.Ordinal);
            for (int actor = 0; actor < 2; actor++)
                using (session.Enter((NativeActorRun.Slot)actor))
                    for (int place = 0; place < Places.Length; place++)
                    {
                        var target = session.Carry.Get((NativeActorRun.Slot)actor, Places[place]);
                        if (target.childItem != null) throw new InvalidOperationException("Hydration cannot overwrite occupied native carry.");
                        var saved = roots["actor/" + actor + "/" + Names[place]];
                        if (saved.Type == JTokenType.Null) continue;
                        var root = DecodeCarry(attempt.Store, saved.ToString(Newtonsoft.Json.Formatting.None));
                        target.UncheckedAccept(root);
                        if (!ReferenceEquals(target.childItem, root) || !ReferenceEquals(root.parentInventory, target))
                            throw new InvalidOperationException("Native carry rejected a hydrated root.");
                    }
            var all = entry.GetAllItems().Concat(session.Carry.GetAllCarriedItems()).Where(item => item != null).Distinct().ToList();
            var byId = new Dictionary<int, GameItem>();
            foreach (var item in all)
                if (item.uniqueId > 0 && !byId.TryAdd(item.uniqueId, item)) throw new InvalidOperationException("Duplicate hydrated native item identity.");
            var selected = attempt.Envelope.SelectedBackpacks;
            for (int actor = 0; actor < 2; actor++)
            {
                GameItem item = null;
                if (selected[actor].Type != JTokenType.Null && !byId.TryGetValue((int)selected[actor], out item))
                    throw new InvalidOperationException("Selected backpack is missing from the restored native graph.");
                session.Carry.SetSelected((NativeActorRun.Slot)actor, item);
            }
            attempt.Hydrated = true;
            session.Hydrating = false;
            session.SetVerified(attempt.Envelope);
            return all; // Vanilla now restores the ONE shared ItemFeature list across all roots.
        }
        catch (Exception error) { session.MarkFault(error); throw; }
    }

    private static void ValidatePartnerSetup(string receipt, Guid run)
    {
        // Missing means the fixed Partner is still awaiting their first choice.
        // A present receipt must be valid even if current personal perks have
        // subsequently changed through normal gameplay.
        if (receipt != null && !NativeActorPartnerSetup.IsComplete(receipt, run))
            throw new InvalidDataException("Partner setup receipt is incomplete.");
    }

    // Reuse the original complete onLoaded pipeline. Its null-inventory return
    // occurs before save-bag transfer/destruction. Within this narrow scope the
    // enumeration includes the carried root too, since it is not a save bag.
    private static GameItem DecodeCarry(PlayerStore store, string json)
    {
        if (decoding != null) throw new InvalidOperationException("Nested carried root hydration.");
        var attempt = decoding = new DecodeAttempt();
        try
        {
            store.DecodeSaveItem(json, null);
            return attempt.Root ?? throw new InvalidOperationException("Native carry decoding did not produce a root.");
        }
        finally { decoding = null; }
    }
    [HarmonyPatch(typeof(PlayerStore), nameof(PlayerStore.DecodeSaveItem))]
    private static class DecodeBoundary
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> source)
        {
            var result = ReplaceOne(source, AccessTools.Method(typeof(SaveManager), nameof(SaveManager.DecodeNodes)),
                AccessTools.Method(typeof(NativeActorPersistence), nameof(DecodeNodes)), "native root decoder");
            var enumerate = typeof(GraphUtils).GetMethods().Single(method => method.Name == nameof(GraphUtils.FindAllChildrenType) && method.IsGenericMethodDefinition)
                .MakeGenericMethod(typeof(GameItem));
            return ReplaceOne(result, enumerate, AccessTools.Method(typeof(NativeActorPersistence), nameof(DecodedChildren)), "native load callbacks");
        }
    }
    private static GameItem DecodeNodes(List<SaveItemNode> nodes)
    {
        var item = SaveManager.DecodeNodes(nodes);
        if (decoding != null && decoding.Root == null) decoding.Root = item;
        return item;
    }
    private static List<GameItem> DecodedChildren(GraphNodeStorage node, Func<GameItem, bool> filter, Func<GameItem, bool> passthrough)
    {
        var items = GraphUtils.FindAllChildrenType(node, filter, passthrough);
        if (decoding != null && ReferenceEquals(decoding.Root, node) && node is GameItem root)
            items.Insert(0, root);
        return items;
    }

    internal static IEnumerable<CodeInstruction> ReplaceOne(IEnumerable<CodeInstruction> source, MethodInfo original, MethodInfo replacement, string boundary)
    {
        var code = source.ToList();
        var matches = code.Where(value => (value.opcode == OpCodes.Call || value.opcode == OpCodes.Callvirt) && Equals(value.operand, original)).ToArray();
        if (matches.Length != 1) throw new InvalidOperationException(boundary + " changed; expected one call.");
        int index = code.IndexOf(matches[0]);
        code[index] = new CodeInstruction(code[index]) { opcode = OpCodes.Call, operand = replacement };
        return code;
    }

    private static NativeActorCheckpoint.Envelope ReadLoadEnvelope(string file,int slot,HashSet<Guid> registered,
        CoopRun.PreparedResume prepared)
    {
        var settings = new ES3Settings(file);
        if (settings.location != ES3.Location.File || settings.format != ES3.Format.JSON)
            throw new InvalidDataException("Unsupported native checkpoint settings.");
        var bytes=ReadFileImage(settings.FullPath);
        // The confirmed menu selection can save a parse only for the exact
        // same file image. Direct loads and replacements run full validation.
        var verified=prepared?.Match(settings.FullPath,slot,registered,bytes);
        if(verified!=null)
        {
            if(NativeBackgroundQa.Enabled && NativePerformance.Profiling)
                Debug.Log("[probably_coop QA] PASS prepared-load-image confirmed checkpoint matches the native file bytes");
            return verified;
        }
        using var reader = ES3Reader.Create(bytes, settings);
        var receipt=NativeActorCheckpoint.ReadReceipt(reader,registered.Contains);
        return receipt==null ? null : NativeActorCheckpoint.DecodeReceipt(receipt,receipt.Run,slot,receipt.Day);
    }
    internal static byte[] ReadFileImage(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length <= 0 || stream.Length > NativeActorCheckpoint.MaximumSlotBytes) throw new InvalidDataException("Native checkpoint size is invalid.");
        var bytes = new byte[(int)stream.Length];
        int offset = 0;
        while (offset < bytes.Length)
        {
            int count = stream.Read(bytes, offset, bytes.Length - offset);
            if (count == 0) throw new EndOfStreamException("Native checkpoint truncated.");
            offset += count;
        }
        return bytes;
    }
}
