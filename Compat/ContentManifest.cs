using System;
using System.Collections.Generic;
using ProbablyCoop.Net;

namespace ProbablyCoop;

// Unknown content is required by default. A mod can declare its own compatibility
// through the game's authenticated, in-process messaging API.
internal static class ContentManifest
{
    private const string ModId = "probably_coop";
    private const string Request = "manifest-request-v1";
    private const string Reply = "probably-coop-manifest-v1";

    internal static SessionManifest Read()
    {
        var enabled = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (LoadedMod mod in ModLoader.Mods)
        {
            if (mod == null || !mod.IsEnabled || string.IsNullOrEmpty(mod.Manifest?.ID)) continue;
            enabled.TryAdd(mod.Manifest.ID, mod.Manifest.ModVersion);
        }

        var declarations = new Dictionary<string, SessionMod>(StringComparer.Ordinal);
        var invalid = new HashSet<string>(StringComparer.Ordinal);
        void Receive(string sender, object payload)
        {
            if (!enabled.TryGetValue(sender, out string version) || sender == ModId || invalid.Contains(sender)) return;
            if (payload is string[] fields && fields.Length == 1 && fields[0] == "presentation")
            {
                Add(new SessionMod(sender, version, presentationOnly: true));
                return;
            }
            if (payload is string[] reply && reply.Length == 2 && reply[0] == "gameplay" &&
                SessionMod.IsAdapterProtocol(reply[1]))
            {
                Add(new SessionMod(sender, version, reply[1]));
                return;
            }
            invalid.Add(sender);
        }
        void Add(SessionMod declaration)
        {
            if (declarations.TryAdd(declaration.Id, declaration)) return;
            var previous = declarations[declaration.Id];
            if (previous.PresentationOnly != declaration.PresentationOnly ||
                previous.AdapterProtocol != declaration.AdapterProtocol) invalid.Add(declaration.Id);
        }

        var channels = new List<string>();
        try
        {
            foreach (string id in enabled.Keys)
            {
                if (id == ModId) continue;
                string channel = id + ":" + Reply;
                ModHelper.OnMessage(ModId, channel, Receive);
                channels.Add(channel);
            }
            if (channels.Count != 0) ModHelper.SendMessage(ModId, Request, null);
        }
        finally
        {
            foreach (string channel in channels) ModHelper.OffMessage(ModId, channel, Receive);
        }

        var mods = new List<SessionMod>(enabled.Count);
        foreach (var mod in enabled)
        {
            if (mod.Key == ModId)
            {
                // Candidate builds share a Workshop version. An old launcher
                // must still be rejected before incompatible world data arrives.
                string build = "effect-" + typeof(ContentManifest).Module.ModuleVersionId.ToString("N");
                mods.Add(new SessionMod(mod.Key, mod.Value, build));
            }
            else mods.Add(!invalid.Contains(mod.Key) && declarations.TryGetValue(mod.Key, out var declaration)
                ? declaration : new SessionMod(mod.Key, mod.Value));
        }
        // The native version string can survive a binary hotfix. The runtime
        // assembly identity also covers changes made between those version labels.
        string game = PlayerStore.VERSION + "/" + typeof(PlayerStore).Module.ModuleVersionId.ToString("N");
        return SessionManifest.Of(game, mods);
    }
}
