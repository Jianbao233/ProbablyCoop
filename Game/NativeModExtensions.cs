using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Api;
using ProbablyCoop.Core;
using UnityEngine;

namespace ProbablyCoop;

internal sealed partial class NativeWorld
{
    private const string ModExtensionOwner = "probably_coop";
    private sealed class ModExtension
    {
        internal readonly ModExtensionContract Contract;
        internal readonly CoopExtensionRegistration Registration;
        internal CoopExtensionSession Session;
        internal CoopExtensionState State;

        internal ModExtension(ModExtensionContract contract,CoopExtensionRegistration registration)
        {
            Contract = contract;
            Registration = registration;
        }
    }

    private sealed class ModActionPending
    {
        internal readonly Action<CoopActionResult> Complete;
        internal ushort Result;
        internal ModActionPending(Action<CoopActionResult> complete) { Complete = complete; }
    }

    private Dictionary<string,ModExtension> modExtensions;
    private JArray roomMods;
    private readonly Dictionary<ulong,ModActionPending> pendingModActions = new Dictionary<ulong,ModActionPending>();
    private ulong modActionsProjectedThrough;
    private bool modExtensionExecuting;
    private bool modExtensionHostInitialized;
    private Exception modExtensionSetupFailure;

    private static Dictionary<string,ModExtension> DiscoverModExtensions()
    {
        var mods = ModLoader.Mods.Where(mod => mod?.IsEnabled==true && mod.Assembly!=null &&
            ModExtensionContract.IsToken(mod.Manifest?.ID) && mod.Manifest.ID!=ModExtensionOwner)
            .GroupBy(mod=>mod.Manifest.ID,StringComparer.Ordinal)
            .ToDictionary(group=>group.Key,group=>group.First().Manifest.ModVersion,StringComparer.Ordinal);
        var declared = new Dictionary<string,ModExtension>(StringComparer.Ordinal);
        var badRegistrations = new HashSet<string>(StringComparer.Ordinal);
        bool registrationOverflow = false;
        void Receive(string sender,object payload)
        {
            if(!mods.TryGetValue(sender,out var version)) return;
            if(payload is not CoopExtensionRegistration registration || declared.ContainsKey(sender))
            {
                badRegistrations.Add(sender);
                return;
            }
            if(declared.Count==ModExtensionContract.MaxExtensions)
            {
                registrationOverflow = true;
                return;
            }
            try { declared.Add(sender,new ModExtension(new ModExtensionContract(sender,version,registration),registration)); }
            catch(InvalidDataException) { badRegistrations.Add(sender); }
        }
        var subscribed = new List<string>();
        try
        {
            foreach(string id in mods.Keys)
            {
                var channel = id + ":" + CoopExtensionApi.RegistrationChannel;
                ModHelper.OnMessage(ModExtensionOwner,channel,Receive);
                subscribed.Add(channel);
            }
            if(subscribed.Count>0)
                ModHelper.SendMessage(ModExtensionOwner,"extension-discover-v1",null);
        }
        finally
        {
            foreach(string channel in subscribed)
                ModHelper.OffMessage(ModExtensionOwner,channel,Receive);
        }
        if(registrationOverflow) throw new InvalidDataException("Too many cooperative extensions for one shop");
        if(badRegistrations.Count>0)
            throw new InvalidDataException("Invalid cooperative extension registrations: " +
                string.Join(", ",badRegistrations.OrderBy(id=>id,StringComparer.Ordinal)));

        return declared;
    }

    private Dictionary<string,ModExtension> GetModExtensions()
    {
        if(modExtensions!=null) return modExtensions;
        var declared=modExtensions=DiscoverModExtensions();
        foreach(var extension in declared.Values)
        {
            var session = new CoopExtensionSession(host ? CoopExtensionActor.Host : CoopExtensionActor.Partner,
                () => IsModExtensionReady(extension),() => ReadModExtensionState(extension),
                (action,payload,revision,complete) => RequestModAction(extension,action,payload,revision,complete));
            extension.Session = session;
            extension.Registration.Attach(session);
        }
        return declared;
    }

    private void InitializeHostModExtensions()
    {
        if(modExtensionHostInitialized) return;
        modExtensionHostInitialized = true;
        try
        {
            foreach(var extension in GetModExtensions().Values)
                extension.State = extension.Contract.Freeze(null,ReadModAuthorityState(extension),false);
        }
        catch(Exception error)
        {
            CloseModExtensions();
            modExtensionSetupFailure = error;
            Debug.LogWarning("[probably_coop] extension setup failed; host shop retained. " + error);
        }
    }

    private bool IsModExtensionReady(ModExtension extension) => !closed && !modExtensionExecuting &&
        ModHelper.IsModRunning(extension.Contract.Id) && extension.State!=null && link!=null &&
        (host ? shop!=null : !link.SynchronizationFailed && applied!=0 && projectingGraph==null && Ready);

    private CoopExtensionState ReadModExtensionState(ModExtension extension)
    {
        if(host) extension.State = extension.Contract.Freeze(extension.State,ReadModAuthorityState(extension),false);
        return extension.State;
    }

    private JArray CaptureModExtensions()
    {
        if(modExtensionSetupFailure!=null)
            throw new InvalidDataException("Cooperative extension setup failed",modExtensionSetupFailure);
        var extensions = GetModExtensions();
        var rows = new JArray();
        foreach(var extension in extensions.Values.OrderBy(e=>e.Contract.Id,StringComparer.Ordinal))
        {
            if(!ModHelper.IsModRunning(extension.Contract.Id))
                throw new InvalidDataException("Cooperative extension is no longer enabled: " + extension.Contract.Id);
            rows.Add(extension.Contract.Write(ReadModExtensionState(extension)));
        }
#if PROBABLY_COOP_GRID_QA
        if (NativeBackgroundQa.Enabled && NativeBackgroundQa.IsHostProduct && NativeJoinWizardQa.Enabled && NativeJoinWizardQa.Blocked)
            rows.Add(new JObject { ["id"]="qa_missing_expansion", ["version"]="1.4", ["protocol"]="qa_1",
                ["actions"]=new JArray("activate"), ["revision"]=1, ["payload"]="{}" });
#endif
        return rows;
    }

    private List<(ModExtension Extension,CoopExtensionState State)> PrepareModExtensionProjection(JToken token)
    {
        var extensions = GetModExtensions();
        var prepared = new List<(ModExtension,CoopExtensionState)>(extensions.Count);
        CheckModExtensionRows(token,extensions,extension=>extension.Contract,(extension,state)=>
        {
            if(!ReferenceEquals(state,extension.State)) prepared.Add((extension,state));
        },extension=>extension.State);
        return prepared;
    }

    // Check before opening the native perk picker, so a rejected join cannot
    // commit a new partner character. Discovering a contract has no session effects.
    internal static Dictionary<string,string> ReviewModExtensionJoin(JToken token,ref Dictionary<string,ModExtensionContract> contracts)
    {
        contracts ??= DiscoverModExtensions().ToDictionary(row=>row.Key,row=>row.Value.Contract,StringComparer.Ordinal);
        var rows = ModExtensionContract.ReadRows(token);
        var blockers = new Dictionary<string,string>(StringComparer.Ordinal);
        foreach (string id in rows.Keys)
            if (!contracts.ContainsKey(id) || !ModHelper.IsModRunning(id))
                blockers[id] = "sync_extension_missing_guest";
        foreach (var contract in contracts.Values)
        {
            if (!rows.TryGetValue(contract.Id, out var row)) blockers[contract.Id] = "sync_extension_missing_host";
            else if (!blockers.ContainsKey(contract.Id))
            {
                try { contract.Read(row, null); }
                catch (InvalidDataException) { blockers[contract.Id] = "sync_extension_mismatch"; }
            }
        }
        return blockers;
    }

    private static void CheckModExtensionRows<T>(JToken token,Dictionary<string,T> extensions,
        Func<T,ModExtensionContract> contractOf,Action<T,CoopExtensionState> accept,Func<T,CoopExtensionState> previous)
    {
        var rows = ModExtensionContract.ReadRows(token);
        foreach(string id in rows.Keys)
            if(!extensions.ContainsKey(id))
                throw new ModExtensionJoinException("sync_extension_missing_guest",id,"Missing guest cooperative extension");
        foreach(var extension in extensions.Values)
        {
            var contract=contractOf(extension);
            if(!ModHelper.IsModRunning(contract.Id))
                throw new ModExtensionJoinException("sync_extension_missing_guest",contract.Id,"Cooperative extension is no longer enabled");
            if(!rows.TryGetValue(contract.Id,out var row))
                throw new ModExtensionJoinException("sync_extension_missing_host",contract.Id,"Missing host cooperative extension");
            CoopExtensionState state;
            try { state=contract.Read(row,previous?.Invoke(extension)); }
            catch(InvalidDataException error)
            {
                throw new ModExtensionJoinException("sync_extension_mismatch",contract.Id,"Incompatible cooperative extension data",error);
            }
            accept?.Invoke(extension,state);
        }
    }

    private static void ProjectModExtensions(List<(ModExtension Extension,CoopExtensionState State)> prepared)
    {
        foreach(var row in prepared)
        {
            row.Extension.Registration.ProjectState(row.State);
            row.Extension.State = row.State;
        }
    }

    private bool ModActionAllowed() => !closed && !modExtensionExecuting && !EndingActive &&
        NativeActorSession.Current is { Terminal:false, Faulted:false, Hydrating:false, NightActive:false, TransitionActive:false } &&
        shop!=null && !shop.BlocksWorldCommands && !PartnerHasHand &&
        ItemMouseDragHandler.current?.IsDraggingItem!=true && ItemMultiSelectHandler.current?.IsGroupDragging!=true;

    private CoopActionSubmission RequestModAction(ModExtension extension,string action,string payload,ulong revision,
        Action<CoopActionResult> complete)
    {
        if(!ModExtensionContract.IsToken(action) || !extension.Contract.HasAction(action) || revision==0 ||
            !ModExtensionContract.IsText(payload,CoopExtensionApi.MaximumActionBytes)) return CoopActionSubmission.Invalid;
        if(host)
        {
            if(!ModActionAllowed() || NativeActorSession.Current.Run.CurrentActor!=NativeActorRun.Slot.Host)
                return CoopActionSubmission.Busy;
            var result = RunModAction(extension,action,payload,revision,CoopExtensionActor.Host);
            NotifyModAction(complete,result);
            return CoopActionSubmission.Completed;
        }
        if(pendingModActions.Count>=4) return CoopActionSubmission.Busy;
        var command = new JObject {
            ["kind"] = "modExtension", ["extension"] = extension.Contract.Id, ["version"] = extension.Contract.PackageVersion,
            ["protocol"] = extension.Contract.Protocol, ["action"] = action, ["payload"] = payload, ["stateRevision"] = revision
        };
        SealOperation(command);
        byte[] bytes = Encoding.UTF8.GetBytes(command.ToString(Formatting.None));
        if(bytes.Length>ReplicaLink.MaxCommand) return CoopActionSubmission.Invalid;
        if(!link.SubmitIndependent(bytes,Time.realtimeSinceStartupAsDouble,out ulong serial)) return CoopActionSubmission.Busy;
        pendingModActions.Add(serial,new ModActionPending(complete));
        return CoopActionSubmission.Queued;
    }

    private ushort ExecuteModAction(JObject command)
    {
        if(command.Count!=8 || command["extension"]?.Type!=JTokenType.String || command["action"]?.Type!=JTokenType.String ||
            command["version"]?.Type!=JTokenType.String || command["protocol"]?.Type!=JTokenType.String ||
            command["payload"]?.Type!=JTokenType.String || command["stateRevision"]?.Type!=JTokenType.Integer ||
            !ulong.TryParse(command["stateRevision"].ToString(),out ulong revision) || revision==0)
            return (ushort)ShopResult.Unavailable;
        string id = (string)command["extension"];
        string action = (string)command["action"];
        string payload = (string)command["payload"];
        if(modExtensions==null || !ModExtensionContract.IsToken(id) || !modExtensions.TryGetValue(id,out var extension) ||
            !ModHelper.IsModRunning(id) || (string)command["version"]!=extension.Contract.PackageVersion ||
            (string)command["protocol"]!=extension.Contract.Protocol || !ModExtensionContract.IsToken(action) ||
            !extension.Contract.HasAction(action) || !ModExtensionContract.IsText(payload,CoopExtensionApi.MaximumActionBytes))
            return (ushort)ShopResult.Unavailable;
        if(!ModActionAllowed() || PartnerSetupRequired()) return (ushort)ShopResult.Busy;
        return RunModAction(extension,action,payload,revision,CoopExtensionActor.Partner) switch {
            CoopActionResult.Applied => (ushort)ShopResult.Applied,
            CoopActionResult.Stale => (ushort)ShopResult.Stale,
            CoopActionResult.Busy => (ushort)ShopResult.Busy,
            CoopActionResult.Rejected => (ushort)ShopResult.Unavailable,
            _ => (ushort)ShopResult.Failed
        };
    }

    private CoopActionResult RunModAction(ModExtension extension,string action,string payload,ulong revision,CoopExtensionActor actor)
    {
        modExtensionExecuting = true;
        try
        {
            extension.State = extension.Contract.Freeze(extension.State,ReadModAuthorityState(extension),false);
            if(extension.State.Revision!=revision) return CoopActionResult.Stale;
            var result = extension.Registration.Execute(new CoopExtensionAction(actor,action,payload));
            if(result==CoopActionResult.Applied)
            {
                extension.State = extension.Contract.Freeze(extension.State,ReadModAuthorityState(extension),true);
                nextCapture = 0;
            }
            else if(result is not (CoopActionResult.Rejected or CoopActionResult.Stale or CoopActionResult.Busy))
                throw new InvalidOperationException("Cooperative extension callback returned an invalid result: " + extension.Contract.Id);
            return result;
        }
        catch(Exception error)
        {
            // The author may already have mutated native state. Reuse the
            // existing no-save run recovery boundary rather than claim rollback.
            NativeActorSession.Current?.MarkFault(error);
            throw;
        }
        finally { modExtensionExecuting = false; }
    }

    private string ReadModAuthorityState(ModExtension extension)
    {
        // A host UI read can enter here outside an action. Keep session calls
        // unavailable inside any state callback, including reads of another extension.
        bool executing = modExtensionExecuting;
        modExtensionExecuting = true;
        try
        {
            using var authority = NativeActorSession.Current?.Enter(NativeActorRun.Slot.Host);
            return extension.Registration.ReadState();
        }
        finally { modExtensionExecuting = executing; }
    }

    private void DrainModActionReplies()
    {
        if(pendingModActions.Count==0) return;
        foreach(var row in pendingModActions.OrderBy(p=>p.Key).ToArray())
        {
            if(row.Value.Result==0 && link.TryTakeIndependentResult(row.Key,out ushort result)) row.Value.Result = result;
            if(row.Value.Result==0 || ((ShopResult)row.Value.Result==ShopResult.Applied && modActionsProjectedThrough<row.Key)) continue;
            pendingModActions.Remove(row.Key);
            NotifyModAction(row.Value.Complete,(ShopResult)row.Value.Result switch {
                ShopResult.Applied => CoopActionResult.Applied,
                ShopResult.Stale => CoopActionResult.Stale,
                ShopResult.Busy => CoopActionResult.Busy,
                ShopResult.Unavailable or ShopResult.NoSpace => CoopActionResult.Rejected,
                _ => CoopActionResult.Failed
            });
        }
    }

    private static void NotifyModAction(Action<CoopActionResult> complete,CoopActionResult result)
    {
        try { complete?.Invoke(result); }
        catch(Exception error) { Debug.LogWarning("[probably_coop] extension completion callback failed: " + error); }
    }

    private void CloseModExtensions()
    {
        if(modExtensions!=null)
            foreach(var extension in modExtensions.Values) extension.Session?.Close();
        modExtensions=null;
        foreach(var action in pendingModActions.Values) NotifyModAction(action.Complete,CoopActionResult.Disconnected);
        pendingModActions.Clear();
    }
}
