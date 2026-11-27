using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace ProbablyCoop;

internal static class NativeRoomModManifest
{
    internal static Dictionary<string, RoomModManifest.Entry> Local()
    {
        var mods = ModLoader.Mods
        .Where(mod => mod?.Manifest != null && mod.Manifest.ID != RoomModManifest.OwnId &&
            ModExtensionContract.IsToken(mod.Manifest.ID))
        .GroupBy(mod => mod.Manifest.ID, StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group =>
        {
            var mod = group.First();
            string name = RoomModManifest.Display(mod.Manifest.Name, 96);
            string digest = RoomModManifest.CanonicalDigest(mod.Fingerprint?.Combined);
            if (digest.Length != 0) digest += RoomModManifest.CanonicalDigest(mod.Fingerprint?.FolderHash);
            bool failed = !string.IsNullOrEmpty(mod.LoadError);
            return new RoomModManifest.Entry(group.Key, name.Length == 0 ? group.Key : name,
                RoomModManifest.Display(mod.Manifest.ModVersion, 48), mod.WorkshopId,
                mod.IsEnabled && !failed, digest, loadFailed: failed, shadowedWorkshop: mod.ShadowedWorkshopId);
        }, StringComparer.Ordinal);
#if PROBABLY_COOP_GRID_QA
        if (NativeBackgroundQa.Enabled && NativeJoinWizardQa.Enabled)
        {
            bool authority = NativeBackgroundQa.IsHostProduct;
            mods["qa_container"] = new RoomModManifest.Entry("qa_container", "水培设备 · QA", authority ? "2.1" : "2.0", 0);
            if (!authority) mods["qa_local_sound"] = new RoomModManifest.Entry("qa_local_sound", "本地音效主题 · QA", "1.0", 0);
        }
        if (NativeBackgroundQa.Enabled && NativeBackgroundQa.IsGuestProduct && NativeJoinWizardQa.OpeningHandoff)
        {
            var provider = NativeJoinWizardQa.OpeningProvider;
            mods[provider.Id] = new RoomModManifest.Entry(provider.Id, provider.Name, provider.Version, 0, enabled: false);
        }
#endif
        return mods;
    }

    internal static Dictionary<string, RoomModManifest.Entry> ReadSavedOpeningMods(string text)
    {
        if (text == null) return null;
        if (text.Length > 8192) throw new System.IO.InvalidDataException("Saved opening Mod list is too large.");
        return RoomModManifest.ReadOpeningPackages(JToken.Parse(text));
    }

    internal static string MissingOpeningMessage(IReadOnlyDictionary<string, RoomModManifest.Entry> required)
    {
        if (required == null) return null;
        var available = Local();
        var missing = required.Values.Where(mod => !available.TryGetValue(mod.Id, out var local) ||
            !RoomModManifest.ExactPackage(local, local)).Select(mod => mod.Name + " (" + mod.Id + ")").ToArray();
        return missing.Length == 0 ? null :
            ModHelper.GetLocalized("probably_coop", "opening_save_missing_mod", string.Join(", ", missing));
    }

    internal static JArray CaptureOpening(PlayerStore store)
    {
        var required = ReadSavedOpeningMods(NativeActorCheckpoint.ReadOpeningMods(store.modData));
        if (required == null) return null;
        var active = Local();
        // The room uses the host's currently enabled package. Both players
        // can update together without binding the save to the old checksum.
        var packages = required.Values.Select(saved => active.TryGetValue(saved.Id, out var current) &&
            RoomModManifest.ExactPackage(current, current) ? current :
            throw new System.IO.InvalidDataException("Required opening Mod is not active: " + saved.Id));
        return RoomModManifest.Write(packages);
    }

    internal static JArray Capture()
    {
        var mods = Local();
#if PROBABLY_COOP_GRID_QA
        if (NativeBackgroundQa.Enabled && NativeBackgroundQa.IsHostProduct &&
            NativeJoinWizardQa.Enabled)
            mods["qa_missing_expansion"] = new RoomModManifest.Entry("qa_missing_expansion", "化学扩展 · QA", "1.4", 0, shadowedWorkshop: 1000001);
#endif
        return RoomModManifest.Write(mods.Values);
    }
}
