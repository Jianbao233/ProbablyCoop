using System;
using System.Collections.Generic;
using System.Globalization;
using ProbablyCoop.Core;

namespace ProbablyCoop;

internal static class RoomConnectionStatus
{
    private static string Loc(string key, params object[] args) =>
        ModHelper.GetLocalized("probably_coop",key,args);

    internal static (string Summary, string Details) Format(RoomSelector room, string syncPhase, string syncCounters)
    {
        var session = room.Session;
        var native = session?.Native;
        string role = room.IsHost || room.Busy && room.Status == "creating" ? "terminal_role_host" :
            room.IsGuest ? "terminal_role_guest" : "terminal_role_none";
        string transport = room.Transport switch
        {
            RoomSelector.TransportKind.Steam => "terminal_transport_steam",
            RoomSelector.TransportKind.Ipv4 => "terminal_transport_ipv4",
            RoomSelector.TransportKind.Loopback => "terminal_transport_local",
            _ => "terminal_transport_none"
        };
        string phase = room.IsHost && native?.SynchronizationFailed == true ? "status_sync_host_failed" :
            syncPhase is "sync_connecting" or "status_await_approval" or "sync_preparing" or "sync_receiving" or
                "sync_checking" or "sync_installing" or "sync_ready" or "sync_failed" ? syncPhase : "status_" + room.Status;
        string summary = Loc("terminal_overview",Loc(role),Loc(transport),Loc(phase));

        var rows = new List<string>(3);
        if (native != null)
        {
            rows.Add(Loc("terminal_packets",room.Sent,room.Received));
            if (native.Revision != 0)
                rows.Add(Loc("terminal_revision",native.Revision));

            var transfer = native.Transfer;
            if (transfer.Pages > 0)
                rows.Add(Loc("terminal_batches",transfer.CompletePages,transfer.Pages,transfer.ReceivedBytes / 1024));
            else if (native.FullSnapshot is NativeSnapshotFrame snapshot && native.FullRevision != 0)
                rows.Add(Loc("terminal_batches",snapshot.Count,snapshot.Count,snapshot.Bytes / 1024));
        }
        // On a failed join the native link may already be gone. Constrain the
        // retained UI counter to its numeric fields; never echo diagnostic text.
        if (syncPhase == "sync_failed" && native == null &&
            TryReadCounters(syncCounters,out int complete,out int pages,out int kib))
            rows.Add(Loc("terminal_batches",complete,pages,kib));
        return (summary,string.Join("\n",rows));
    }

    private static bool TryReadCounters(string counters,out int complete,out int pages,out int kib)
    {
        complete = pages = kib = 0;
        if (string.IsNullOrEmpty(counters) || counters.Length > 48 ||
            !counters.EndsWith(" KiB",StringComparison.Ordinal))
            return false;
        var fields = counters.Substring(0,counters.Length - 4).Split('/','·');
        return fields.Length == 3 &&
            int.TryParse(fields[0].Trim(),NumberStyles.None,CultureInfo.InvariantCulture,out complete) &&
            int.TryParse(fields[1].Trim(),NumberStyles.None,CultureInfo.InvariantCulture,out pages) &&
            int.TryParse(fields[2].Trim(),NumberStyles.AllowThousands,CultureInfo.CurrentCulture,out kib) &&
            pages > 0 && pages <= NativeSnapshotFrame.MaxPages && complete <= pages &&
            kib <= NativeSnapshotFrame.MaxPackedBytes / 1024;
    }
}
