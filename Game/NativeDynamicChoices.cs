using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Core;

namespace ProbablyCoop;

internal sealed partial class NativeWorld
{
    // Only an already validated, typed remote command can capture a native
    // callback. The wire carries an option index and lease, never a delegate.
    internal static NativeWorld RemoteUiExecution { get; private set; }
    private GameItem[] remoteChoiceOrigins=Array.Empty<GameItem>();
    private bool executingResponseChoice;
    private bool executingAnyChoice,choiceClosed;
    private readonly ChoiceLease choiceLease=new ChoiceLease();
    private sealed class NativeChoice
    {
        internal ulong Context;
        internal List<GameItem> Items;
        internal List<GameItem> Previews;
        internal GameGridScrollableInventory Detached;
        internal GameItem[] Origins;
        internal string[] Proofs;
        internal string Title,No;
        internal ButtonElement.Color Color;
        internal bool Disabled,Response;
        internal Func<GameItem,bool> Select;
        internal Action Cancel;
        internal NativeActorActivities.Lease Activity;
    }
    private NativeChoice nativeChoice;
    private ulong shownChoice,choiceAwaiting,choiceRequest;
    private JObject pendingChoice;
    private readonly object dynamicChoiceOwner=new object();

    private GameItem[] ChoiceOrigins(JObject command)
    {
        if((string)command["kind"]=="choice") return nativeChoice?.Origins ?? Array.Empty<GameItem>();
        // Other typed commands also use "id" for textual mail/event ids. Only
        // integer entity references belong to this graph, never coerce strings.
        return new[]{"owner","id","target","selected"}.Select(key=>command[key])
            .Where(value=>value?.Type==JTokenType.Integer && uint.TryParse(value.ToString(),out _))
            .Select(value=>(uint)value)
            .Where(id=>id!=0).Select(id=>model.Objects.TryGetValue(id,out var value) ? value as GameItem : null)
            .Where(item=>item?.parentInventory!=null).Distinct().ToArray();
    }
    internal void CaptureNativeChoice(List<GameItem> items,string title,Func<GameItem,bool> select,Action cancel,
        string no,ButtonElement.Color color,bool disabled,bool response)
    {
        if(items==null || items.Count>ChoiceLease.Limit || items.Any(item=>item==null))
            throw new InvalidDataException("Invalid bounded native choices");
        ResetDynamicChoice();
        var flow=new NativeChoice { Items=items.ToList(),Title=title,Select=select,Cancel=cancel,No=no,
            Color=color,Disabled=disabled,Response=response,Context=ObserveOperationContext(),Origins=remoteChoiceOrigins };
        // Native choice callbacks sometimes query parentInventory. Give only
        // unparented previews a detached native inventory; shared items stay put.
        var previews=items.Where(item=>item.parentInventory==null).ToList();
        flow.Previews=previews;
        if(previews.Count>0)
        {
            int width=Math.Max(24,previews.Max(item=>item.modifiedShape.width));
            int height=Math.Max(400,previews.Sum(item=>item.modifiedShape.height+1));
            if(width>128 || height>ChoiceLease.Limit*129) throw new InvalidDataException("Native choice shapes exceed bounded grid");
            flow.Detached=new GameGridScrollableInventory(width,12,height);
            flow.Detached.handler.gameObject.SetActive(false);
            flow.Detached.AddFastUnchecked(previews);
        }
        nativeChoice=flow;
        if(NativeActorSession.Current is NativeActorSession actors)
        {
            flow.Activity=actors.Activities.Begin(flow,"native-choice",NativeActorRun.Slot.Partner,
                ()=>flow.Context==ObserveOperationContext() && ChoiceOriginValid(flow),blocksCommit:true);
            if(flow.Select!=null) flow.Select=flow.Activity.Wrap(flow.Select);
            if(flow.Cancel!=null) flow.Cancel=flow.Activity.Wrap(flow.Cancel);
        }
        choiceLease.Open(flow.Context,items.Count,!disabled);
        nextCapture=0;
    }
    private static void DisposeChoice(NativeChoice flow)
    {
        if(flow==null) return;
        NativeActorSession.Current?.Activities.RetireSource(flow);
        foreach(var item in flow.Previews)
            if(item.parentInventory==null || ReferenceEquals(item.parentInventory,flow.Detached)) item.Destroy();
        if(flow.Detached?.handler!=null) UnityEngine.Object.Destroy(flow.Detached.handler.gameObject);
    }
    private void ResetDynamicChoice()
    {
        var old=nativeChoice; nativeChoice=null; choiceLease.Clear();
        DisposeChoice(old);
        pendingChoice=null; choiceAwaiting=shownChoice=choiceRequest=0;
        NativeLocalItemChooser.Close(dynamicChoiceOwner);
    }
    private bool ChoiceOriginValid(NativeChoice flow)
    {
        if(flow.Context!=ObserveOperationContext()) return false;
        if(flow.Origins.Any(item=>item.parentInventory==null || item.unitCount<1 || !Accessible(item.parentInventory))) return false;
        if(flow.Proofs==null) flow.Proofs=flow.Origins.Select(MoveToken).ToArray();
        return flow.Origins.Select(MoveToken).SequenceEqual(flow.Proofs);
    }
    private JObject CaptureDynamicChoice()
    {
        // A continuable native ending hides UI; it does not cancel the native
        // operation which owns a pending choice. Resume the same lease afterward.
        if(EndingActive) return new JObject();
        var flow=nativeChoice;
        if(flow==null) return new JObject();
        if(!ChoiceOriginValid(flow)) { ResetDynamicChoice(); return new JObject(); }
        var options=new JArray();
        foreach(var item in flow.Items)
        {
            if(item is not GameItemElement element) throw new InvalidDataException("Native choice has no visual element");
            options.Add(new JObject { ["label"]=item.name,["atlas"]=element.spriteAtlasPath,["sprite"]=element.spritePath,
                ["shape"]=NativeMirrorProbe.ChoiceShape(item.shape),
                ["normal"]=NativeMirrorProbe.TooltipData(element.GetTooltip()),
                ["advanced"]=NativeMirrorProbe.TooltipData(element.GetTooltipAdvanced()),
                ["enabled"]=!item.forceDisableUse && !item.forceDisableActivate &&
                    (!flow.Response || item.MayActivateSlotItem() && item.CanActivateSlotItem()) });
        }
        var data=new JObject { ["serial"]=choiceLease.Serial,["context"]=flow.Context,["title"]=flow.Title,
            ["no"]=flow.No,["color"]=(int)flow.Color,["disabled"]=flow.Disabled,["response"]=flow.Response,["options"]=options };
        if(data.ToString(Newtonsoft.Json.Formatting.None).Length>512*1024)
            throw new InvalidDataException("Native choice presentation too large");
        return data;
    }
    private ushort ExecuteChoice(JObject command)
    {
        var flow=nativeChoice;
        if(flow==null || !ChoiceOriginValid(flow)) { ResetDynamicChoice(); return (ushort)ShopResult.Stale; }
        int index=(int)command["index"];
        if(index>=0 && flow.Origins.Any(HandTouches)) return (ushort)ShopResult.Busy;
        if(index>=0 && index<flow.Items.Count && flow.Response &&
            (!flow.Items[index].MayActivateSlotItem() || !flow.Items[index].CanActivateSlotItem()))
            return (ushort)ShopResult.Unavailable;
        if(!choiceLease.Take((ulong)command["serial"],(ulong)command["context"],index)) return (ushort)ShopResult.Stale;
        nativeChoice=null; // Consume before executing, including exceptions/nested choices.
        bool keep=false;
        executingResponseChoice=flow.Response;
        executingAnyChoice=true; choiceClosed=false;
        try
        {
            if(index<0) flow.Cancel?.Invoke();
            else keep=flow.Select?.Invoke(flow.Items[index])!=true;
        }
        finally
        {
            executingResponseChoice=executingAnyChoice=false;
            if(keep && !choiceClosed && nativeChoice==null && flow.Context==ObserveOperationContext())
            {
                flow.Proofs=null; nativeChoice=flow;
                choiceLease.Open(flow.Context,flow.Items.Count,!flow.Disabled);
            }
            else DisposeChoice(flow);
            nextCapture=0;
        }
        return (ushort)ShopResult.Applied;
    }
    private void ApplyDynamicChoice(JObject data)
    {
        ulong serial=(ulong?)data["serial"] ?? 0;
        if(serial==0)
        {
            NativeLocalItemChooser.Close(dynamicChoiceOwner);
            shownChoice=choiceAwaiting=choiceRequest=0; pendingChoice=null; return;
        }
        if(serial==shownChoice) return;
        if((ulong)data["context"]!=observedOperationContext || data["options"] is not JArray options || options.Count>ChoiceLease.Limit)
            throw new InvalidDataException("Invalid native choice projection");
        NativeLocalItemChooser.Close(dynamicChoiceOwner);
        shownChoice=serial; choiceAwaiting=choiceRequest=0; pendingChoice=null;
        var choices=options.Cast<JObject>().Select((option,index)=>new NativeLocalItemChooser.Choice {
            Label=(string)option["label"],Atlas=(string)option["atlas"],Sprite=(string)option["sprite"],
            Shape=NativeMirrorProbe.ReadChoiceShape((JObject)option["shape"]),
            Normal=ReadTooltip((JObject)option["normal"]),Advanced=ReadTooltip((JObject)option["advanced"]),
            Enabled=(bool)option["enabled"],Select=()=>{ QueueChoice(serial,index); return false; } }).ToList();
        NativeLocalItemChooser.Show(dynamicChoiceOwner,(string)data["title"],choices,()=>QueueChoice(serial,-1),
            (string)data["no"],(ButtonElement.Color)(int)data["color"],(bool)data["disabled"],(bool)data["response"]);
    }
    private void QueueChoice(ulong serial,int index)
    {
        if(serial!=shownChoice || choiceAwaiting==serial || pendingChoice!=null) return;
        pendingChoice=new JObject { ["kind"]="choice",["serial"]=serial,["index"]=index,["context"]=observedOperationContext };
        choiceAwaiting=serial;
    }
    private void FlushDynamicChoice()
    {
        if(pendingChoice==null || UiPending("choice") || !Ready) return;
        if(SubmitDirect(pendingChoice)) { pendingChoice=null; choiceRequest=LastSubmittedUiRequest; }
    }
    private void ObserveChoiceReply(ulong serial,ushort result)
    {
        if(choiceRequest==0 || serial!=choiceRequest) return;
        if(result!=(ushort)ShopResult.Applied) choiceAwaiting=0;
        choiceRequest=0;
    }

    [HarmonyPatch(typeof(OverlayHandler),nameof(OverlayHandler.ShowChooseItem),new[]{typeof(List<GameItem>),typeof(string),
        typeof(Func<GameItem,bool>),typeof(Action),typeof(string),typeof(ButtonElement.Color),typeof(bool)})]
    private static class RemoteChooseItems
    {
        private static bool Prefix(List<GameItem> items,string title,Func<GameItem,bool> chooseFunc,Action noAction,
            string no,ButtonElement.Color noColor,bool isNoDisabled)
        {
            if(RemoteUiExecution==null) return true;
            RemoteUiExecution.CaptureNativeChoice(items,title,chooseFunc,noAction,no,noColor,isNoDisabled,false);
            return false;
        }
    }
    [HarmonyPatch(typeof(EmporiumEntry),nameof(EmporiumEntry.ShowResponseWindow))]
    private static class RemoteResponseItems
    {
        private static bool Prefix(StoreResponse response)
        {
            if(RemoteUiExecution==null) return true;
            var choices=response.choices.ToList();
            RemoteUiExecution.CaptureNativeChoice(choices.Select(pair=>pair.Item1).ToList(),response.title,item=>
            { choices.First(pair=>ReferenceEquals(pair.Item1,item)).Item2?.Invoke(); return true; },null,
                "Cancel",ButtonElement.Color.Red,!response.closable,true);
            return false;
        }
    }
    [HarmonyPatch(typeof(EmporiumEntry),nameof(EmporiumEntry.HideResponseWindow))]
    private static class RemoteResponseClose
    {
        private static bool Prefix()
        {
            var world=RemoteUiExecution;
            if(world==null) return true;
            if(world.executingResponseChoice) world.choiceClosed=true;
            else if(world.nativeChoice?.Response==true) world.ResetDynamicChoice();
            return false;
        }
    }
    [HarmonyPatch(typeof(OverlayHandler),nameof(OverlayHandler.HideChooseItem))]
    private static class RemoteOverlayClose
    {
        private static bool Prefix()
        {
            var world=RemoteUiExecution;
            if(world==null) return true;
            if(world.executingAnyChoice && !world.executingResponseChoice) world.choiceClosed=true;
            else if(world.nativeChoice?.Response==false) world.ResetDynamicChoice();
            return false;
        }
    }
}
