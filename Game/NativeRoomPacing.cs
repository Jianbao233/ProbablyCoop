using HarmonyLib;
using ProbablyCoop.Core;
using UnityEngine;

namespace ProbablyCoop;

// Unity dispatches room traffic and native business on the player loop. Focus
// changes must not suspend that loop while the other player is working.
[HarmonyPatch(typeof(EmporiumOption), nameof(EmporiumOption.ApplyFpsCap))]
internal static class NativeRoomPacing
{
    private enum Mode { Off, Waiting, Network, LocalPair }
    private static Mode mode;
    private static bool previousBackground;
    private static int previousRate, previousVsync;
    internal static bool PacingLocalPair => mode == Mode.LocalPair;

    internal static void Update(RoomSelector room)
    {
        var next = Mode.Off;
        if (!Application.isBatchMode && room != null && (room.Busy || room.HasLobby))
            next = room.Session?.State != LinkState.Connected ? Mode.Waiting :
                room.IsLoopback ? Mode.LocalPair : Mode.Network;
        if (next == mode) return;
        if (mode == Mode.Off)
        {
            previousRate = Application.targetFrameRate;
            previousVsync = QualitySettings.vSyncCount;
            previousBackground = Application.runInBackground;
        }
        mode = next;
        Application.runInBackground = mode != Mode.Off || previousBackground;
        var options = EmporiumOption.Instance;
        if (options != null) options.ApplyFpsCap();
        else
        {
            QualitySettings.vSyncCount = previousVsync;
            Application.targetFrameRate = previousRate;
            ApplyRoomPolicy();
        }
    }

    [HarmonyPostfix]
    private static void ApplyRoomPolicy()
    {
        if (mode == Mode.LocalPair)
        {
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = 60;
        }
        else if (mode == Mode.Network && Application.targetFrameRate == 10 && QualitySettings.vSyncCount == 0)
        {
            Application.targetFrameRate = 30;
        }
    }
}
