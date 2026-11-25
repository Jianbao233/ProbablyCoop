using System.Collections.Generic;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Core;
using UnityEngine;

namespace ProbablyCoop;

[HarmonyPatch(typeof(StoreBedroll), nameof(StoreBedroll.OnClick))]
internal static class GuestBedrollClick
{
    private static bool Prefix()
    {
        if (!NativeGuest.Active) return true;
        NativeWorld.Guest?.RequestBedrollBell();
        return false;
    }
}

[HarmonyPatch(typeof(StoreBedroll), nameof(StoreBedroll.GetTooltip))]
internal static class GuestBedrollTooltip
{
    private static void Postfix(ref string __result)
    {
        if (NativeGuest.Active)
            __result = ModHelper.GetLocalized("probably_coop", "day_bedroll_bell");
    }
}

internal sealed partial class NativeWorld
{
    private ulong pendingBedrollContext, pendingBedrollClicks;
    private readonly List<ulong> bedrollReceipts = new List<ulong>();
    internal int QaBedrollBells { get; private set; }

    internal void RequestBedrollBell()
    {
        if (closed || host || !Ready || link == null || observedOperationContext == 0 ||
            MapUIManager.Instance?.IsOutside != false || MapUIManager.Instance.IsInTransit) return;
        if (pendingBedrollContext != observedOperationContext)
        {
            pendingBedrollContext = observedOperationContext;
            pendingBedrollClicks = 0;
        }
        pendingBedrollClicks++;
        FlushBedrollBells();
    }

    private void FlushBedrollBells()
    {
        if (link == null) return;
        for (int i = bedrollReceipts.Count - 1; i >= 0; i--)
            if (link.TryTakeIndependentResult(bedrollReceipts[i], out _)) bedrollReceipts.RemoveAt(i);

        if (pendingBedrollContext != observedOperationContext) pendingBedrollClicks = 0;
        while (pendingBedrollClicks != 0)
        {
            if (!SubmitHand(new JObject { ["kind"] = "bedrollBell", ["context"] = pendingBedrollContext }, out ulong serial)) break;
            bedrollReceipts.Add(serial);
            pendingBedrollClicks--;
        }
    }

    private ushort ReceiveBedrollBell(JObject command)
    {
        if (!ValidOperationContext(command)) return (ushort)ShopResult.Stale;
        if (shop.BlocksWorldCommands || EndingActive ||
            MapUIManager.Instance?.IsOutside != false || MapUIManager.Instance.IsInTransit)
            return (ushort)ShopResult.Unavailable;

        var audio = Object.FindObjectOfType<StoreBell>()?.audioSource;
        if (audio == null || audio.clip == null) return (ushort)ShopResult.Unavailable;
        audio.PlayOneShot(audio.clip);
        if (NativeBackgroundQa.Enabled) QaBedrollBells++;
        return (ushort)ShopResult.Applied;
    }
}
