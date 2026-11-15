using System;
using System.Collections.Generic;
using HarmonyLib;
using ProbablyCoop.Sync;

namespace ProbablyCoop;

internal sealed partial class NativeEffectCampaign
{
    private const int MorningRows = 96;
    private const int MorningLineBytes = 1024;
    private const int MorningLimit = MorningRows * (MorningLineBytes + 2) + 6;
    private string[] morningReport = Array.Empty<string>();
    private int morningReportDay;

    internal void RecordMorningReport()
    {
        int day = StoreStation.GetDayCounter();
        if (!host || running.Kind != TeamTransitionKind.Sleep || day != running.Day + 1 || day == morningReportDay)
            return;
        var lines = new List<string>();
        foreach (string text in PlayerStore.Instance.nightLogs)
        {
            if (lines.Count == MorningRows) break;
            if (text == null) continue;
            if (Utf8.GetByteCount(text) <= MorningLineBytes) lines.Add(text);
            else
            {
                int length = Math.Min(256, text.Length);
                if (char.IsHighSurrogate(text[length - 1])) length--;
                lines.Add(text.Substring(0, length));
            }
        }
        morningReport = lines.ToArray();
        morningReportDay = day;
    }

    private void WriteMorningReport(SyncWriter writer)
    {
        RecordMorningReport();
        writer.UInt((uint)morningReportDay);
        writer.Byte((byte)morningReport.Length);
        foreach (string line in morningReport) writer.Text(line);
    }

    private static bool TryReadMorningReport(SyncReader reader, uint day, out List<string> report)
    {
        report = null;
        uint reportDay = reader.UInt();
        byte rows = reader.Byte();
        if (!reader.Ok || rows > MorningRows || reportDay > day || reportDay == 0 && rows != 0) return false;
        var lines = new List<string>(rows);
        for (int i = 0; i < rows; i++) lines.Add(reader.Text(MorningLineBytes));
        if (reportDay == day) report = lines;
        return reader.Ok;
    }

    private static void PresentMorningReport(List<string> lines)
    {
        var store = PlayerStore.Instance;
        var logs = store.nightLogs;
        var actions = store.nightlyAction;
        try
        {
            EndOfDayUIManager.Instance.CloseUI(restorePaperUI: false);
            PaperUIManager.Instance?.RestorePaperUIClosedForSleep(morning: true);
            store.nightLogs = lines;
            store.nightlyAction = new List<Action>();
            StartOfDayUIManager.Instance.ShowMorningReport();
        }
        finally
        {
            store.nightLogs = logs;
            store.nightlyAction = actions;
        }
    }
}

internal sealed partial class NativeInventoryPilot
{
    [HarmonyPatch(typeof(StartOfDayUIManager), nameof(StartOfDayUIManager.OpenUI))]
    private static class CampaignMorningReport
    {
        private static bool Prepare() => RoomRuntime.Selected;
        private static void Prefix()
        {
            if (AuthoritativeCampaign) ActiveCampaign.RecordMorningReport();
        }
    }
}
