using System;
using System.IO;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Core;

namespace ProbablyCoop;

internal sealed partial class NativeWorld
{
    private NativeActorRun actorRun;
    private NativeActorState remoteHostActor;
    private Guid actorProjectionRun;
    private Harmony actorHarmony;
    private bool qaActorWoundApplied;
    private NativeActorPersonalUi.Receiver actorNotifications=new NativeActorPersonalUi.Receiver();
    internal bool HasActorProjection => actorRun != null || actorProjectionRun != Guid.Empty;
    internal NativeActorState RemoteHostActor => remoteHostActor;
    internal static bool ActorProjectionQaRequested => NativeBackgroundQa.Enabled &&
        Array.IndexOf(Environment.GetCommandLineArgs(), "--probably-coop-qa-actor-projection") >= 0;
    private static readonly Lazy<System.Reflection.FieldInfo[]> SharedWorldFieldContract = new Lazy<System.Reflection.FieldInfo[]>(
        () => WorldFields.Where(field => !NativeActorProjection.OwnsPlayerField(field.Name)).ToArray());
    private static System.Reflection.FieldInfo[] SharedWorldFields => SharedWorldFieldContract.Value;

    // Activation remains isolated QA-only until carried roots, starting effects,
    // activity continuations and the complete checkpoint have been integrated.
    // No existing cooperative or single-player slot is silently upgraded here.
    internal void EnableQaActorProjection(PlayerStore existingStore)
    {
        if (!host || !NativeBackgroundQa.IsHostProduct || !ActorProjectionQaRequested || actorRun != null ||
            Array.IndexOf(Environment.GetCommandLineArgs(), "--probably-coop-qa-input-only") < 0)
            throw new InvalidOperationException("Actor projection activation requires the isolated rapid-input QA host.");
        if (!CoopRun.IsRegistered(existingStore)) throw new InvalidOperationException("Actor QA run is not isolated and registered.");
        var partner = NativeActorState.CreateDefault();
        partner.playerName = "QA Partner";
        partner.healthData.luck = checked(existingStore.healthData.luck + 17);
        partner.healthData.woundState = 6;
        if (!existingStore.startingPerks.Any(perk => perk.id == "lucky"))
            partner.startingPerks.Add(new StartingPerk { id = "lucky", type = StartingPerk.StartingPerkType.POSITIVE });
        var loaded = ModLoader.Mods.Single(mod => mod.Assembly == typeof(NativeWorld).Assembly && mod.Manifest?.ID == "probably_coop");
        actorHarmony = loaded.HarmonyInstance ?? throw new InvalidOperationException("Missing framework Harmony.");
        actorRun = new NativeActorRun(existingStore, partner);
        qaActorCarry = new NativeActorCarry(EmporiumEntry.Instance,()=>actorRun.CurrentActor,NativeActorRun.Slot.Host);
        try { NativeActorFieldAccess.Install(actorHarmony, actorRun.Resolve); }
        catch { actorRun.Dispose(); actorRun = null; throw; }
    }

    private JObject CaptureActors() => actorRun == null ? null : NativeActorProjection.Capture(actorRun.Run,
        NativeActorState.Capture(PlayerStore.Instance), actorRun.Partner);

    private NativeActorProjection.Prepared PrepareActorProjection(JObject snapshot, out Guid run)
    {
        run = actorProjectionRun;
        var value = snapshot["actors"];
        bool hasActors = value != null && value.Type != JTokenType.Null;
        if (!hasActors && (actorProjectionRun != Guid.Empty || ActorProjectionQaRequested))
            throw new InvalidDataException("Actor projection cannot disappear during a cooperative run.");
        if (!hasActors) return null;
        NativeActorProjection.RejectLegacyPersonalMirrors(snapshot);
        if (value is not JObject envelope || envelope["run"]?.Type != JTokenType.String ||
            !Guid.TryParse((string)envelope["run"], out var incomingRun) || incomingRun == Guid.Empty)
            throw new InvalidDataException("Missing actor run identity.");
        if (run == Guid.Empty) run = incomingRun;
        var parsed = NativeActorProjection.Read(value, run);
        remoteHostActor ??= NativeActorState.CreateDefault();
        guestActorCarry ??= new NativeActorCarry(EmporiumEntry.Instance,
            () => NativeActorRun.Slot.Partner,NativeActorRun.Slot.Partner);
        return parsed.PrepareGuest(PlayerStore.Instance, remoteHostActor);
    }

    private void CloseActorProjection()
    {
        Exception patchError = null;
        try
        {
            if (actorHarmony != null) NativeActorFieldAccess.Uninstall(actorHarmony);
            actorHarmony = null;
        }
        catch (Exception error) { patchError = error; }
        try
        {
            if(actorHarmony!=null || NativeActorSession.Current==null || !ReferenceEquals(actorRun,NativeActorSession.Current.Run))
                actorRun?.Dispose();
            actorRun = null;
            guestActorCarry?.DisposeForSceneUnload(); guestActorCarry=null;
            qaActorCarry?.DisposeForSceneUnload(); qaActorCarry=null;
        }
        catch (Exception error)
        {
            if (patchError != null) throw new AggregateException("Actor patch and run cleanup failed", patchError, error);
            throw;
        }
        finally
        {
            remoteHostActor = null; actorProjectionRun = Guid.Empty;
            actorNotifications=new NativeActorPersonalUi.Receiver();
        }
        if (patchError != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(patchError).Throw();
    }

    internal bool RequestQaActorWound() => ActorProjectionQaRequested && HasActorProjection &&
        Submit(new JObject { ["kind"] = "qaActorWound" });

    private ushort ExecuteQaActorWound()
    {
        if (!ActorProjectionQaRequested || actorRun == null || qaActorWoundApplied)
            return (ushort)ShopResult.Unavailable;
        // This explicit QA action exercises the same authenticated request and
        // snapshot path. The native method is real; it is not a gameplay button
        // adapter or evidence that every gameplay command has actor context.
        // Capture immediately before the action: the standard QA fixture may
        // legitimately add a host perk after the actor run was first created.
        var hostBefore = NativeActorState.Capture(PlayerStore.Instance).Serialize();
        using (actorRun.Enter(NativeActorRun.Slot.Partner)) HealthData.ReceiveMinorWound();
        if (actorRun.Partner.healthData.woundState != 8)
            throw new InvalidOperationException("Native personal operation did not update partner wounds: " + actorRun.Partner.healthData.woundState);
        if (!JToken.DeepEquals(hostBefore, NativeActorState.Capture(PlayerStore.Instance).Serialize()))
            throw new InvalidOperationException("Native partner operation mutated the host personal state.");
        qaActorWoundApplied = true;
        nextCapture = 0;
        UnityEngine.Debug.Log("[probably_coop QA] PASS actor-network-host-mutation native=ReceiveMinorWound partner=8 host=unchanged");
        return (ushort)ShopResult.Applied;
    }
}
