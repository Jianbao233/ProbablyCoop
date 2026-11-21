using System;
using System.IO;
using System.Threading;
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
        return ReadContract(NativeSnapshotCodec.Unpack(delta ? NativeRecordFrame.Body(frame, fullRevision) : frame));
    }

    internal static (Guid Run, bool Required, JToken Extensions) Read(NativeSnapshotFrame frame,NativeSnapshotFrame full,ulong fullRevision,
        CancellationToken cancellation=default)
    {
        if(frame.Delta && full?.Full!=true) throw new InvalidDataException("Missing cooperative join checkpoint");
        var metadata=frame.Delta ? NativeRecordFrame.Body(frame.First,fullRevision) : frame.First;
        var state=frame.Count==1 ? NativeSnapshotCodec.Unpack(metadata,cancellation) :
            NativeSnapshotPages.ReadMetadata(frame,cancellation);
        var join=ReadContract(state);
        var extensions=state["modExtensions"];
        state.Remove("modExtensions");
        return (join.Run,join.Required,extensions);
    }

    private static (Guid Run, bool Required) ReadContract(JObject state)
    {
        if (state["actors"] is not JObject actors || actors["run"]?.Type != JTokenType.String ||
            !Guid.TryParse((string)actors["run"], out var run) || run == Guid.Empty ||
            state["partnerSetupRequired"]?.Type != JTokenType.Boolean)
            throw new InvalidDataException("Host did not provide a cooperative character contract");
        return (run, (bool)state["partnerSetupRequired"]);
    }
}
