namespace ProbablyCoop.Core;

// Local support codes. They are intentionally independent of packet enums.
internal static class CoopIssueCode
{
    internal static string Settlement(DaySettlement attempt) =>
        attempt.SaveFailed ? "DAY-206" :
        attempt.SaveVerified ? (attempt.MorningCompleted ? "DAY-204" : "DAY-201") :
        attempt.MorningCompleted ? "DAY-202" : "DAY-203";

    internal static string Synchronization(NativeSyncFailure failure, bool localError, bool modMismatch) =>
        modMismatch ? "SYNC-106" : failure switch
        {
            NativeSyncFailure.HostCapture => "SYNC-101",
            NativeSyncFailure.HostEncode => "SYNC-102",
            NativeSyncFailure.GuestPageFormat => "SYNC-103",
            NativeSyncFailure.GuestTransferBudget => "SYNC-104",
            _ => localError ? "SYNC-105" : "SYNC-107"
        };

    internal static string DirectRoom(string status) => status switch
    {
        "bad_address" => "LAN-101",
        "udp_address_not_local" => "LAN-102",
        "udp_port_in_use" => "LAN-103",
        "bad_udp_port" => "LAN-104",
        "bad_mapping_address" => "LAN-105",
        _ => null
    };
}
