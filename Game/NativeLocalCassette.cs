using System;
using System.Collections.Generic;
using HarmonyLib;

namespace ProbablyCoop;

internal static class NativeLocalCassette
{
    private const string State = MachineCassettePlayer.CASETTE_PLAYER_STATE_INT;
    private const string Index = MachineCassettePlayer.CURRENT_PLAYING_CASSETTE_INDEX_TAG;
    private const string Track = MachineCassettePlayer.CURRENT_PLAYING_TRACK_ID_TAG;
    private static readonly string[] PlaybackTags = { State, Index, Track };

    internal enum Control { None, Previous, Play, Stop, Next, LowerVolume, RaiseVolume }

    internal static bool IsDeck(GameItem item) => item?.uniqueId == -103 && item.IsID("cassette_player");

    private static MachineryPanelRow Panels(GameItem item) => item?.contentWindow?.childElement as MachineryPanelRow;

    internal static GameGridInventory PlayingInventory(GameItem item) =>
        Panels(item)?.GetPanel("cassette")?.content is MachineryIntake intake
            ? intake.content as GameGridInventory
            : null;

    private static IReadOnlyList<PixelElement> Controls(GameItem item, string role) =>
        (Panels(item)?.GetPanel(role)?.content as MachineryStack)?.elements;

    internal static Control ReadControl(GameItem item, MachineryButton button)
    {
        if (!IsDeck(item)) return Control.None;
        var playback = Controls(item, "controls");
        if (playback?.Count == 4)
        {
            if (ReferenceEquals(playback[0], button)) return Control.Previous;
            if (ReferenceEquals(playback[1], button)) return Control.Play;
            if (ReferenceEquals(playback[2], button)) return Control.Stop;
            if (ReferenceEquals(playback[3], button)) return Control.Next;
        }
        var volume = Controls(item, "volume");
        if (volume?.Count == 3 && volume[1] is MachineryText)
        {
            if (ReferenceEquals(volume[0], button)) return Control.LowerVolume;
            if (ReferenceEquals(volume[2], button)) return Control.RaiseVolume;
        }
        return Control.None;
    }

    internal static bool TryUseReplicaControl(GameItem item, MachineryButton button)
    {
        var control = ReadControl(item, button);
        if (control == Control.None) return false;
        var tapes = PlayingInventory(item);
        switch (control)
        {
            case Control.Previous: if (tapes != null) MachineCassettePlayer.OnPreviousButtonPressed(item, tapes); break;
            case Control.Play: if (tapes != null) MachineCassettePlayer.OnPlayButtonPressed(item, tapes); break;
            case Control.Stop: MachineCassettePlayer.OnStopButtonPressed(item); break;
            case Control.Next: if (tapes != null) MachineCassettePlayer.OnNextButtonPressed(item, tapes); break;
            case Control.LowerVolume:
            case Control.RaiseVolume:
                MachineCassettePlayer.ChangeVolumeClick((MachineryText)Controls(item, "volume")[1], control == Control.RaiseVolume);
                break;
        }
        return true;
    }

    internal static void RefreshReplica(GameItem item)
    {
        if (!IsDeck(item)) return;
        var volume = Controls(item, "volume");
        if (volume?.Count == 3 && volume[1] is MachineryText label)
        {
            string text = LocHelper.GetLocalizedMechanic("mech_cassette_player_vol_value",
                (EmporiumOption.Instance.musicVolume * 100f).ToString("0"));
            if (label.GetText() != text) label.SetText(text);
        }
        RefreshPlaylist(item);
    }

    internal static void RefreshPlaylist(GameItem item)
    {
        var tapes = PlayingInventory(item);
        if (tapes == null || item.GetTagReadonly(State).GetInt() == 0) return;
        int playingIndex = item.GetTagReadonly(Index).GetInt();
        string playingTrack = item.GetTagReadonly(Track).GetString();
        int index = 0;
        foreach (var tape in tapes.childItems)
        {
            if (!tape.IsTag("CASSETTE_TAG")) continue;
            if (index == playingIndex && tape.GetTagReadonly("CASSETTE_CONTENT").GetString() == playingTrack) return;
            index++;
        }
        MachineCassettePlayer.OnCassetteInventoryChanged(item, tapes);
    }

    internal static Dictionary<string, TagState> SharedTags(TagSystem tags)
    {
        var shared = new Dictionary<string, TagState>(tags.dict);
        foreach (string key in PlaybackTags) shared.Remove(key);
        return shared;
    }

    internal static void PreservePlaybackTags(TagSystem previous, TagSystem next)
    {
        foreach (string key in PlaybackTags)
        {
            if (previous.dict.TryGetValue(key, out var value)) next.dict[key] = value;
            else next.dict.Remove(key);
        }
    }

    [HarmonyPatch(typeof(MachineCassettePlayer), nameof(MachineCassettePlayer.GetDeckOwner))]
    private static class ReplicaDeckOwner
    {
        private static void Postfix(GameInventory deck, ref GameItem __result)
        {
            if (__result != null || deck == null || !NativeGuest.Active) return;
            var player = EmporiumEntry.Instance?.cassettePlayer;
            if (IsDeck(player) && ReferenceEquals(PlayingInventory(player), deck)) __result = player;
        }
    }
}
