using System;
using System.IO;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Core;

namespace ProbablyCoop;

// Incremental frames only elide root item records. Run and setup metadata
// remain complete on every frame, including while the menu picker is open.
internal static class NativeJoinSnapshot
{
    internal static (Guid Run, bool Required) Read(byte[] frame, byte[] full, ulong fullRevision)
    {
        bool delta = NativeRecordFrame.IsDelta(frame);
        if (delta && (full == null || !NativeRecordFrame.IsFull(full)))
            throw new InvalidDataException("Missing cooperative join checkpoint");
        var state = NativeSnapshotCodec.Unpack(delta ? NativeRecordFrame.Body(frame, fullRevision) : frame);
        if (state["actors"] is not JObject actors || actors["run"]?.Type != JTokenType.String ||
            !Guid.TryParse((string)actors["run"], out var run) || run == Guid.Empty ||
            state["partnerSetupRequired"]?.Type != JTokenType.Boolean)
            throw new InvalidDataException("Host did not provide a cooperative character contract");
        return (run, (bool)state["partnerSetupRequired"]);
    }
}
