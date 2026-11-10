using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace ProbablyCoop;

internal sealed partial class NativeWorld
{
    private readonly HashSet<uint> remoteViews=new HashSet<uint>();
    private double nextViews,viewsExpiry;
    private uint lastTool;
    private uint lastHover,remoteHover;
    private readonly ProbablyCoop.Core.TooltipReadOrder readOrder=new ProbablyCoop.Core.TooltipReadOrder();
    private uint publishedInterest,remoteInterest,servedInterest;
    private GameItemElement readItem;
    private double readUntil;
    internal void ObserveTooltip(GameItemElement item)
    {
        double now=Time.realtimeSinceStartupAsDouble;
        if(!ReferenceEquals(readItem,item) || now>=readUntil) SetReadTarget(item,now);
        readUntil=now+0.30;
    }
    private double readStarted;
    private void SetReadTarget(GameItemElement item,double now)
    {
        readItem=item; readStarted=now; fastRead=null;
        readOrder.Select(now);
    }
    internal void WindowVisibilityChanged() => nextViews=0;
    internal static bool InNativeItemTick;
    private void PublishViews()
    {
        if(host || !Ready) return;
        var tool=ItemMouseDragHandler.current?.GetCurrentItem() ?? ItemSelectHandler.current?.GetEquippedItem() ??
            HarmonyLib.AccessTools.Field(typeof(ItemSelectHandler),"currentItem").GetValue(ItemSelectHandler.current) as GameItem;
        uint toolId=model.ReplicaId(tool);
        double now=Time.realtimeSinceStartupAsDouble;
        if(readItem!=null && now>=readUntil) SetReadTarget(null,now);
        uint hover=model.ReplicaId(readItem);
        // A datagram may be lost or superseded by a full snapshot. Holding
        // still must not require moving off the item and back to get a new read.
        if(hover!=0 && readOrder.Renew(now,CurrentFastTooltip(readItem,false)!=null))
            readStarted=now;
        if(now<nextViews && toolId==lastTool && hover==lastHover && publishedInterest==readOrder.Interest) return;
        lastTool=toolId; lastHover=hover; publishedInterest=readOrder.Interest;
        nextViews=now+0.5;
        var ids=model.Objects.Where(p=>p.Value is PixelWindow w && w.IsVisible()).Select(p=>p.Key).Prepend(toolId).Where(id=>id!=0).Take(62).Prepend(publishedInterest).Prepend(hover).ToArray();
        var bytes=new byte[ids.Length*4];
        for(int i=0;i<ids.Length;i++) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i*4,4),ids[i]);
        link.PublishPresence(bytes);
    }
    private void ReceiveViews(byte[] bytes)
    {
        remoteViews.Clear(); remoteTools.Clear(); viewsExpiry=Time.realtimeSinceStartupAsDouble+3;
        remoteHover=bytes.Length>=8 ? BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0,4)) : 0;
        remoteInterest=bytes.Length>=8 ? BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4,4)) : 0;
        for(int offset=8;offset<bytes.Length;offset+=4)
        {
            uint id=BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset,4));
            if(!model.Objects.TryGetValue(id,out var value)) continue;
            if(value is GameItem) { if(remoteTools.Count<4) remoteTools.Add(id); continue; }
            if(value is not PixelWindow window) continue;
            remoteViews.Add(id);
            foreach(var item in window.parentItems)
                if(ReferenceEquals(item.contentWindow,window) && item.IsTag("TRACK_OPENED_TAG") && !item.IsTag("OPENED_TAG")) item.EnableTag("OPENED_TAG");
        }
    }
    internal bool ViewedRemotely(PixelWindow window) => Time.realtimeSinceStartupAsDouble<viewsExpiry && remoteViews.Contains(model.ReplicaId(window));
}

// Keep the periodic survey for windows managed outside WindowsHandler.
// Normal vanilla open/close operations publish once on the next room tick,
// even when neither the held tool nor the last heartbeat has changed.
[HarmonyPatch(typeof(WindowsHandler),nameof(WindowsHandler.TryAddWindow))]
internal static class NativeWindowInterestAdded
{
    private static void Postfix(bool __result) { if(__result) NativeWorld.Guest?.WindowVisibilityChanged(); }
}
[HarmonyPatch(typeof(WindowsHandler),nameof(WindowsHandler.TryRemoveWindow))]
internal static class NativeWindowInterestRemoved
{
    private static void Postfix(bool __result) { if(__result) NativeWorld.Guest?.WindowVisibilityChanged(); }
}

// Native callbacks mix simulation and readout refresh. Only their existing one-second
// scope sees aggregate viewing interest; host window toggles always see local visibility.
[HarmonyPatch(typeof(EmporiumEntry),nameof(EmporiumEntry.OneSecPrefixFunction))]
internal static class NativeItemTickBegin
{ private static void Postfix() { NativeWorld.InNativeItemTick=NativeWorld.Host!=null; } }
[HarmonyPatch(typeof(EmporiumEntry),"Update")]
internal static class NativeItemTickEnd
{ private static void Finalizer() { NativeWorld.InNativeItemTick=false; } }
[HarmonyPatch(typeof(PixelWindow),nameof(PixelWindow.IsVisible))]
internal static class NativeViewedMachine
{
    private static void Postfix(PixelWindow __instance,ref bool __result)
    { if(!__result && NativeWorld.InNativeItemTick && NativeWorld.Host?.ViewedRemotely(__instance)==true) __result=true; }
}
