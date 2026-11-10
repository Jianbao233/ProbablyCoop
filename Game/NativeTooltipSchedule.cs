using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace ProbablyCoop;

internal sealed partial class NativeWorld
{
    // A speculative read belongs to the next publication only. First observations
    // and the partner's current read always run inside the authoritative capture.
    private const double MaxReadAge=0.18,BaseSlice=0.0015,MaxSlice=0.0035;
    private double secondsPerTicket=0.00018;
    private readonly List<TooltipTicket> tooltipTickets=new List<TooltipTicket>();
    private readonly Dictionary<GameItemElement,TooltipSample> prefetched=new Dictionary<GameItemElement,TooltipSample>();
    // A dense nested stash can contain hundreds of descriptions, even when
    // only a few items are visible. Keep the last native text for each item
    // and refresh it in small slices; the current remote read stays live.
    private const int DenseTooltipThreshold=128;
    private const double DenseReadBudget=0.00125;
    private const double DenseReadInterval=0.04;
    private sealed class DenseSample { internal TooltipSample Value; }
    private ConditionalWeakTable<GameItemElement,DenseSample> denseReads=new ConditionalWeakTable<GameItemElement,DenseSample>();
    private bool denseTooltips;
    private int denseReadIndex;
    private double nextDenseRead;
    private readonly HashSet<GraphNodeStorage> readVisited=new HashSet<GraphNodeStorage>();
    private readonly Stack<GraphNodeStorage> readParents=new Stack<GraphNodeStorage>();
    private int nextTicket;
    private NativeActorInventory.RootKey captureTooltipKey;
    private int qaWarmReads,qaFocusedReads;
    private bool mayUsePrefetch;
    private int profileReused,profileOpaque,profileCustom,profileFocused,profileMissing,profileExpired,profileContext,profileDetached;
    private string TooltipScheduleSummary()
    {
        string result="reused="+profileReused+" opaque="+profileOpaque+" custom="+profileCustom+" focused="+profileFocused+
            " missing="+profileMissing+" expired="+profileExpired+" context="+profileContext+" detached="+profileDetached;
        profileReused=profileOpaque=profileCustom=profileFocused=profileMissing=profileExpired=profileContext=profileDetached=0;
        return result;
    }
    private static readonly FieldInfo[] TooltipHooks={
        AccessTools.Field(typeof(ModHook),"OnCreateTooltipEarly"),AccessTools.Field(typeof(ModHook),"OnCreateTooltipLate"),
        AccessTools.Field(typeof(ModHook),"OnCreateDevTooltipEarly"),AccessTools.Field(typeof(ModHook),"OnCreateDevTooltipLate")
    };
    private static bool HasOpaqueTooltipHooks()
    {
        foreach(var hook in TooltipHooks) if(hook==null || hook.GetValue(null)!=null) return true;
        return false;
    }
    private readonly struct TooltipTicket
    {
        internal readonly GameItemElement Item;
        internal readonly GameItem RootItem;
        internal readonly NativeActorInventory.RootKey Key;
        internal TooltipTicket(GameItemElement item,GameItem root,NativeActorInventory.RootKey key) { Item=item; RootItem=root; Key=key; }
    }
    private readonly struct TooltipSample
    {
        internal readonly (RichText Normal,RichText Advanced) Pair;
        internal readonly double Time;
        private readonly GameInventory parent;
        private readonly GameItem root;
        private readonly object customer,location,barter;
        private readonly int count,day,cash;
        private readonly PlayerStore.StoreState phase;
        private readonly bool owned;
        internal TooltipSample(TooltipTicket ticket,(RichText,RichText) pair,double now)
        {
            Pair=pair; Time=now; var item=ticket.Item;
            root=ticket.RootItem; parent=item.parentInventory; count=item.unitCount; owned=GeneralHelper.IsItemOwned(item);
            var store=PlayerStore.Instance;
            day=StoreStation.Instance.dayCounter; cash=store.playerCash; phase=store.storeState;
            customer=store.currentClientInstance; location=MapUIManager.Instance.Current;
            barter=OverlayHandler.current.currentBarterCharacter;
        }
        internal bool Current(GameItem item,GameItem currentRoot,double now)
        {
            var store=PlayerStore.Instance;
            return now-Time<=MaxReadAge && SamePlacement(item,currentRoot) && day==StoreStation.Instance.dayCounter &&
                cash==store.playerCash && phase==store.storeState && ReferenceEquals(customer,store.currentClientInstance) &&
                ReferenceEquals(location,MapUIManager.Instance.Current) && ReferenceEquals(barter,OverlayHandler.current.currentBarterCharacter);
        }
        internal bool SamePlacement(GameItem item,GameItem currentRoot) =>
            ReferenceEquals(root,currentRoot) && ReferenceEquals(parent,item.parentInventory) &&
            count==item.unitCount && owned==GeneralHelper.IsItemOwned(item);
    }
    private static (RichText Normal,RichText Advanced) EvaluateTooltip(GameItemElement item)
    {
        using var actorScope=Host?.EnterPartnerOperation();
        using var timer=NativePerformance.Measure("host.tooltip_prepare");
        using var timing=NativeTooltipTiming.Begin();
        return (item.GetTooltip(),item.GetTooltipAdvanced());
    }
    private bool Focused(GameItem item) => remoteHover!=0 && Time.realtimeSinceStartupAsDouble<viewsExpiry && model.Id(item)==remoteHover;
    private (RichText Normal,RichText Advanced) ReadDenseTooltip(TooltipTicket ticket)
    {
        var saved=denseReads.GetOrCreateValue(ticket.Item);
        if(!Focused(ticket.Item) && saved.Value.SamePlacement(ticket.Item,ticket.RootItem))
        {
            if(NativePerformance.Detailed) profileReused++;
            return saved.Value.Pair;
        }
        using var timing=NativePerformance.Measure("host.in_capture_tooltip");
        var pair=EvaluateTooltip(ticket.Item);
        saved.Value=new TooltipSample(ticket,pair,Time.realtimeSinceStartupAsDouble);
        return pair;
    }
    private void RefreshDenseTooltips()
    {
        double now=Time.realtimeSinceStartupAsDouble;
        if(tooltipTickets.Count==0 || now<nextDenseRead) return;
        nextDenseRead=now+DenseReadInterval;
        using var timing=NativePerformance.Measure("host.tooltip_slice",NativePerformance.FrameWork.Tooltip);
        var end=Stopwatch.GetTimestamp()+(long)(Stopwatch.Frequency*DenseReadBudget);
        for(int i=0;i<Math.Min(16,tooltipTickets.Count);i++)
        {
            if(denseReadIndex>=tooltipTickets.Count) denseReadIndex=0;
            var ticket=tooltipTickets[denseReadIndex++];
            if(!Focused(ticket.Item) && Attached(ticket))
            {
                var pair=EvaluateTooltip(ticket.Item);
                denseReads.GetOrCreateValue(ticket.Item).Value=new TooltipSample(ticket,pair,Time.realtimeSinceStartupAsDouble);
            }
            if(Stopwatch.GetTimestamp()>=end) break;
        }
    }
    private (RichText Normal,RichText Advanced) ReadTooltipForCapture(GameItemElement item)
    {
        if(mayUsePrefetch && item.tooltipRichTextFunc==null && !Focused(item) && prefetched.TryGetValue(item,out var sample) &&
            sample.Current(item,captureRoot,Time.realtimeSinceStartupAsDouble))
        {
            if(NativePerformance.Detailed) profileReused++;
            if(NativeBackgroundQa.Enabled && ++qaWarmReads==64)
                UnityEngine.Debug.Log("[probably_coop QA] PASS paced-tooltip 64 native tooltip pairs consumed in their single scheduled publication");
            return sample.Pair;
        }
        if(NativePerformance.Detailed)
        {
            if(!mayUsePrefetch) profileOpaque++;
            else if(item.tooltipRichTextFunc!=null) profileCustom++;
            else if(Focused(item)) profileFocused++;
            else if(!prefetched.TryGetValue(item,out var prior)) profileMissing++;
            else if(Time.realtimeSinceStartupAsDouble-prior.Time>MaxReadAge) profileExpired++;
            else profileContext++;
        }
        if(NativeBackgroundQa.Enabled && Focused(item) && ++qaFocusedReads==1)
            UnityEngine.Debug.Log("[probably_coop QA] PASS focused-tooltip guest read bypassed speculative samples");
        using var captureRead=NativePerformance.Measure("host.in_capture_tooltip");
        return EvaluateTooltip(item);
    }
    private void PrefetchTooltipReads()
    {
        if(denseTooltips) { RefreshDenseTooltips(); return; }
        double now=Time.realtimeSinceStartupAsDouble;
        double remaining=nextCapture-now;
        if(remaining>MaxReadAge || nextTicket>=tooltipTickets.Count || HasOpaqueTooltipHooks()) return;
        using var timing=NativePerformance.Measure("host.tooltip_slice",NativePerformance.FrameWork.Tooltip);
        // Use the remaining frames to distribute cold native reads. A dense
        // shop otherwise spends the entire shortfall on the publication frame.
        double frameSeconds=Math.Max(1d/120,Math.Min(1d/30,Time.unscaledDeltaTime));
        int framesLeft=Math.Max(1,(int)Math.Ceiling(remaining/frameSeconds));
        double needed=(tooltipTickets.Count-nextTicket)*secondsPerTicket/framesLeft;
        double budget=Time.unscaledDeltaTime>0.05f ? BaseSlice : Math.Min(MaxSlice,Math.Max(BaseSlice,needed));
        long start=Stopwatch.GetTimestamp(),end=start+(long)(Stopwatch.Frequency*budget);
        int initialTicket=nextTicket;
        for(int i=0;i<32 && nextTicket<tooltipTickets.Count;i++)
        {
            var ticket=tooltipTickets[nextTicket++];
            if(ticket.Item.tooltipRichTextFunc==null && !Focused(ticket.Item))
            {
                if(Attached(ticket)) prefetched[ticket.Item]=new TooltipSample(ticket,EvaluateTooltip(ticket.Item),now);
                else if(NativePerformance.Detailed) profileDetached++;
            }
            if(Stopwatch.GetTimestamp()>=end) break;
        }
        double measured=(Stopwatch.GetTimestamp()-start)/(double)Stopwatch.Frequency/(nextTicket-initialTicket);
        secondsPerTicket=0.8*secondsPerTicket+0.2*Math.Min(0.002,measured);
    }
    private bool Attached(TooltipTicket ticket)
    {
        var root=PhysicalRoot(ticket.Key);
        if(root==null || ticket.Item.unitCount<=0 || ticket.Item.parentInventory==null || ticket.RootItem.parentInventory!=root ||
            !root.childItems.Contains(ticket.RootItem)) return false;
        if(ReferenceEquals(ticket.Item,ticket.RootItem)) return true;
        // A machine can eject an entire nested container between publications.
        // Skip missing paths and unusually large/cyclic graphs; capture handles them normally.
        readVisited.Clear(); readParents.Clear(); readParents.Push(ticket.Item);
        while(readParents.Count>0 && readVisited.Count<128)
        {
            var node=readParents.Pop();
            if(ReferenceEquals(node,ticket.RootItem)) return true;
            if(!readVisited.Add(node)) continue;
            foreach(var parent in node.parents) if(parent!=null) readParents.Push(parent);
        }
        return false;
    }
    private void ClearTooltipSchedule()
    {
        outstandingReads.Reset();
        tooltipTickets.Clear(); prefetched.Clear(); readParents.Clear(); readVisited.Clear(); nextTicket=0; readItem=null; fastRead=null;
        denseReads=new ConditionalWeakTable<GameItemElement,DenseSample>(); denseReadIndex=0; nextDenseRead=0; denseTooltips=false;
    }
    internal void InvalidateTooltipReads() => prefetched.Clear();
}
