using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ProbablyCoop;

internal sealed partial class NativeWorld
{
    internal enum AudioCue { CashRegister, ShopRegister, PhoneAnswered, PhoneEnded, PhoneInvalid }

    private const int AudioHistoryLimit = 16;
    private readonly Queue<(ulong Serial, AudioCue Cue)> audioHistory = new();
    private ulong audioSerial, appliedAudio;
    private bool audioInitialized, ringingLocally;
#if PROBABLY_COOP_GRID_QA
    internal int QaRegisterPlays { get; private set; }
    internal int QaPhoneRings { get; private set; }
    internal int QaInvalidNumbers { get; private set; }
#endif

    internal void RecordAudio(AudioCue cue)
    {
        if (!host || closed || !peerConnected) return;
        audioHistory.Enqueue((++audioSerial, cue));
        while (audioHistory.Count > AudioHistoryLimit) audioHistory.Dequeue();
        nextCapture = 0;
    }

    private JArray CaptureAudio()
    {
        var rows = new JArray();
        foreach (var (serial, cue) in audioHistory) rows.Add(new JArray(serial, (int)cue));
        return rows;
    }

    private void ApplyAudio(JToken payload)
    {
        if (payload is not JArray rows || rows.Count > AudioHistoryLimit) return;
        var phone = PhoneUIManager.Instance;
        bool phoneVisible = phone?.phonePanel?.activeInHierarchy == true;
        bool calling = phoneVisible && (bool?)phoneState?["calling"] == true;
        if (calling != ringingLocally)
        {
            if (calling) PlayReplicaCue(phone.phoneCalling, "PhoneCalling");
            else StopReplicaRingtone();
            ringingLocally = calling;
        }

        foreach (var token in rows)
        {
            if (token is not JArray row || row.Count != 2 ||
                !ulong.TryParse(row[0].ToString(), out ulong serial) || serial <= appliedAudio ||
                !int.TryParse(row[1].ToString(), out int id) || id < 0 || id > (int)AudioCue.PhoneInvalid)
                continue;
            appliedAudio = serial;
            if (!audioInitialized) continue;
            var cue = (AudioCue)id;
            var source = cue switch
            {
                AudioCue.CashRegister => NegociationUIManager.Instance?.audioSourceCashRegister,
                AudioCue.ShopRegister => EmporiumAudioPlayer.Instance?.audioRegister,
                AudioCue.PhoneAnswered when phoneVisible => phone.phoneClientPickup,
                AudioCue.PhoneEnded when phoneVisible => phone.phoneClientDrop,
                AudioCue.PhoneInvalid when phoneVisible => phone.phoneNoNumber,
                _ => null
            };
            PlayReplicaCue(source, cue.ToString());
        }
        audioInitialized = true;
    }

    private void PlayReplicaCue(AudioSource source, string cue)
    {
        if (source == null || !source.isActiveAndEnabled || source.clip == null) return;
        source.Play();
#if PROBABLY_COOP_GRID_QA
        switch (cue)
        {
            case nameof(AudioCue.CashRegister): QaRegisterPlays++; break;
            case "PhoneCalling": QaPhoneRings++; break;
            case nameof(AudioCue.PhoneInvalid): QaInvalidNumbers++; break;
        }
#endif
        if (NativeBackgroundQa.Enabled) Debug.Log("[probably_coop QA] replica audio " + cue);
    }

    private void StopReplicaRingtone()
    {
        if (ringingLocally && PhoneUIManager.Instance?.phoneCalling is { } source && source != null)
            source.Stop();
        ringingLocally = false;
    }
}

[HarmonyPatch]
internal static class NativeSharedAudioPlayback
{
    private static readonly MethodInfo PlayAudio = AccessTools.Method(typeof(AudioSource), nameof(AudioSource.Play), Type.EmptyTypes);
    private static readonly MethodInfo PlayAndRecord = AccessTools.Method(typeof(NativeSharedAudioPlayback), nameof(Play));

    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(NegociationUIManager), nameof(NegociationUIManager.OnOfferAcceptClick));
        yield return AccessTools.Method(typeof(NegociationUIManager), nameof(NegociationUIManager.OnWholesaleClick));
        yield return AccessTools.Method(typeof(NegociationUIManager), nameof(NegociationUIManager.BuyMultipleItem));
        yield return AccessTools.Method(typeof(NegociationUIManager), nameof(NegociationUIManager.SellMultiple));
        yield return AccessTools.Method(typeof(PlayerStore), nameof(PlayerStore.SellItemFromTable));
        yield return AccessTools.Method(typeof(PlayerStore), nameof(PlayerStore.ModCash));
        yield return AccessTools.Method(typeof(PlayerStore), nameof(PlayerStore.GrantWantedCaptureReward));
        yield return AccessTools.Method(typeof(PlayerStore), nameof(PlayerStore.OnCustomerReported));
        yield return AccessTools.Method(typeof(LotteryHelper), nameof(LotteryHelper.TryRedeemPrize));
        yield return AccessTools.Method(typeof(EmporiumAudioPlayer), nameof(EmporiumAudioPlayer.PlayRegister));
        yield return AccessTools.Method(typeof(AppraisalServiceHelper), nameof(AppraisalServiceHelper.GiveAppraisalFee));
        yield return AccessTools.Method(typeof(AugHelper), nameof(AugHelper.OnGoodKill));
        yield return AccessTools.Method(typeof(PhoneUIManager), nameof(PhoneUIManager.HandleCall));
        yield return AccessTools.Method(typeof(PhoneUIManager), nameof(PhoneUIManager.StartPhoneDialogSequence));
        yield return AccessTools.Method(typeof(PhoneUIManager), nameof(PhoneUIManager.OnPhoneDialogEnd));
        yield return AccessTools.Method(typeof(PhoneUIManager), nameof(PhoneUIManager.NoNumber));
        yield return AccessTools.Method(typeof(PhoneUIManager), nameof(PhoneUIManager.AutoCall));
    }

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code) =>
        code.MethodReplacer(PlayAudio, PlayAndRecord);

    internal static void Play(AudioSource source)
    {
        source.Play();
        var host = NativeWorld.Host;
        if (host == null) return;
        var phone = PhoneUIManager.Instance;
        if (source == NegociationUIManager.Instance?.audioSourceCashRegister) host.RecordAudio(NativeWorld.AudioCue.CashRegister);
        else if (source == EmporiumAudioPlayer.Instance?.audioRegister) host.RecordAudio(NativeWorld.AudioCue.ShopRegister);
        else if (phone != null && source == phone.phoneClientPickup) host.RecordAudio(NativeWorld.AudioCue.PhoneAnswered);
        else if (phone != null && source == phone.phoneClientDrop) host.RecordAudio(NativeWorld.AudioCue.PhoneEnded);
        else if (phone != null && source == phone.phoneNoNumber) host.RecordAudio(NativeWorld.AudioCue.PhoneInvalid);
    }
}
