using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ProbablyCoop;

// Presentation follows native playback, independently of the business phase.
// Sequences scope one-shot cues to this world. A new guest uses the first
// snapshot as its baseline and resumes the pose without replaying old cues.
internal readonly struct NativeShopPresentation
{
    private static readonly AccessTools.FieldRef<StoreShutterButton,bool> Closed =
        AccessTools.FieldRefAccess<StoreShutterButton,bool>("isClosed");
    private static readonly AccessTools.FieldRef<StoreShutterButton,float> Elapsed =
        AccessTools.FieldRefAccess<StoreShutterButton,float>("elapsedTime");
    private static readonly AccessTools.FieldRef<StoreShutterButton,float> Duration =
        AccessTools.FieldRefAccess<StoreShutterButton,float>("duration");

    internal readonly ulong Bell, Shutter;
    internal readonly bool ShutterClosed, Animated;
    internal readonly float OpenFraction;

    private NativeShopPresentation(ulong bell, ulong shutter, bool closed, bool animated, float openFraction)
    {
        Bell=bell; Shutter=shutter; ShutterClosed=closed; Animated=animated; OpenFraction=openFraction;
    }

    internal static JObject Capture(ulong bell, ulong shutter, bool animated)
    {
        var door=StoreShutterButton.Instance;
        return new JObject {
            ["bell"]=bell, ["shutter"]=shutter, ["closed"]=door==null || Closed(door),
            ["animated"]=animated, ["openFraction"]=door==null ? 0 : Fraction(door)
        };
    }

    internal static NativeShopPresentation Read(JToken data)
    {
        if(data is not JObject cues || cues["bell"]?.Type!=JTokenType.Integer ||
            cues["shutter"]?.Type!=JTokenType.Integer || cues["closed"]?.Type!=JTokenType.Boolean ||
            cues["animated"]?.Type!=JTokenType.Boolean || cues["openFraction"]?.Type is not (JTokenType.Float or JTokenType.Integer))
            throw new InvalidDataException("Invalid native shop presentation");
        float fraction=(float)cues["openFraction"];
        if(float.IsNaN(fraction) || fraction<0 || fraction>1)
            throw new InvalidDataException("Invalid native shutter position");
        return new NativeShopPresentation((ulong)cues["bell"],(ulong)cues["shutter"],
            (bool)cues["closed"],(bool)cues["animated"],fraction);
    }

    internal static float Fraction(StoreShutterButton door)
    {
        // The original blocker drives the visible front-window mask.
        var path=door.blockerEndPosition-door.blockerStartPosition;
        return path.sqrMagnitude>0 ? Mathf.Clamp01(Vector3.Dot(
            door.blockerGameObject.transform.localPosition-door.blockerStartPosition,path)/path.sqrMagnitude) : 0;
    }

    internal void ApplyShutter(StoreShutterButton door, bool play)
    {
        float fraction=play ? Fraction(door) : OpenFraction;
        if(play)
        {
            Closed(door)=!ShutterClosed;
            door.ToggleShutterDoor();
        }
        Closed(door)=ShutterClosed;
        Elapsed(door)=Duration(door)*(ShutterClosed ? 1-fraction : fraction);
        door.shutterGameObject.transform.position=Vector3.Lerp(door.startPosition,door.endPosition,fraction);
        door.blockerGameObject.transform.localPosition=Vector3.Lerp(door.blockerStartPosition,door.blockerEndPosition,fraction);
        door.blockerMaskObject.gameObject.SetActive(ShutterClosed || fraction<1);
    }
}

internal sealed partial class NativeWorld
{
    private ulong bellCue, shutterCue;
    private bool shutterAnimated;
    internal NativeShopPresentation ShopPresentation { get; private set; }

    internal void RecordBellCue()
    {
        bellCue++;
        nextCapture=0;
    }

    internal void RecordShutterCue(bool animated)
    {
        shutterCue++;
        shutterAnimated=animated;
        nextCapture=0;
    }
}

[HarmonyPatch(typeof(StoreBell), nameof(StoreBell.RingBell))]
internal static class NativeBellPlayback
{
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
        NativeActorPersistence.ReplaceOne(instructions,
            AccessTools.Method(typeof(AudioSource),nameof(AudioSource.Play),Type.EmptyTypes),
            AccessTools.Method(typeof(NativeBellPlayback),nameof(Play)), "native store bell audio");

    private static void Play(AudioSource source)
    {
        source.Play();
        var host=NativeWorld.Host;
        if(host==null) return;
        host.RecordBellCue();
        if(NativeBackgroundQa.Enabled)
            Debug.Log($"[probably_coop QA] host native bell playing={source.isPlaying} clip={source.clip!=null}");
    }
}

[HarmonyPatch(typeof(StoreShutterButton), nameof(StoreShutterButton.ToggleShutterDoor))]
internal static class NativeShutterPlayback
{
    private static void Postfix(StoreShutterButton __instance, bool __runOriginal)
    {
        if(__runOriginal && __instance.shutterGameObject!=null) NativeWorld.Host?.RecordShutterCue(true);
    }
}

[HarmonyPatch(typeof(StoreShutterButton), nameof(StoreShutterButton.ForceImmediateClose))]
internal static class NativeShutterReset
{
    private static void Postfix(bool __runOriginal)
    {
        if(__runOriginal) NativeWorld.Host?.RecordShutterCue(false);
    }
}
