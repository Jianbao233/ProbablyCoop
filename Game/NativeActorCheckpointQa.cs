using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

namespace ProbablyCoop;

// Explicit, memory-only ES3 smoke inside the isolated Unity host. No save slot,
// PlayerStore construction, game state mutation, or automatic patch/activation.
internal static class NativeActorCheckpointQa
{
    internal static string[] Run()
    {
        if (!NativeBackgroundQa.Enabled || !NativeBackgroundQa.IsHostProduct ||
            Array.IndexOf(Environment.GetCommandLineArgs(), "--probably-coop-qa-actor-fields") < 0)
            throw new InvalidOperationException("Actor checkpoint QA requires the isolated actor-fields host.");
        var evidence = new List<string>();
        Guid run = Guid.NewGuid();
        var host = NativeActorState.CreateDefault(); host.playerName = "checkpoint-fixture-host";
        var partner = NativeActorState.CreateDefault(); partner.playerName = "checkpoint-fixture-partner";
        var personal = new JObject { ["schema"] = 1, ["run"] = run.ToString("D"),
            ["actors"] = new JArray(host.Serialize(), partner.Serialize()) };
        var roots = new JArray();
        foreach (int actor in new[] { 0, 1 })
            foreach (string slot in new[] { "pocket-left", "pocket-right", "backpack" })
                roots.Add(new JObject { ["key"] = "actor/" + actor + "/" + slot, ["save"] = null });
        var envelope = NativeActorCheckpoint.Create(run, 777, 9, personal, roots, new JArray(null, null), Array.Empty<int>());
        JObject Fixture()
        {
            var native = host.Serialize(); native.Remove("schema");
            native["runID"] = run.ToString("D"); native["saveSlotId"] = 777; native["currentUniqueId"] = 100;
            native["modData"] = new JObject { [NativeActorCheckpoint.ModDataKey] = envelope.Encoded };
            if (envelope.PartnerSetup != null)
                native["modData"][NativeActorCheckpoint.PartnerSetupDataKey] = envelope.PartnerSetup;
            foreach (string field in "mainInvJSON backInvJSON hiddenInvJSON showcaseInvJSON soldInvJSON hirelingInvJSON vendingMachineInvJSON vendingFountainInvJSON trashcanInvJSON docInvJSON cassettePlayerInvJSON".Split(' '))
                native[field] = null;
            return new JObject {
                ["playerStore"] = new JObject { ["__type"] = typeof(PlayerStore).AssemblyQualifiedName, ["value"] = native },
                ["storeStation"] = new JObject { ["__type"] = typeof(StoreStation).AssemblyQualifiedName,
                    ["value"] = new JObject { ["dayCounter"] = 9 } }
            };
        }
        NativeActorCheckpoint.Receipt Read(JObject fixture)
        {
            using var reader = ES3Reader.Create(Encoding.UTF8.GetBytes(fixture.ToString()), new ES3Settings(false));
            return NativeActorCheckpoint.ReadReceipt(reader);
        }
        void Reject(string name, Action action)
        {
            try { action(); }
            catch (InvalidDataException) { evidence.Add("PASS " + name); return; }
            throw new InvalidOperationException("Checkpoint QA accepted " + name);
        }
        NativeActorCheckpoint.VerifyReceipt(envelope, Read(Fixture()));
        evidence.Add("PASS real ES3 memory read pairs personal actors with native run/slot/day/nonce");
        if (NativeActorCheckpoint.PartnerSetupDataKey != NativeActorPartnerSetup.SaveKey)
            throw new InvalidOperationException("Partner setup persistence key drift.");
        string proof;
        using (var hash = SHA256.Create()) proof = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes("[]"))).Replace("-", "");
        var setup = new JObject { ["schema"] = 1, ["run"] = run.ToString("D"), ["slot"] = 1,
            ["ids"] = new JArray(), ["selection"] = proof, ["completed"] = true }.ToString(Newtonsoft.Json.Formatting.None);
        if (!NativeActorPartnerSetup.IsComplete(setup, run)) throw new InvalidOperationException("Fixture setup receipt is not valid.");
        envelope = NativeActorCheckpoint.Create(run, 777, 9, personal, roots, new JArray(null, null), Array.Empty<int>(), setup);
        NativeActorCheckpoint.VerifyReceipt(envelope, Read(Fixture()));
        evidence.Add("PASS real ES3 same-file actor and partner setup receipt pairing");
        var missingSetup = Fixture();
        ((JObject)missingSetup["playerStore"]["value"]["modData"]).Remove(NativeActorCheckpoint.PartnerSetupDataKey);
        Reject("missing partner setup with valid actor checkpoint", () => NativeActorCheckpoint.VerifyReceipt(envelope, Read(missingSetup)));
        var changedSetup = Fixture();
        changedSetup["playerStore"]["value"]["modData"][NativeActorCheckpoint.PartnerSetupDataKey] = "old receipt";
        Reject("substituted partner setup with valid actor checkpoint", () => NativeActorCheckpoint.VerifyReceipt(envelope, Read(changedSetup)));
        Reject("setup semantic cross-run mismatch", () => NativeActorPartnerSetup.IsComplete(setup, Guid.NewGuid()));
        var invalidSetup = JObject.Parse(setup); invalidSetup["selection"] = "bad proof";
        Reject("setup semantic invalid completion proof", () => NativeActorPartnerSetup.IsComplete(invalidSetup.ToString(), run));
        var fixture = Fixture();
        foreach (var row in new[] { ("docInvJSON", -101, "dossier"), ("trashcanInvJSON", -102, "trashcan"),
            ("cassettePlayerInvJSON", -103, "cassette_player") })
            fixture["playerStore"]["value"][row.Item1] = new JObject { ["saveItems"] = new JArray(
                new JObject { ["uniqueId"] = row.Item2, ["identifier"] = row.Item3 }) }.ToString();
        NativeActorCheckpoint.VerifyReceipt(envelope, Read(fixture));
        evidence.Add("PASS native dossier/trashcan/cassette fixed negative IDs");
        fixture = Fixture();
        var old = NativeActorCheckpoint.Create(run, 777, 9, personal, roots, new JArray(null, null), Array.Empty<int>(), setup);
        fixture["playerStore"]["value"]["modData"][NativeActorCheckpoint.ModDataKey] = old.Encoded;
        Reject("same-day stale nonce", () => NativeActorCheckpoint.VerifyReceipt(envelope, Read(fixture)));
        fixture = Fixture(); fixture["playerStore"]["value"]["runID"] = Guid.NewGuid().ToString("D");
        Reject("cross-run native entry", () => NativeActorCheckpoint.VerifyReceipt(envelope, Read(fixture)));
        fixture = Fixture(); fixture["playerStore"]["value"]["saveSlotId"] = 778;
        Reject("cross-slot native entry", () => NativeActorCheckpoint.VerifyReceipt(envelope, Read(fixture)));
        fixture = Fixture(); fixture["storeStation"]["value"]["dayCounter"] = 8;
        Reject("cross-day native station", () => NativeActorCheckpoint.VerifyReceipt(envelope, Read(fixture)));
        fixture = Fixture(); fixture["playerStore"]["value"]["playerName"] = "wrong-host";
        Reject("native host/personal mismatch", () => NativeActorCheckpoint.VerifyReceipt(envelope, Read(fixture)));
        var item = new JObject { ["uniqueId"] = 10, ["uuid"] = 0, ["identifier"] = "checkpoint-fixture",
            ["unitCount"] = 1, ["childItems"] = new JArray(), ["childItemInventoryNode"] = new JArray() };
        var save = new JObject { ["saveItems"] = new JArray(item) };
        roots[0]["save"] = save.DeepClone(); roots[3]["save"] = save.DeepClone();
        Reject("duplicate carry identity across actors", () => NativeActorCheckpoint.Create(run, 777, 9, personal,
            roots, new JArray(null, null), Array.Empty<int>()));
        roots[3]["save"] = null;
        var carried = NativeActorCheckpoint.Create(run, 777, 9, personal, roots, new JArray(null, null), Array.Empty<int>(), setup);
        fixture = Fixture(); fixture["playerStore"]["value"]["modData"][NativeActorCheckpoint.ModDataKey] = carried.Encoded;
        fixture["playerStore"]["value"]["mainInvJSON"] = save.ToString();
        Reject("real ES3 readback shared/carry overlap", () => NativeActorCheckpoint.VerifyReceipt(carried, Read(fixture)));
        evidence.Add("PASS memory-only fixture; no native slot access or PlayerStore construction");
        return evidence.ToArray();
    }
}
