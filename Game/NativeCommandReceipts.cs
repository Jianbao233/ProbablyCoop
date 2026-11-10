using ProbablyCoop.Core;
using UnityEngine;

namespace ProbablyCoop;

internal sealed partial class NativeWorld
{
    private void ConfirmProjectedReceipts(ReplicaLink connection, NativeReceiptProjection projection)
    {
        foreach(var receipt in projection.Receipts)
        {
            if(connection.ConfirmSnapshotReceipt(receipt.Serial,receipt.Result) && NativeBackgroundQa.Enabled)
                Debug.Log($"[probably_coop QA] snapshot_receipt serial={receipt.Serial} result={receipt.Result} revision={applied}");
        }
    }
}
