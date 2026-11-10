using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Core;

namespace ProbablyCoop;

internal sealed partial class NativeWorld
{
    private static string HashData(JObject data)
    {
        // Exclude only the root proof, preserving the exact prior canonical property
        // order and child proofs without cloning the entire nested UI graph.
        using var text=new System.IO.StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        using(var writer=new JsonTextWriter(text) { Formatting=Formatting.None })
        {
            writer.WriteStartObject();
            foreach(var property in data.Properties()) if(property.Name!="token") property.WriteTo(writer);
            writer.WriteEndObject();
        }
        using var sha=SHA256.Create(); return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString())));
    }
    internal bool TryData(object value,out JObject data) => model.Data.TryGetValue(model.ReplicaId(value),out data);
    private readonly MoveProofCache moveProofs=new MoveProofCache();
    private readonly byte[] moveShapeBuffer=new byte[128*128];
    private string MoveToken(GameItem item) => MoveToken(item, model.Id(item), model.Id(item.parentInventory));
    private string ReplicaMoveToken(GameItem item) => MoveToken(item, model.ReplicaId(item), model.ReplicaId(item.parentInventory));
    private string MoveToken(GameItem item, uint id, uint parent)
    {
        // Preserve the original proof exactly, while avoiding JSON/SHA allocation
        // for repeated contents/sort/transfer queries on the same unchanged item.
        int quantity=item.unitCount; bool owned=GeneralHelper.IsItemOwned(item);
        var shape=item.modifiedShape;
        var state=new MoveProofCache.State(id,parent,quantity,owned,
            shape.minX,shape.minY,shape.orientation,shape.flipped,shape.width,shape.height,shape.GetOutsideBounds());
        if(state.Width<0 || state.Height<0 || state.Width>128 || state.Height>128)
            throw new System.IO.InvalidDataException("Invalid move proof shape");
        int count=0;
        for(int y=0;y<state.Height;y++) for(int x=0;x<state.Width;x++) moveShapeBuffer[count++]=shape.GetLocal(x,y);
        var cells=moveShapeBuffer.AsSpan(0,count);
        if(moveProofs.TryGet(state,cells,out var proof)) return proof;
        var projectedCells=new JArray(); for(int i=0;i<count;i++) projectedCells.Add(moveShapeBuffer[i]);
        proof=HashData(new JObject {
            ["id"]=state.Id,["parent"]=state.Parent,["quantity"]=state.Quantity,["owned"]=state.Owned,
            ["x"]=state.X,["y"]=state.Y,["rotation"]=state.Rotation,["flip"]=state.Flip,
            ["shape"]=new JObject { ["w"]=state.Width,["h"]=state.Height,["outside"]=state.Outside,["cells"]=projectedCells } });
        moveProofs.Put(state,cells,proof);
        return proof;
    }
    private GameItem capturedControlOwner;
    private string capturedControlOwnerProof;
    private int verifiedControlOwners;
    private string OwnerControlProof(GameItem owner)
    {
        using var timing=NativePerformance.Measure("host.control_owner");
        return HashData(new JObject { ["owner"]=model.Id(owner),["parent"]=model.Id(owner.parentInventory),
            ["owned"]=GeneralHelper.IsItemEffectivelyOwned(owner),["quantity"]=owner.unitCount,
            ["scene"]=MapUIManager.Instance.Current?.Id ?? "",
            ["contents"]=new JArray(GraphUtils.FindAllChildrenType<GameItem>(owner).Select(i=>new JObject {
                ["id"]=model.Id(i),["parent"]=model.Id(i.parentInventory),["quantity"]=i.unitCount })) });
    }
    private string CaptureControlToken(object control,GameItem owner)
    {
        // The visual tree is visited depth-first for one top-level item at a
        // time. Only share its proof within this publication, never with input.
        if(!ReferenceEquals(owner,capturedControlOwner))
        {
            capturedControlOwnerProof=OwnerControlProof(owner);
            capturedControlOwner=owner;
        }
        else if(NativeBackgroundQa.Enabled && verifiedControlOwners<32)
        {
            if(capturedControlOwnerProof!=OwnerControlProof(owner)) throw new InvalidOperationException("Equipment contents changed during control capture");
            if(++verifiedControlOwners==32) UnityEngine.Debug.Log("[probably_coop QA] PASS shared-control-owner 32 native control observations matched fresh ordered contents");
        }
        return ControlToken(control,capturedControlOwnerProof);
    }
    private string ControlToken(object control,string ownerProof)
    {
        var data=new JObject { ["id"]=model.Id(control),["equipment"]=ownerProof };
        if(control is MachineryButton b) { data["disabled"]=b.isDisabled; data["hidden"]=b.isHidden; }
        if(control is MachineryRadioList r) data["radio"]=new JArray(r.group.buttons.Select(b=>new JObject { ["on"]=b.isOn,["disabled"]=b.isDisabled,["hidden"]=b.isHidden }));
        if(control is MachineryRadioButton toggle)
        { data["on"]=toggle.isOn; data["disabled"]=toggle.isDisabled; data["hidden"]=toggle.isHidden; }
        if(control is MachinerySlider s) data["slider"]=new JArray(s.min,s.max,s.step,s.value,s.isDisabled);
        if(control is MachineryRangeSlider range) data["range"]=new JArray(range.min,range.max,range.step,range.low,range.high,range.isDisabled);
        return HashData(data);
    }
    internal void RequestControl(MachineryButton button)
    {
        if(!TryData(button,out var data) || button.isDisabled || button.isHidden) return;
        uint owner=(uint)data["owner"];
        if(!model.Data.TryGetValue(owner,out var root)) return;
        Submit(new JObject { ["kind"]="control",["id"]=(uint)data["entity"],["owner"]=owner,["token"]=(string)data["controlToken"] });
    }
    internal void RequestRadio(MachineryRadioList radio,int index)
    {
        var intent=RadioIntent(radio,index);
        SubmitRadioIntent(radio,intent,index);
    }
    internal void SubmitRadioIntent(MachineryRadioList radio,JObject intent,int index) => PreviewSelection(radio,intent,index);
    internal JObject RadioIntent(MachineryRadioList radio,int index)
    {
        if(!TryData(radio,out var data) || radio.Get(index) is not MachineryRadioButton button || button.isDisabled || button.isHidden) return null;
        uint owner=(uint)data["owner"]; if(!model.Data.ContainsKey(owner)) return null;
        return new JObject { ["kind"]="control",["id"]=(uint)data["entity"],["owner"]=owner,["token"]=(string)data["controlToken"],["index"]=index };
    }
    private void RequestToggle(MachineryRadioButton toggle,bool desired)
    {
        if(toggle.group!=null) return;
        PreviewSelection(toggle,ToggleIntent(toggle,desired),desired ? 1 : 0);
    }
    private JObject ToggleIntent(MachineryRadioButton toggle,bool desired) =>
        TryData(toggle,out var data) && toggle.group==null && !toggle.isDisabled && !toggle.isHidden
            ? new JObject { ["kind"]="control",["id"]=(uint)data["entity"],["owner"]=(uint)data["owner"],
                ["token"]=(string)data["controlToken"],["on"]=desired } : null;
    private readonly ActivityRequests uiRequests=new ActivityRequests();
    internal ulong LastSubmittedUiRequest { get; private set; }
    internal ulong LastUiCompletion { get; private set; }
    internal ushort LastUiResult { get; private set; }
    internal bool LastUiResultFromProjection { get; private set; }
    internal bool HasUiRequests=>uiRequests.Count!=0;
    internal ActivityRequests.Entry UiRequestState(ulong serial)=>uiRequests.Entries.FirstOrDefault(entry=>entry.Serial==serial);
    private bool UiPending(string kind)=>uiRequests.Pending(kind);
    private bool MachinePending(uint owner)=>uiRequests.PendingActivity("control/"+owner);
    private static string UiActivity(JObject command)=>(string)command["kind"] switch {
        "control"=>"control/"+(uint)command["owner"],
        "frontend"=>"frontend/"+(int)command["type"]+"/"+(ulong)command["view"],
        _=>(string)command["kind"] };
    private readonly CommandProjectionTiming commandProjection=new CommandProjectionTiming {
        Completed=milliseconds=>NativePerformance.Sample("guest.command_to_projection",milliseconds),
        Slow=NativePerformance.SlowProjection
    };
    private static double CommandClock()=>System.Diagnostics.Stopwatch.GetTimestamp()*1000d/System.Diagnostics.Stopwatch.Frequency;
    private double nextCommandNotice;
    private bool SubmitDirect(JObject command)
    {
        SealOperation(command);
        if(link!=null) CheckCommandReply();
        if(!host && RequiresQuietUi(command) && HasPendingWork)
        {
            CommandNotice("请等待当前操作确认后再切换经营阶段或共同出行。");
            return false;
        }
        double started=CommandClock();
        var frontend=PlanFrontend(command);
        ulong issued=0;
        bool accepted=!closed && !host && Ready && link!=null && uiRequests.HasRoom &&
            link.SubmitIndependent(Encoding.UTF8.GetBytes(command.ToString(Formatting.None)),UnityEngine.Time.realtimeSinceStartupAsDouble,out issued);
        if(accepted)
        {
            // Read the issued id from the transport out parameter, never from
            // a mutable global "current request" shared with other activities.
            LastSubmittedUiRequest=issued;
            if(!uiRequests.Track(issued,(string)command["kind"],UiActivity(command),started))
                throw new InvalidOperationException("Native activity request tracking failed");
            commandProjection.Sent(issued,started,(string)command["kind"],applied);
            BeginFrontendPrediction(frontend,issued);
        }
        else CommandNotice("待确认的操作已满，请稍后再试。");
        return accepted;
    }
    private void CommandNotice(string message)
    {
        double now=UnityEngine.Time.realtimeSinceStartupAsDouble;
        if(now<nextCommandNotice) return; nextCommandNotice=now+1;
        StoreUIManager.Instance?.Notify(message);
    }
    private void CheckCommandReply(bool fromProjection=false)
    {
        if(link==null) return;
        foreach(var entry in uiRequests.Entries.ToArray())
        {
            if(entry.Result.HasValue || !link.TryTakeIndependentResult(entry.Serial,out ushort result) ||
                !uiRequests.Reply(entry.Serial,result,out _)) continue;
            double now=CommandClock();
            commandProjection.Reply(entry.Serial,result==(ushort)ShopResult.Applied,now);
            NativePerformance.Sample(fromProjection ? "guest.command_receipt_from_snapshot" :
                result==(ushort)ShopResult.Applied ? "guest.command_reply_applied" : "guest.command_reply_rejected",now-entry.Started);
            if(entry.Serial>LastUiCompletion)
            { LastUiCompletion=entry.Serial; LastUiResult=result; LastUiResultFromProjection=fromProjection; }
            if(!entry.Active) continue;
            ObserveChoiceReply(entry.Serial,result);
            ObservePhoneReply(entry.Serial,result);
            if(result==(ushort)ShopResult.Applied) continue;
            ReconcileSelections(0,entry.Serial);
            CommandNotice((ShopResult)result switch {
                ShopResult.Stale=>"物品或报价已发生变化，请根据最新状态重试。",
                ShopResult.NoSpace=>"目标位置空间不足。",
                ShopResult.Busy=>"此操作暂时不可用，请稍后再试。",
                _=>"操作未完成，已保留房主确认的状态。" });
        }
        uiRequests.Collect();
        ReconcileContinuous(0);
        ReconcileFrontends(0);
    }
    internal bool RequestMove(SlotMarker slot,int amount)
    {
        if(slot.item==null || slot.inventory==null || slot.itemGridShape==null || !TryData(slot.item,out var item)) return false;
        uint destination=model.ReplicaId(slot.inventory);
        if(destination==0) return false;
        if(amount<0) amount=slot.numTransfer;
        var shape=slot.itemGridShape;
        return Submit(new JObject { ["kind"]="move",["id"]=(uint)item["entity"],["token"]=(string)item["moveToken"],
            ["destination"]=destination,["target"]=model.ReplicaId(slot.targetItem),["count"]=amount,
            ["x"]=shape.minX,["y"]=shape.minY,["rotation"]=shape.orientation,["flip"]=shape.flipped });
    }
    private bool CurrentItem(JObject command,out GameItem item)
    {
        item=null;
        if(!model.Objects.TryGetValue((uint)command["id"],out var obj) || (item=obj as GameItem)==null || item.parentInventory==null || item.unitCount<1) return false;
        return MoveToken(item)==(string)command["token"];
    }
    private ushort ExecuteMove(JObject command)
    {
        if(shop.Settling) return (ushort)ShopResult.Busy;
        if(!CurrentItem(command,out var item)) return (ushort)ShopResult.Stale;
        if(HandTouches(item)) return (ushort)ShopResult.Busy;
        if(!model.Objects.TryGetValue((uint)command["destination"],out var obj) || obj is not GameInventory destination) return (ushort)ShopResult.Stale;
        if(HandDestination(destination)) return (ushort)ShopResult.Busy;
        if(!Accessible(item.parentInventory) || !Accessible(destination)) return (ushort)ShopResult.Unavailable;
        int amount=(int)command["count"],x=(int)command["x"],y=(int)command["y"],rotation=(int)command["rotation"];
        if(amount<1 || amount>item.unitCount || amount>item.MaxNumRemove() || !item.MayRemove() ||
            rotation<0 || rotation>3 || x<0 || x>127 || y<0 || y>127 || !GeneralHelper.MayPlayerInsertInto(destination)) return (ushort)ShopResult.Unavailable;
        var shape=new GridShapeBuilder(item.modifiedShape); shape.SetTransform(x,y,(bool)command["flip"],rotation);
        var slot=destination.TryInventorySlot(item,amount,shape,item.modifiedState);
        if(slot?.IsValid()!=true || slot.numTransfer<amount) return (ushort)ShopResult.NoSpace;
        if((uint)command["target"]!=model.Id(slot.targetItem)) return (ushort)ShopResult.Stale;
        if(HandTouches(slot.targetItem)) return (ushort)ShopResult.Busy;
        int moved=slot.TryAcceptOnce(amount);
        nextCapture=0; return (ushort)(moved==amount ? ShopResult.Applied : ShopResult.Failed);
    }
    private ushort ExecuteControl(JObject command)
    {
        if(shop.Settling) return (ushort)ShopResult.Busy;
        uint owner=(uint)command["owner"];
        if(!model.Objects.TryGetValue(owner,out var obj) || obj is not GameItem item || item.parentInventory==null || !Accessible(item.parentInventory) || !GeneralHelper.IsItemEffectivelyOwned(item)) return (ushort)ShopResult.Unavailable;
        if(HandTouches(item)) return (ushort)ShopResult.Busy;
        uint id=(uint)command["id"];
        if(!model.Objects.TryGetValue(id,out obj) || !model.Data.TryGetValue(id,out var control) || (uint)control["owner"]!=owner) return (ushort)ShopResult.Unavailable;
        if(ControlToken(obj,OwnerControlProof(item))!=(string)command["token"]) return (ushort)ShopResult.Stale;
        if(obj is MachinerySlider slider)
        {
            float value=(float)command["value"];
            if(slider.isDisabled || float.IsNaN(value) || float.IsInfinity(value) || value<slider.min || value>slider.max) return (ushort)ShopResult.Unavailable;
            slider.SetValue(value,true); slider.onReleased?.Invoke(slider.value); nextCapture=0; return (ushort)ShopResult.Applied;
        }
        if(obj is MachineryRangeSlider range)
        {
            float low=(float)command["low"],high=(float)command["high"];
            if(range.isDisabled || float.IsNaN(low) || float.IsNaN(high) || float.IsInfinity(low) || float.IsInfinity(high) || low<range.min || high>range.max || low>high) return (ushort)ShopResult.Unavailable;
            range.SetValues(low,high,true); range.onReleased?.Invoke(range.low,range.high); nextCapture=0; return (ushort)ShopResult.Applied;
        }
        if(obj is MachineryRadioList radio)
        {
            int index=(int?)command["index"] ?? -1;
            if(index<0 || index>=radio.group.buttons.Count || radio.Get(index).isDisabled || radio.Get(index).isHidden) return (ushort)ShopResult.Unavailable;
            radio.Select(index,true); nextCapture=0; return (ushort)ShopResult.Applied;
        }
        if(obj is MachineryRadioButton toggle)
        {
            if(toggle.group!=null || toggle.isDisabled || toggle.isHidden || command["on"]?.Type!=JTokenType.Boolean)
                return (ushort)ShopResult.Unavailable;
            bool desired=(bool)command["on"];
            if(toggle.isOn==desired) return (ushort)ShopResult.Stale;
            toggle.SetOn(desired,true); nextCapture=0; return (ushort)ShopResult.Applied;
        }
        if(obj is not MachineryButton button || button.isDisabled || button.isHidden) return (ushort)ShopResult.Unavailable;
        // Only the existing native control's callback is invoked, never a name/delegate from the wire.
        var callback=(Action)AccessTools.Field(typeof(MachineryButton),"callback").GetValue(button);
        if(callback==null) return (ushort)ShopResult.Unavailable;
        callback(); nextCapture=0; return (ushort)ShopResult.Applied;
    }
}

[HarmonyPatch(typeof(SlotMarker),nameof(SlotMarker.TryAcceptOnce))]
internal static class ReplicaNativeDrop
{
    private static bool Prefix(SlotMarker __instance,int amount,ref int __result)
    {
        if(__instance.item is GameItemElement local && NativeLocalItemChooser.Owns(local)) return true;
        if(!NativeGuest.Active)
        {
            if(!NativeHandTransferInput.BlocksHostSlot(__instance)) return true;
            __result=0; return false;
        }
        if(__instance.IsValid()) NativeWorld.Guest?.RequestMove(__instance,amount);
        __result=0; return false;
    }
}
[HarmonyPatch(typeof(SlotMarker),nameof(SlotMarker.AcceptUnchecked))]
internal static class ReplicaUncheckedDrop
{
    private static bool Prefix(SlotMarker __instance,ref int __result)
    {
        if(__instance.item is GameItemElement local && NativeLocalItemChooser.Owns(local)) return true;
        if(!NativeGuest.Active && !NativeHandTransferInput.BlocksHostSlot(__instance)) return true;
        if(NativeGuest.Active && __instance.IsValid()) NativeWorld.Guest?.RequestMove(__instance,__instance.numTransfer);
        __result=0; return false;
    }
}
[HarmonyPatch(typeof(GameItem),nameof(GameItem.MayRemove))]
internal static class ReplicaMayRemove
{
    private static bool Prefix(GameItem __instance,ref bool __result)
    { if(!NativeGuest.Active || NativeWorld.Guest?.TryData(__instance,out var data)!=true) return true; __result=(bool)data["mayRemove"]; return false; }
}
[HarmonyPatch(typeof(GameItem),nameof(GameItem.MaxNumRemove))]
internal static class ReplicaMaxRemove
{
    private static bool Prefix(GameItem __instance,ref int __result)
    { if(!NativeGuest.Active || NativeWorld.Guest?.TryData(__instance,out var data)!=true) return true; __result=(int)data["maxRemove"]; return false; }
}
