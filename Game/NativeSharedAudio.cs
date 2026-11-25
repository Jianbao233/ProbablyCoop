using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ProbablyCoop;

internal sealed partial class NativeWorld
{
    // Audio cues are serialized by ID. Keep the existing order when adding cues.
    internal enum AudioCue
    {
        CashRegister, ShopRegister, PhoneAnswered, PhoneEnded, PhoneInvalid,
        PourWater, DumpWater, DropperDraw, UseFaucet, UseWaterFilter, SecurityAlarm,
        Blender, AutoBlender, ModuleInsert, ModuleRemove, EffectModuleInsert,
        BatteryInsert, BatteryRemove, Scanner, Motor, Scavenge, ScavengeMinor, ScavengeMajor,
        PrintLabels, InspectionVictory, InspectionScam, InspectionFailed,
        GunMetalMount, GunMetalUnmount, GunPlasticMount, GunPlasticUnmount, GunRailMount, GunRailUnmount,
        GunLightMagazine, GunPistolMagazine, GunSmgMagazine,
        MachineUnscrew, MachineCutWire, MachineWeld,
        KeycardAccepted, KeycardRejected,
        StoryPistolBurst, StoryGunshot, BombCountdown, BombExplosion
    }

    private const int AudioHistoryLimit = 16;
    private readonly Queue<(ulong Serial, AudioCue Cue)> audioHistory = new();
    private ulong audioSerial, appliedAudio;
    private bool audioInitialized, ringingLocally;
#if PROBABLY_COOP_GRID_QA
    internal int QaRegisterPlays { get; private set; }
    internal int QaPhoneRings { get; private set; }
    internal int QaInvalidNumbers { get; private set; }
    internal int QaLiquidCues { get; private set; }
    internal int QaSecurityAlarms { get; private set; }
    internal int QaMachineCues { get; private set; }
    internal int QaScavengeCues { get; private set; }
    internal int QaLabelCues { get; private set; }
    internal int QaInspectionVictoryCues { get; private set; }
    internal int QaInspectionScamCues { get; private set; }
    internal int QaInspectionFailedCues { get; private set; }
    internal int QaMachineSalvageCues { get; private set; }
    internal int QaKeycardAccepted { get; private set; }
    internal int QaKeycardRejected { get; private set; }
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
                !int.TryParse(row[1].ToString(), out int id) || id < 0 || id > (int)AudioCue.BombExplosion)
                continue;
            appliedAudio = serial;
            if (!audioInitialized) continue;
            var cue = (AudioCue)id;
            if (cue >= AudioCue.StoryPistolBurst)
            {
                // A delayed/coalesced snapshot may already reveal the ending.
                if (cue != AudioCue.BombCountdown || !endingActive) NativeLocationAudio.Play(cue);
                continue;
            }
            if (cue is >= AudioCue.PourWater and <= AudioCue.UseWaterFilter)
            {
                if (AudioManager.Instance != null)
                {
                    NativeLiquidAudio.Play(cue);
#if PROBABLY_COOP_GRID_QA
                    QaLiquidCues++;
#endif
                }
                continue;
            }
            if (cue is >= AudioCue.Blender and <= AudioCue.ScavengeMajor)
            {
                if (NativeLocationAudio.Play(cue))
                {
#if PROBABLY_COOP_GRID_QA
                    if (cue <= AudioCue.Motor) QaMachineCues++;
                    else QaScavengeCues++;
#endif
                    if (NativeBackgroundQa.Enabled) Debug.Log("[probably_coop QA] replica location audio " + cue);
                }
                continue;
            }
            if (cue is >= AudioCue.GunMetalMount and <= AudioCue.GunSmgMagazine)
            {
                NativeGunWorkshopAudio.Play(cue);
                continue;
            }
            var source = cue switch
            {
                AudioCue.CashRegister => NegociationUIManager.Instance?.audioSourceCashRegister,
                AudioCue.ShopRegister => EmporiumAudioPlayer.Instance?.audioRegister,
                AudioCue.SecurityAlarm => EmporiumAudioPlayer.Instance?.alarm2,
                AudioCue.PrintLabels => EmporiumAudioPlayer.Instance?.audioLabeler,
                AudioCue.InspectionVictory => EmporiumAudioPlayer.Instance?.audioBeatVictory,
                AudioCue.InspectionScam => EmporiumAudioPlayer.Instance?.audioExposeScam,
                AudioCue.InspectionFailed => EmporiumAudioPlayer.Instance?.audioFail,
                AudioCue.MachineUnscrew => EmporiumAudioPlayer.Instance?.ratchet,
                AudioCue.MachineCutWire => EmporiumAudioPlayer.Instance?.wirecutter,
                AudioCue.MachineWeld => EmporiumAudioPlayer.Instance?.welder,
                AudioCue.KeycardAccepted => EmporiumAudioPlayer.Instance?.audioClickClick,
                AudioCue.KeycardRejected => EmporiumAudioPlayer.Instance?.audioNegativeTwoBeep,
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
            case nameof(AudioCue.SecurityAlarm): QaSecurityAlarms++; break;
            case nameof(AudioCue.PrintLabels): QaLabelCues++; break;
            case nameof(AudioCue.InspectionVictory): QaInspectionVictoryCues++; break;
            case nameof(AudioCue.InspectionScam): QaInspectionScamCues++; break;
            case nameof(AudioCue.InspectionFailed): QaInspectionFailedCues++; break;
            case nameof(AudioCue.MachineUnscrew):
            case nameof(AudioCue.MachineCutWire):
            case nameof(AudioCue.MachineWeld): QaMachineSalvageCues++; break;
            case nameof(AudioCue.KeycardAccepted): QaKeycardAccepted++; break;
            case nameof(AudioCue.KeycardRejected): QaKeycardRejected++; break;
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

[HarmonyPatch(typeof(GunModHelper), nameof(GunModHelper.PlayEffect))]
internal static class NativeGunWorkshopAudio
{
    // Only the fixed original attachment cues travel with store state.
    // The shooting view and its local reload/aim feedback own their own audio.
    private static readonly (NativeWorld.AudioCue Cue, string Clip)[] Effects =
    {
        (NativeWorld.AudioCue.GunMetalMount, AudioHelper.MODDING1),
        (NativeWorld.AudioCue.GunMetalUnmount, AudioHelper.MODDING2),
        (NativeWorld.AudioCue.GunPlasticMount, AudioHelper.MODDING_PLASTIC1),
        (NativeWorld.AudioCue.GunPlasticUnmount, AudioHelper.MODDING_PLASTIC2),
        (NativeWorld.AudioCue.GunRailMount, AudioHelper.MOD_COCKING2_1),
        (NativeWorld.AudioCue.GunRailUnmount, AudioHelper.MOD_COCKING2_2),
        (NativeWorld.AudioCue.GunLightMagazine, AudioHelper.MOD_PISTOL_RELOAD_LIGHT),
        (NativeWorld.AudioCue.GunPistolMagazine, AudioHelper.MOD_PISTOL_RELOAD),
        (NativeWorld.AudioCue.GunSmgMagazine, AudioHelper.MOD_SMG_RELOAD),
    };

    private static bool InShop => MapUIManager.Instance is { } map && !map.IsOutside;

    private static void Postfix(string effectID, bool __runOriginal)
    {
        var host = NativeWorld.Host;
        if (!__runOriginal || host == null || !InShop) return;
        foreach (var (cue, clip) in Effects)
        {
            if (clip != effectID) continue;
            if (AudioManager.Instance?.CanPlay(clip) == true) host.RecordAudio(cue);
            return;
        }
    }

    internal static void Play(NativeWorld.AudioCue cue)
    {
        if (!InShop || AudioManager.Instance is not { } audio) return;
        foreach (var effect in Effects)
        {
            if (effect.Cue != cue) continue;
            audio.Play2(effect.Clip);
            return;
        }
    }
}

[HarmonyPatch]
internal static class NativeLocationAudio
{
    // null denotes the shop. Outside sound cues are audible only in their
    // original location, including when the two players travel separately.
    private static readonly (NativeWorld.AudioCue Cue, string Identifier, bool ThirdSource, string Location)[] Effects =
    {
        (NativeWorld.AudioCue.Blender, "blender", false, null),
        (NativeWorld.AudioCue.AutoBlender, "blender", true, null),
        (NativeWorld.AudioCue.ModuleInsert, "module_insert", true, null),
        (NativeWorld.AudioCue.ModuleRemove, "module_remove", true, null),
        (NativeWorld.AudioCue.EffectModuleInsert, "module_insert", false, null),
        (NativeWorld.AudioCue.BatteryInsert, "battery_insert", true, null),
        (NativeWorld.AudioCue.BatteryRemove, "battery_removed", true, null),
        (NativeWorld.AudioCue.Scanner, "scanner_activate", true, null),
        (NativeWorld.AudioCue.Motor, "turbo", false, null),
        (NativeWorld.AudioCue.Scavenge, "debris", false, AfterhourLocation.DUMPING_GROUND),
        (NativeWorld.AudioCue.ScavengeMinor, "debris_impact2", false, AfterhourLocation.DUMPING_GROUND),
        (NativeWorld.AudioCue.ScavengeMajor, "debris_impact", false, AfterhourLocation.DUMPING_GROUND),
        (NativeWorld.AudioCue.StoryPistolBurst, "pistol_burst", false, null),
        (NativeWorld.AudioCue.StoryGunshot, "gunshot", false, null),
        (NativeWorld.AudioCue.BombCountdown, "countdown", false, null),
        (NativeWorld.AudioCue.BombExplosion, "explosionDebris", false, null),
    };

    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(AudioManager), nameof(AudioManager.Play2));
        yield return AccessTools.Method(typeof(AudioManager), nameof(AudioManager.Play3));
    }

    private static void Postfix(MethodBase __originalMethod, string identifier, bool __result)
    {
        var world = NativeWorld.Host;
        if (!__result || world == null) return;
        bool thirdSource = __originalMethod.Name == nameof(AudioManager.Play3);
        foreach (var effect in Effects)
        {
            if (effect.Identifier != identifier || effect.ThirdSource != thirdSource) continue;
            if (IsInLocation(effect.Location)) world.RecordAudio(effect.Cue);
            return;
        }
    }

    internal static bool Play(NativeWorld.AudioCue cue)
    {
        var audio = AudioManager.Instance;
        if (audio == null) return false;
        foreach (var effect in Effects)
        {
            if (effect.Cue != cue) continue;
            if (!IsInLocation(effect.Location)) return false;
            return effect.ThirdSource ? audio.Play3(effect.Identifier) : audio.Play2(effect.Identifier);
        }
        return false;
    }

    private static bool IsInLocation(string location)
    {
        var map = MapUIManager.Instance;
        return map != null && (location == null ? !map.IsOutside : map.Current?.Id == location);
    }
}

[HarmonyPatch]
internal static class NativeLiquidAudio
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(AudioHelper), nameof(AudioHelper.PourWater));
        yield return AccessTools.Method(typeof(AudioHelper), nameof(AudioHelper.DumpWater));
        yield return AccessTools.Method(typeof(AudioHelper), nameof(AudioHelper.DropperDraw));
        yield return AccessTools.Method(typeof(AudioHelper), nameof(AudioHelper.DropperDrop));
        yield return AccessTools.Method(typeof(AudioHelper), nameof(AudioHelper.UseFaucet));
        yield return AccessTools.Method(typeof(AudioHelper), nameof(AudioHelper.UseWaterFilter));
    }

    private static void Postfix(MethodBase __originalMethod)
    {
        var host = NativeWorld.Host;
        if (host == null) return;
        var cue = __originalMethod.Name switch
        {
            nameof(AudioHelper.DumpWater) => NativeWorld.AudioCue.DumpWater,
            nameof(AudioHelper.DropperDraw) => NativeWorld.AudioCue.DropperDraw,
            nameof(AudioHelper.UseFaucet) => NativeWorld.AudioCue.UseFaucet,
            nameof(AudioHelper.UseWaterFilter) => NativeWorld.AudioCue.UseWaterFilter,
            // The original dropper release and liquid pour use the same clip.
            _ => NativeWorld.AudioCue.PourWater
        };
        host.RecordAudio(cue);
    }

    internal static void Play(NativeWorld.AudioCue cue)
    {
        switch (cue)
        {
            case NativeWorld.AudioCue.PourWater: AudioHelper.PourWater(); break;
            case NativeWorld.AudioCue.DumpWater: AudioHelper.DumpWater(); break;
            case NativeWorld.AudioCue.DropperDraw: AudioHelper.DropperDraw(); break;
            case NativeWorld.AudioCue.UseFaucet: AudioHelper.UseFaucet(); break;
            case NativeWorld.AudioCue.UseWaterFilter: AudioHelper.UseWaterFilter(); break;
        }
        if (NativeBackgroundQa.Enabled) Debug.Log("[probably_coop QA] replica audio " + cue);
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
        yield return AccessTools.Method(typeof(EmporiumAudioPlayer), nameof(EmporiumAudioPlayer.PlayBeatVictory));
        yield return AccessTools.Method(typeof(ItemFeature), nameof(ItemFeature.ExposeFeature));
        yield return AccessTools.Method(typeof(NegociationHelper), nameof(NegociationHelper.OnFeatureExposed));
        yield return AccessTools.Method(typeof(InspectionUIManager), nameof(InspectionUIManager.OnConfrontFailed));
        yield return AccessTools.Method(typeof(LabelerElement), nameof(LabelerElement.OnDropdownValueChanged));
        yield return AccessTools.Method(typeof(LabelerUIManager), nameof(LabelerUIManager.Repeat));
        yield return AccessTools.Method(typeof(AppraisalServiceHelper), nameof(AppraisalServiceHelper.GiveAppraisalFee));
        yield return AccessTools.Method(typeof(AugHelper), nameof(AugHelper.OnGoodKill));
        yield return AccessTools.Method(typeof(MachineBrokenHelper), nameof(MachineBrokenHelper.OnScrewdriverUsed));
        yield return AccessTools.Method(typeof(MachineBrokenHelper), nameof(MachineBrokenHelper.OnWireCutterUsed));
        yield return AccessTools.Method(typeof(MachineBrokenHelper), nameof(MachineBrokenHelper.OnWelderUsed));
        yield return AccessTools.Method(typeof(LockHelper), nameof(LockHelper.TryOpenLock));
        yield return AccessTools.Method(typeof(PhoneUIManager), nameof(PhoneUIManager.HandleCall));
        yield return AccessTools.Method(typeof(PhoneUIManager), nameof(PhoneUIManager.StartPhoneDialogSequence));
        yield return AccessTools.Method(typeof(PhoneUIManager), nameof(PhoneUIManager.OnPhoneDialogEnd));
        yield return AccessTools.Method(typeof(PhoneUIManager), nameof(PhoneUIManager.NoNumber));
        yield return AccessTools.Method(typeof(PhoneUIManager), nameof(PhoneUIManager.AutoCall));
    }

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code, MethodBase __originalMethod)
    {
        // Different named fields can point at the same original AudioSource.
        // Use the actual operation to identify printer and inspection results.
        string playback = __originalMethod.Name switch
        {
            nameof(LabelerElement.OnDropdownValueChanged) or nameof(LabelerUIManager.Repeat) => nameof(PlayLabels),
            nameof(EmporiumAudioPlayer.PlayBeatVictory) => nameof(PlayInspectionVictory),
            nameof(ItemFeature.ExposeFeature) or nameof(NegociationHelper.OnFeatureExposed) => nameof(PlayInspectionScam),
            nameof(InspectionUIManager.OnConfrontFailed) => nameof(PlayInspectionFailed),
            nameof(MachineBrokenHelper.OnScrewdriverUsed) => nameof(PlayMachineUnscrew),
            nameof(MachineBrokenHelper.OnWireCutterUsed) => nameof(PlayMachineCutWire),
            nameof(MachineBrokenHelper.OnWelderUsed) => nameof(PlayMachineWeld),
            nameof(LockHelper.TryOpenLock) => nameof(PlayKeycardResult),
            _ => null
        };
        return code.MethodReplacer(PlayAudio, playback == null ? PlayAndRecord :
            AccessTools.Method(typeof(NativeSharedAudioPlayback), playback));
    }

    internal static void PlayLabels(AudioSource source)
    {
        source.Play();
        if (ReplicaLabelInit.Depth == 0) NativeWorld.Host?.RecordAudio(NativeWorld.AudioCue.PrintLabels);
    }

    private static void PlayInspectionVictory(AudioSource source) => PlayKnownCue(source, NativeWorld.AudioCue.InspectionVictory);
    private static void PlayInspectionScam(AudioSource source) => PlayKnownCue(source, NativeWorld.AudioCue.InspectionScam);
    private static void PlayInspectionFailed(AudioSource source) => PlayKnownCue(source, NativeWorld.AudioCue.InspectionFailed);
    private static void PlayMachineUnscrew(AudioSource source) => PlayKnownCue(source, NativeWorld.AudioCue.MachineUnscrew);
    private static void PlayMachineCutWire(AudioSource source) => PlayKnownCue(source, NativeWorld.AudioCue.MachineCutWire);
    private static void PlayMachineWeld(AudioSource source) => PlayKnownCue(source, NativeWorld.AudioCue.MachineWeld);

    private static void PlayKeycardResult(AudioSource source)
    {
        // The opening is shared. A wrong-card beep belongs to the player
        // who used the key, even when the authority ran the native callback.
        if (source == EmporiumAudioPlayer.Instance.audioClickClick)
        {
            PlayKnownCue(source, NativeWorld.AudioCue.KeycardAccepted);
            return;
        }
        if (NativeWorld.Host is { } host && NativeActorSession.Current?.Run.CurrentActor == NativeActorRun.Slot.Partner)
            host.RecordAudio(NativeWorld.AudioCue.KeycardRejected);
        else
            source.Play();
    }

    private static void PlayKnownCue(AudioSource source, NativeWorld.AudioCue cue)
    {
        source.Play();
        NativeWorld.Host?.RecordAudio(cue);
    }

    internal static void Play(AudioSource source)
    {
        source.Play();
        var host = NativeWorld.Host;
        if (host == null) return;
        var phone = PhoneUIManager.Instance;
        var emporium = EmporiumAudioPlayer.Instance;
        if (source == NegociationUIManager.Instance?.audioSourceCashRegister) host.RecordAudio(NativeWorld.AudioCue.CashRegister);
        else if (source == emporium?.audioRegister) host.RecordAudio(NativeWorld.AudioCue.ShopRegister);
        else if (source == emporium?.alarm2) host.RecordAudio(NativeWorld.AudioCue.SecurityAlarm);
        else if (phone != null && source == phone.phoneClientPickup) host.RecordAudio(NativeWorld.AudioCue.PhoneAnswered);
        else if (phone != null && source == phone.phoneClientDrop) host.RecordAudio(NativeWorld.AudioCue.PhoneEnded);
        else if (phone != null && source == phone.phoneNoNumber) host.RecordAudio(NativeWorld.AudioCue.PhoneInvalid);
    }
}
