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
            return new RoomModManifest.Entry(group.Key, name.Length == 0 ? group.Key : name,
                RoomModManifest.Display(mod.Manifest.ModVersion, 48), mod.WorkshopId,
                mod.IsEnabled && string.IsNullOrEmpty(mod.LoadError), digest);
        }, StringComparer.Ordinal);
#if PROBABLY_COOP_GRID_QA
        if (NativeBackgroundQa.Enabled && NativeJoinWizardQa.Enabled)
        {
            bool authority = NativeBackgroundQa.IsHostProduct;
            mods["qa_container"] = new RoomModManifest.Entry("qa_container", "水培设备 · QA", authority ? "2.1" : "2.0", 0);
            if (!authority) mods["qa_local_sound"] = new RoomModManifest.Entry("qa_local_sound", "本地音效主题 · QA", "1.0", 0);
        }
#endif
        return mods;
    }

    internal static JArray Capture()
    {
        var mods = Local();
#if PROBABLY_COOP_GRID_QA
        if (NativeBackgroundQa.Enabled && NativeBackgroundQa.IsHostProduct &&
            NativeJoinWizardQa.Enabled)
            mods["qa_missing_expansion"] = new RoomModManifest.Entry("qa_missing_expansion", "化学扩展 · QA", "1.4", 1000001);
#endif
        return RoomModManifest.Write(mods.Values);
    }
}
