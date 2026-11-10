using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using ES3Internal;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ProbablyCoop;

// Persistence codec. No patch, save call, singleton access, item factory, or
// second PlayerStore. The run coordinator supplies the settled
// capture and hydrate its six carried roots BEFORE native feature restoration.
internal static class NativeActorCheckpoint
{
    internal const string ModDataKey = "probably_coop:actor_checkpoint_v1";
    internal const string PartnerSetupDataKey = "probably_coop.partner_setup.v1";
    internal const int SchemaVersion = 2;
    internal const int MaximumPartnerSetupCharacters = 8192;
    internal const int MaximumEnvelopeCharacters = 16 * 1024 * 1024;
    internal const int MaximumSlotBytes = 64 * 1024 * 1024;
    private const int MaximumNodes = 20000;
    private static readonly object FreshWriteAuthority = new object();
    [ThreadStatic] private static Pending activeWrite;
    private static readonly string[] RootKeys = {
        "actor/0/pocket-left", "actor/0/pocket-right", "actor/0/backpack",
        "actor/1/pocket-left", "actor/1/pocket-right", "actor/1/backpack"
    };
    private static readonly HashSet<string> NativeInventoryFields = new HashSet<string>(StringComparer.Ordinal) {
        "mainInvJSON", "backInvJSON", "hiddenInvJSON", "showcaseInvJSON", "soldInvJSON",
        "hirelingInvJSON", "vendingMachineInvJSON", "vendingFountainInvJSON", "trashcanInvJSON",
        "docInvJSON", "cassettePlayerInvJSON"
    };
    private static readonly string[] NativeActorFields = {
        "playerName", "healthData", "startingPerks", "appraiserLevel", "appraisalProgress",
        "appraisalPromotionDeclined", "appraisalPromotionDay", "appraisalBlacklist", "scavengingAttempts",
        "dumpingGroundMajorWoundImmunitySearchesLeft", "dumpingGroundMinorWoundImmunitySearchesLeft"
    };

    internal sealed class Envelope
    {
        internal readonly Guid Run;
        internal readonly int Slot;
        internal readonly int Day;
        internal readonly Guid Checkpoint;
        internal readonly string Encoded;
        private readonly JObject data;
        private int writeTicket;
        internal JObject Personal => (JObject)data["personal"].DeepClone();
        internal JArray Roots => (JArray)data["roots"].DeepClone();
        internal JArray SelectedBackpacks => (JArray)data["selectedBackpacks"].DeepClone();
        internal string PartnerSetup => SetupText(data["partnerSetup"]);
        internal Envelope(Guid run, int slot, int day, Guid checkpoint, JObject value)
        {
            Run = run; Slot = slot; Day = day; Checkpoint = checkpoint;
            data = (JObject)value.DeepClone();
            Encoded = data.ToString(Formatting.None);
            if (Encoded.Length > MaximumEnvelopeCharacters) throw Invalid("envelope size");
        }
        internal void AuthorizeFreshCapture(object authority)
        {
            if (!ReferenceEquals(authority, FreshWriteAuthority) ||
                System.Threading.Interlocked.CompareExchange(ref writeTicket, 1, 0) != 0)
                throw Invalid("only a new capture can issue one write ticket");
        }
        internal bool ClaimFreshWrite() => System.Threading.Interlocked.CompareExchange(ref writeTicket, 2, 1) == 1;
    }

    // A receipt describes bytes from ONE file read. It deliberately does not
    // deserialize PlayerStore or StoreStation. It is not a gameplay checkpoint.
    internal sealed class Receipt
    {
        internal Guid Run;
        internal int Slot;
        internal int Day;
        internal int CurrentUniqueId;
        internal string Encoded;
        internal string PartnerSetup;
        internal JObject NativeHost;
        internal readonly Dictionary<string, string> NativeInventories = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    // roots: exactly six {key, save} rows. save=null for an empty native
    // GameSlotInventory, otherwise the unmodified native SaveState JSON object
    // of its ONE direct GameItem (not a fabricated save-bag wrapper).
    // Native SaveManager.EncodeNodes appends ItemFeatures to the shared native
    // savedItemFeatureList: capture after SaveGame clears it, before WriteSlotFile.
    // This codec never calls EncodeNodes; it cannot certify capture side effects.
    internal static Envelope Create(Guid run, int slot, int day, JObject personal,
        JArray roots, JArray selectedBackpacks, IEnumerable<int> sharedItemIds, string partnerSetup = null)
    {
        var value = new JObject { ["schema"] = SchemaVersion, ["run"] = run.ToString("D"),
            ["slot"] = slot, ["day"] = day, ["checkpoint"] = Guid.NewGuid().ToString("D"),
            ["phase"] = "settled", ["personal"] = personal?.DeepClone(),
            ["roots"] = roots?.DeepClone(), ["selectedBackpacks"] = selectedBackpacks?.DeepClone(),
            ["partnerSetup"] = partnerSetup == null ? JValue.CreateNull() : new JValue(partnerSetup) };
        var envelope = Decode(value.ToString(Formatting.None), sharedItemIds);
        envelope.AuthorizeFreshCapture(FreshWriteAuthority);
        return envelope;
    }

    // Decode validates detached data only. Native item-specific tags, factories,
    // inventory capacities and onLoaded effects require a separate hydration
    // preflight and effect scope; success here never authorizes direct DecodeNodes.
    internal static Envelope Decode(string encoded, IEnumerable<int> sharedItemIds)
    {
        var value = Parse(encoded, MaximumEnvelopeCharacters);
        if (Int(value["schema"]) != SchemaVersion) throw Invalid("unsupported actor checkpoint schema; create a new cooperative run");
        Keys(value, "schema run slot day checkpoint phase personal roots selectedBackpacks partnerSetup");
        if (Text(value["phase"]) != "settled") throw Invalid("phase");
        SetupText(value["partnerSetup"]);
        var run = Identity(value["run"]); var checkpoint = Identity(value["checkpoint"]);
        int slot = Int(value["slot"]), day = Int(value["day"]);
        if (slot < 0 || day < 0) throw Invalid("slot/day");
        var personal = Object(value["personal"]);
        Keys(personal, "schema run actors");
        if (Int(personal["schema"]) != 1 || Identity(personal["run"]) != run ||
            personal["actors"] is not JArray actors || actors.Count != 2) throw Invalid("personal pair");
        NativeActorState.Deserialize(actors[0]); NativeActorState.Deserialize(actors[1]);
        ValidateRoots(value, sharedItemIds, out _);
        return new Envelope(run, slot, day, checkpoint, value);
    }

    // One synchronous write attempt, on the game thread. The caller controls
    // native SaveGame and supplies registration/settled checks; this class never
    // expands authority to a single-player slot. Dispose rolls back ONLY our
    // in-memory modData value if no verified receipt was accepted.
    internal sealed class Pending : IDisposable
    {
        private readonly PlayerStore store;
        private readonly Dictionary<string, string> data;
        private readonly Func<bool> canWrite;
        private readonly int thread = Environment.CurrentManagedThreadId;
        private readonly bool hadPrevious;
        private readonly string previous;
        private bool completed;
        private bool disposed;
        internal Envelope Expected { get; }

        internal Pending(PlayerStore existingStore, int currentDay, Envelope expected, Func<bool> isRegisteredAndSettled)
        {
            if (activeWrite != null) throw new InvalidOperationException("Overlapping native checkpoint writes.");
            store = existingStore ?? throw new ArgumentNullException(nameof(existingStore));
            Expected = expected ?? throw new ArgumentNullException(nameof(expected));
            canWrite = isRegisteredAndSettled ?? throw new ArgumentNullException(nameof(isRegisteredAndSettled));
            CheckIdentity();
            if (currentDay != expected.Day || !canWrite()) throw Invalid("unregistered or unsettled write");
            // Do not silently create modData, or preserve a stale host capture.
            data = store.modData ?? throw Invalid("missing native modData");
            if (!JToken.DeepEquals(expected.Personal["actors"][0], NativeActorState.Capture(store).Serialize()))
                throw Invalid("host/personal capture mismatch");
            if (!string.Equals(ReadPartnerSetup(data), expected.PartnerSetup, StringComparison.Ordinal))
                throw Invalid("partner setup capture mismatch");
            hadPrevious = data.TryGetValue(ModDataKey, out previous);
            if (hadPrevious)
            {
                var prior = Parse(previous, MaximumEnvelopeCharacters);
                if (Identity(prior["run"]) != expected.Run || Int(prior["slot"]) != expected.Slot ||
                    Int(prior["day"]) > expected.Day) throw Invalid("previous checkpoint identity");
                if (Identity(prior["checkpoint"]) == expected.Checkpoint) throw Invalid("checkpoint nonce reused");
            }
            // A restored envelope is read evidence, never a new write ticket.
            // Failed/uncertain writes also consume the ticket: retry with a new
            // capture/nonce, not a second Pending that could accept old bytes.
            if (!expected.ClaimFreshWrite()) throw Invalid("checkpoint write ticket already used or restored");
            data[ModDataKey] = expected.Encoded;
            activeWrite = this;
        }

        internal void VerifyWritten()
        {
            Check();
            var receipt = ReadCanonicalSlot(Expected.Slot);
            VerifyReceipt(Expected, receipt);
            Check();
            completed = true;
        }

        // Verification of an injected receipt is intentionally absent: callers
        // cannot accidentally turn their in-memory snapshot into disk evidence.
        private void Check()
        {
            CheckThread();
            if (disposed || completed) throw new InvalidOperationException("Checkpoint attempt is closed.");
            CheckIdentity();
            if (!canWrite() || !ReferenceEquals(store.modData, data) ||
                !data.TryGetValue(ModDataKey, out var current) || current != Expected.Encoded ||
                !string.Equals(ReadPartnerSetup(data), Expected.PartnerSetup, StringComparison.Ordinal))
                throw Invalid("write context changed");
        }
        private void CheckIdentity()
        {
            if (store.saveSlotId != Expected.Slot || !Guid.TryParse(store.runID, out var run) || run != Expected.Run)
                throw Invalid("native run/slot mismatch");
        }
        private void CheckThread()
        {
            if (thread != Environment.CurrentManagedThreadId) throw new InvalidOperationException("Checkpoint thread mismatch.");
        }
        public void Dispose()
        {
            CheckThread();
            if (disposed) return;
            disposed = true;
            if (ReferenceEquals(activeWrite, this)) activeWrite = null;
            // Never erase a later owner/callback's replacement on a failed save.
            if (!completed && data.TryGetValue(ModDataKey, out var current) && current == Expected.Encoded)
            {
                if (hadPrevious) data[ModDataKey] = previous;
                else data.Remove(ModDataKey);
            }
        }
    }

    internal static void VerifyReceipt(Envelope expected, Receipt receipt)
    {
        if (expected == null || receipt == null || receipt.Run != expected.Run || receipt.Slot != expected.Slot ||
            receipt.Day != expected.Day || !string.Equals(receipt.Encoded, expected.Encoded, StringComparison.Ordinal))
            throw Invalid("native write did not produce this checkpoint");
        DecodeReceipt(receipt, expected.Run, expected.Slot, expected.Day, expected.Checkpoint);
    }

    internal static Envelope DecodeReceipt(Receipt receipt, Guid expectedRun, int expectedSlot,
        int expectedDay, Guid? expectedCheckpoint = null)
    {
        if (receipt == null || expectedRun == Guid.Empty || expectedSlot < 0 || expectedDay < 0 ||
            receipt.Run != expectedRun || receipt.Slot != expectedSlot || receipt.Day != expectedDay)
            throw Invalid("loaded native identity");
        var sharedIds = ReadSharedIds(receipt.NativeInventories);
        var decoded = Decode(receipt.Encoded, sharedIds);
        if (decoded.Run != receipt.Run || decoded.Slot != receipt.Slot || decoded.Day != receipt.Day ||
            expectedCheckpoint.HasValue && decoded.Checkpoint != expectedCheckpoint.Value)
            throw Invalid("loaded world/actor pair");
        if (receipt.NativeHost == null || !JToken.DeepEquals(receipt.NativeHost, decoded.Personal["actors"][0]))
            throw Invalid("loaded native host/personal mismatch");
        if (!string.Equals(receipt.PartnerSetup, decoded.PartnerSetup, StringComparison.Ordinal))
            throw Invalid("loaded partner setup/checkpoint mismatch");
        ValidateRoots(Parse(decoded.Encoded, MaximumEnvelopeCharacters), sharedIds, out var carryIds);
        if (receipt.CurrentUniqueId < 0 || sharedIds.Concat(carryIds).Any(id => id > receipt.CurrentUniqueId))
            throw Invalid("native unique ID generator behind items");
        return decoded;
    }

    // Intended for an already-authorized coop load preflight. The caller MUST
    // resolve which native slot/history file it is loading; this method accepts
    // an existing ES3 reader and never substitutes the latest canonical slot.
    internal static Receipt ReadReceipt(ES3Reader reader, Func<Guid, bool> isRegistered = null)
    {
        if (reader == null) throw new ArgumentNullException(nameof(reader));
        return ReadReceiptEntries(ReadNativeEntries(reader), isRegistered);
    }

    internal static IEnumerable<KeyValuePair<string, ES3Data>> ReadNativeEntries(ES3Reader reader)
    {
        foreach (KeyValuePair<string, ES3Data> entry in new ES3Reader.ES3ReaderRawEnumerator(reader))
            yield return entry;
    }

    internal static Receipt ReadReceiptEntries(IEnumerable<KeyValuePair<string, ES3Data>> entries, Func<Guid, bool> isRegistered = null)
    {
        byte[] player = null, station = null;
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (!keys.Add(entry.Key)) throw Invalid("duplicate native key");
            if (entry.Key == "playerStore")
            {
                if (entry.Value.type?.type != typeof(PlayerStore)) throw Invalid("native player type");
                player = entry.Value.bytes;
            }
            else if (entry.Key == "storeStation")
            {
                if (entry.Value.type?.type != typeof(StoreStation)) throw Invalid("native station type");
                station = entry.Value.bytes;
            }
        }
        if (player == null || player.Length > MaximumSlotBytes)
            throw Invalid("missing/oversized native player entry");
        if (isRegistered != null)
        {
            // A slot can have a stale cooperation marker after native repair.
            // Read just the run identity from this same file image before
            // requiring an actor payload or any of the cooperative fields.
            using var header = ES3Reader.Create(player, new ES3Settings(false));
            Guid run = Guid.Empty;
            foreach (string name in header.Properties)
            {
                if (name != "runID") { header.Skip(); continue; }
                Guid.TryParse(header.Read<string>(), out run);
                break;
            }
            if (run == Guid.Empty || !isRegistered(run)) return null;
        }
        if (station == null || station.Length > MaximumSlotBytes)
            throw Invalid("missing/oversized native entries");
        var receipt = new Receipt();
        var host = NativeActorState.CreateDefault();
        var fields = new HashSet<string>(StringComparer.Ordinal);
        // RawEnumerator already decrypted/decompressed entry bodies. Do not
        // apply default encryption/compression a second time.
        using (var body = ES3Reader.Create(player, new ES3Settings(false)))
            foreach (string name in body.Properties)
            {
                if (!fields.Add(name)) throw Invalid("duplicate native player field");
                switch (name)
                {
                    case "runID":
                        if (!Guid.TryParse(body.Read<string>(), out receipt.Run) || receipt.Run == Guid.Empty) throw Invalid("native run");
                        break;
                    case "saveSlotId": receipt.Slot = body.Read<int>(); break;
                    case "currentUniqueId": receipt.CurrentUniqueId = body.Read<int>(); break;
                    case "playerName": host.playerName = body.Read<string>(); break;
                    case "healthData": host.healthData = body.Read<HealthData>(); break;
                    case "startingPerks": host.startingPerks = body.Read<List<StartingPerk>>(); break;
                    case "appraiserLevel": host.appraiserLevel = body.Read<int>(); break;
                    case "appraisalProgress": host.appraisalProgress = body.Read<double>(); break;
                    case "appraisalPromotionDeclined": host.appraisalPromotionDeclined = body.Read<bool>(); break;
                    case "appraisalPromotionDay": host.appraisalPromotionDay = body.Read<int>(); break;
                    case "appraisalBlacklist": host.appraisalBlacklist = body.Read<List<string>>(); break;
                    case "scavengingAttempts": host.scavengingAttempts = body.Read<int>(); break;
                    case "dumpingGroundMajorWoundImmunitySearchesLeft": host.dumpingGroundMajorWoundImmunitySearchesLeft = body.Read<int>(); break;
                    case "dumpingGroundMinorWoundImmunitySearchesLeft": host.dumpingGroundMinorWoundImmunitySearchesLeft = body.Read<int>(); break;
                    case "modData":
                        var modData = body.Read<Dictionary<string, string>>();
                        if (modData == null || !modData.TryGetValue(ModDataKey, out receipt.Encoded)) throw Invalid("missing coop entry");
                        receipt.PartnerSetup = ReadPartnerSetup(modData);
                        break;
                    default:
                        if (NativeInventoryFields.Contains(name)) receipt.NativeInventories.Add(name, body.Read<string>());
                        else body.Skip();
                        break;
                }
            }
        if (!fields.IsSupersetOf(new[] { "runID", "saveSlotId", "currentUniqueId", "modData" }) ||
            !fields.IsSupersetOf(NativeInventoryFields) || !fields.IsSupersetOf(NativeActorFields)) throw Invalid("incomplete native player entry");
        receipt.NativeHost = host.Serialize();
        fields.Clear();
        using (var body = ES3Reader.Create(station, new ES3Settings(false)))
            foreach (string name in body.Properties)
            {
                if (!fields.Add(name)) throw Invalid("duplicate native station field");
                if (name == "dayCounter") receipt.Day = body.Read<int>();
                else body.Skip();
            }
        if (!fields.Contains("dayCounter") || receipt.Slot < 0 || receipt.Day < 0) throw Invalid("native slot/day");
        return receipt;
    }

    // The setup receipt is historical: later perks may legitimately change.
    // Bind its exact text to this save; the coordinator validates its semantic
    // schema with NativeActorPartnerSetup, never compares it to current perks.
    internal static string ReadPartnerSetup(Dictionary<string, string> data)
    {
        if (data == null) throw Invalid("missing native modData");
        if (!data.TryGetValue(PartnerSetupDataKey, out var value)) return null;
        if (string.IsNullOrEmpty(value) || value.Length > MaximumPartnerSetupCharacters)
            throw Invalid("partner setup receipt size");
        return value;
    }

    private static string SetupText(JToken value)
    {
        if (value?.Type == JTokenType.Null) return null;
        var text = Text(value);
        if (string.IsNullOrEmpty(text) || text.Length > MaximumPartnerSetupCharacters)
            throw Invalid("partner setup receipt size");
        return text;
    }

    private static Receipt ReadCanonicalSlot(int slot)
    {
        if (slot < 0) throw Invalid("slot");
        var settings = new ES3Settings(SaveFiles.Slot(slot));
        if (settings.location != ES3.Location.File || settings.format != ES3.Format.JSON)
            throw Invalid("unsupported native persistence settings");
        byte[] bytes;
        // Read exactly one bounded file image; deny concurrent writes/deletes.
        using (var stream = new FileStream(settings.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (stream.Length <= 0 || stream.Length > MaximumSlotBytes) throw Invalid("native slot size");
            bytes = new byte[(int)stream.Length];
            int offset = 0;
            while (offset < bytes.Length)
            {
                int read = stream.Read(bytes, offset, bytes.Length - offset);
                if (read == 0) throw new EndOfStreamException("Native checkpoint truncated.");
                offset += read;
            }
            if (stream.ReadByte() != -1) throw Invalid("native slot changed size");
        }
        using (var reader = ES3Reader.Create(bytes, settings)) return ReadReceipt(reader);
    }

    private static HashSet<int> ReadSharedIds(IEnumerable<KeyValuePair<string, string>> inventories)
    {
        var ids = new HashSet<int>();
        var fixtures = new HashSet<int>();
        foreach (var entry in inventories)
        {
            string encoded = entry.Value;
            if (string.IsNullOrEmpty(encoded)) continue;
            var state = Parse(encoded, MaximumEnvelopeCharacters);
            if (state["saveItems"] is not JArray nodes || nodes.Count > MaximumNodes) throw Invalid("native inventory");
            for (int index = 0; index < nodes.Count; index++)
            {
                var node = nodes[index];
                int id = Int(node["uniqueId"]);
                // Native synthetic save-bags can have an unset (zero) ID; they
                // are not persistent carried entities and are ignored here.
                if (id < 0)
                {
                    // Native AddAlwayItems assigns these fixed fixture IDs.
                    // They are not carry IDs or values of currentUniqueId.
                    string identifier = Text(node["identifier"]);
                    bool known = entry.Key == "docInvJSON" && index == 0 && id == -101 && identifier == "dossier" ||
                        entry.Key == "trashcanInvJSON" && index == 0 && id == -102 && identifier == "trashcan" ||
                        entry.Key == "cassettePlayerInvJSON" && id == -103 && identifier == "cassette_player";
                    if (!known || !fixtures.Add(id)) throw Invalid("native fixture ID");
                }
                if (id > 0) ids.Add(id);
            }
        }
        return ids;
    }

    private static void ValidateRoots(JObject value, IEnumerable<int> sharedItemIds, out HashSet<int> carryIds)
    {
        if (sharedItemIds == null) throw new ArgumentNullException(nameof(sharedItemIds));
        var shared = new HashSet<int>(sharedItemIds);
        if (shared.Any(id => id <= 0)) throw Invalid("shared item ID");
        carryIds = new HashSet<int>();
        var owners = new Dictionary<int, int>();
        if (value["roots"] is not JArray roots || roots.Count != RootKeys.Length) throw Invalid("six carried roots required");
        var missing = new HashSet<string>(RootKeys, StringComparer.Ordinal);
        int count = 0;
        foreach (var token in roots)
        {
            var root = Object(token); Keys(root, "key save");
            string key = Text(root["key"]);
            if (!missing.Remove(key)) throw Invalid("duplicate/unknown carried root");
            int owner = key[6] - '0';
            if (root["save"].Type == JTokenType.Null) continue;
            var save = Object(root["save"]); Keys(save, "saveItems");
            if (save["saveItems"] is not JArray nodes || nodes.Count < 1 || (count += nodes.Count) > MaximumNodes)
                throw Invalid("carried item count");
            var byUuid = new Dictionary<long, JObject>();
            foreach (var entry in nodes)
            {
                var node = Object(entry);
                long uuid = Long(node["uuid"]);
                int id = Int(node["uniqueId"]);
                if (uuid < 0 || byUuid.ContainsKey(uuid) || id <= 0 || shared.Contains(id) || !carryIds.Add(id))
                    throw Invalid("duplicate/invalid carried entity");
                // Native DirectoryMaster supports null identifiers by restoring
                // a placeholder; preserve that value without running its factory.
                if (node["identifier"] == null || node["identifier"].Type is not (JTokenType.String or JTokenType.Null) || Int(node["unitCount"]) < 1 ||
                    node["childItems"] is not JArray || node["childItemInventoryNode"] is not JArray)
                    throw Invalid("carried node");
                byUuid.Add(uuid, node); owners.Add(id, owner);
            }
            long rootUuid = Long(nodes[0]["uuid"]);
            var children = new HashSet<long>();
            foreach (var node in byUuid.Values)
            {
                var child = (JArray)node["childItems"];
                var placements = (JArray)node["childItemInventoryNode"];
                if (child.Count != placements.Count) throw Invalid("native child placement mismatch");
                for (int i = 0; i < child.Count; i++)
                {
                    long id = Long(child[i]);
                    if (id == rootUuid || !byUuid.ContainsKey(id) || !children.Add(id) || Int(placements[i]) < 0)
                        throw Invalid("carried child ownership");
                }
            }
            // One parent per non-root is insufficient: disconnected cycles are
            // rejected by traversal from the actual native first/root node.
            var reached = new HashSet<long>(); var pending = new Stack<(long Id, int Depth)>(); pending.Push((rootUuid, 1));
            while (pending.Count != 0)
            {
                var next = pending.Pop(); long id = next.Id;
                // Each nested item adds an inventory and an item ownership edge.
                // Match NativeActorInventory's 64-edge confirmed-graph limit.
                if (next.Depth > 32) throw Invalid("excessive carried ownership depth");
                if (!reached.Add(id)) throw Invalid("carried cycle");
                foreach (var child in (JArray)byUuid[id]["childItems"]) pending.Push((Long(child), next.Depth + 1));
            }
            if (reached.Count != byUuid.Count) throw Invalid("unreachable carried item");
        }
        if (value["selectedBackpacks"] is not JArray selected || selected.Count != 2) throw Invalid("selected backpack pair");
        var selections = new HashSet<int>();
        for (int actor = 0; actor < 2; actor++)
        {
            if (selected[actor].Type == JTokenType.Null) continue;
            int id = Int(selected[actor]);
            if (id <= 0 || !selections.Add(id) ||
                !(shared.Contains(id) || owners.TryGetValue(id, out int owner) && owner == actor))
                throw Invalid("selected backpack ownership");
        }
    }

    private static JObject Parse(string value, int maximum)
    {
        if (string.IsNullOrEmpty(value) || value.Length > maximum) throw Invalid("JSON size");
        using var reader = new JsonTextReader(new StringReader(value)) { MaxDepth = 96, DateParseHandling = DateParseHandling.None };
        var result = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
        if (reader.Read()) throw Invalid("trailing JSON");
        return result;
    }
    private static JObject Object(JToken value) => value as JObject ?? throw Invalid("object");
    private static void Keys(JObject value, string keys)
    {
        var required = new HashSet<string>(keys.Split(' '), StringComparer.Ordinal);
        foreach (var property in value.Properties()) if (!required.Remove(property.Name)) throw Invalid("unexpected field");
        if (required.Count != 0) throw Invalid("missing field");
    }
    private static string Text(JToken value) => value?.Type == JTokenType.String ? (string)value : throw Invalid("string");
    private static Guid Identity(JToken value) => Guid.TryParseExact(Text(value), "D", out var id) && id != Guid.Empty ? id : throw Invalid("identity");
    private static int Int(JToken value)
    {
        long result = Long(value);
        if (result < int.MinValue || result > int.MaxValue) throw Invalid("integer range");
        return (int)result;
    }
    private static long Long(JToken value) => value?.Type == JTokenType.Integer &&
        long.TryParse(value.ToString(Formatting.None), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long result)
        ? result : throw Invalid("integer");
    private static InvalidDataException Invalid(string reason) => new InvalidDataException("Invalid cooperative checkpoint: " + reason);
}
