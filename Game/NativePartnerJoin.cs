using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Core;
using UnityEngine;

namespace ProbablyCoop;

// Character creation is a menu activity. It precedes entering the replica
// scene, so it can use the actual native perk picker without a second save.
internal sealed class NativePartnerJoin : IDisposable
{
    private ReplicaLink link;
    private ReplicaLink.Stamp stamp;
    private ulong observed;
    private Guid run;
    private bool required, picker, sent, disposing;
    private NativeActorPartnerSetup.DraftHandle pickerOwner;
    private string[] draft;
    private double retry, pickerDeadline;
    private string pickerFailure;
    private Task<(Guid Run,bool Required)> reading;
    private CancellationTokenSource readLifetime;
    private ulong readingRevision;

    internal void ObserveConnection(bool valid)
    {
        if(!valid && (link!=null || picker)) Dispose();
    }

    internal bool Ready(RoomSelector room)
    {
        var connection=room.Session?.Native;
        if(connection==null) { Dispose(); return false; }
        if(!ReferenceEquals(link,connection) || !stamp.IsCurrent(connection))
        {
            Dispose(); link=connection; stamp=connection.CaptureStamp();
        }
        if(connection.Snapshot==null || connection.Revision==0) return false;
        if(observed!=connection.Revision)
        {
            if(reading==null)
            {
                var snapshot=connection.Snapshot;
                var full=connection.FullSnapshot;
                if(snapshot.Delta && full==null) return false;
                ulong baseRevision=connection.FullRevision;
                readingRevision=connection.Revision;
                readLifetime=new CancellationTokenSource();
                var cancellation=readLifetime.Token;
                reading=Task.Run(()=>NativeJoinSnapshot.Read(snapshot,full,baseRevision,cancellation));
                return false;
            }
            if(!reading.IsCompleted) return false;
            var completed=reading;
            reading=null;
            readLifetime.Dispose();
            readLifetime=null;
            var metadata=completed.GetAwaiter().GetResult();
            var incoming=metadata.Run;
            if(run!=Guid.Empty && run!=incoming) throw new InvalidDataException("Cooperative character run changed during join");
            run=incoming; required=metadata.Required; observed=readingRevision;
        }
        if(!required) return true;
        if(sent)
        {
            if(connection.Waiting) return false;
            if(connection.Result==(ushort)ShopResult.Applied) return false; // wait for matching published facts
            if(connection.Result!=(ushort)ShopResult.Busy)
            {
                Debug.LogWarning("[probably_coop] host rejected partner character creation: "+connection.Result);
                room.Leave("partner_setup_rejected");
                return false;
            }
            sent=false; retry=Time.realtimeSinceStartupAsDouble+0.5;
        }
        if(draft==null && !picker)
        {
            if(NativeBackgroundQa.Enabled)
            {
                if(!NativePartnerSetupQa.Tick(out var selected)) return false;
                draft=selected;
                foreach(string evidence in NativePartnerSetupQa.Evidence)
                    Debug.Log("[probably_coop QA] PASS partner-native-picker "+evidence);
            }
            else
            {
                TryOpenPicker(room);
            }
        }
        if(draft!=null && !connection.Waiting && Time.realtimeSinceStartupAsDouble>=retry)
        {
            var command=NativeActorPartnerSetup.CreateDraft(run,draft);
            command["kind"]="partnerSetup";
            sent=connection.Submit(Encoding.UTF8.GetBytes(command.ToString(Newtonsoft.Json.Formatting.None)),Time.realtimeSinceStartupAsDouble);
        }
        return false;
    }
    private void TryOpenPicker(RoomSelector room)
    {
        double now=Time.realtimeSinceStartupAsDouble;
        if(now<retry) return;
        if(pickerDeadline==0) pickerDeadline=now+10;
        var expected=link; var expectedStamp=stamp;
        picker=true;
        string reason;
        try
        {
            if(NativeActorPartnerSetup.TryOpenDraft(ids=>
            {
                picker=false;
                if(ReferenceEquals(link,expected) && expectedStamp.IsCurrent(expected)) draft=ids;
            },()=> { picker=false; if(!disposing) room.Leave(); },out reason,out pickerOwner))
            {
                pickerDeadline=0;
                pickerFailure=null;
                return;
            }
        }
        catch(Exception error)
        {
            // UI construction failures should end this join, while leaving
            // the multiplayer menu and future room attempts usable.
            Debug.LogWarning("[probably_coop] native partner picker failed: "+error);
            picker=false;
            room.Leave("partner_picker_unavailable");
            return;
        }

        picker=false;
        if(pickerFailure!=reason)
        {
            pickerFailure=reason;
            Debug.Log("[probably_coop] waiting for native partner picker: "+reason);
        }
        if(now>=pickerDeadline)
        {
            room.Leave("partner_picker_unavailable");
            return;
        }
        retry=now+0.5;
    }

    public void Dispose()
    {
        if(disposing) return;
        disposing=true;
        try
        {
            if(NativeBackgroundQa.Enabled) NativePartnerSetupQa.Reset();
            NativeActorPartnerSetup.CancelDraft(pickerOwner);
        }
        finally
        {
            readLifetime?.Cancel();
            readLifetime?.Dispose();
            // A task from a departed epoch cannot update this join.
            if(reading!=null)
                _=reading.ContinueWith(task=>{ _=task.Exception; },TaskContinuationOptions.OnlyOnFaulted);
            reading=null; readLifetime=null;
            link=null; observed=0; run=Guid.Empty; required=picker=sent=false; pickerOwner=null; draft=null; retry=pickerDeadline=0; pickerFailure=null; disposing=false;
        }
    }
}

internal sealed partial class NativeWorld
{
    private bool PartnerSetupRequired()
    {
        var session=NativeActorSession.Current;
        if(session==null) return false;
        session.Store.modData.TryGetValue(NativeActorPartnerSetup.SaveKey,out var receipt);
        return !NativeActorPartnerSetup.IsComplete(receipt,session.Run.Run);
    }
    internal bool PartnerSetupRequiredForQa => PartnerSetupRequired();
    private ushort ExecutePartnerSetup(JObject command)
    {
        var session=NativeActorSession.Current;
        if(!host || session==null || session.Terminal || session.Faulted || !session.CanCommit) return (ushort)ShopResult.Busy;
        var draft=(JObject)command.DeepClone(); draft.Remove("kind");
        session.Store.modData.TryGetValue(NativeActorPartnerSetup.SaveKey,out var receipt);
        NativeActorPartnerSetup.Prepared prepared;
        try { prepared=NativeActorPartnerSetup.Prepare(session.Run,draft,receipt); }
        catch(InvalidDataException) { return (ushort)ShopResult.Unavailable; }
        try { session.Store.modData[NativeActorPartnerSetup.SaveKey]=prepared.Commit(); }
        catch(Exception error) { session.MarkFault(error); throw; }
        // Both the receipt and actor data now belong to the native in-memory
        // run. The next native save persists them together; this does not claim
        // that an unsaved day has become a durable checkpoint.
        nextCapture=0;
        return (ushort)ShopResult.Applied;
    }
}
