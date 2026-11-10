using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Core;
using UnityEngine;

namespace ProbablyCoop;

internal sealed partial class NativeWorld
{
    private readonly PickupLease partnerLease=new PickupLease();
    private readonly Dictionary<uint,HeldSource> partnerSources=new Dictionary<uint,HeldSource>();
    private HeldInteraction<GraphNodeStorage> partnerFootprint;
    private bool committingPartner;
    private int heldDay;
    private string heldScene;
    private double nextHandCheck;
    private sealed class HeldSource
    {
        internal GameItem Item;
        internal GameInventory Parent;
        internal string Token;
        internal int Count;
    }
    internal bool PartnerHasHand => partnerLease.ActiveSerial!=0;
    internal int PartnerHandCount => partnerLease.Items.Count;
    private static double HandClock => Time.realtimeSinceStartupAsDouble;
    private static string HandScene => MapUIManager.Instance.Current?.Id ?? "";
    private bool HandBusinessOpen => !shop.Settling && !EndingActive &&
        !(bool)HarmonyLib.AccessTools.Field(typeof(MapUIManager),"inTransit").GetValue(MapUIManager.Instance);

    private void ClearPartnerRecords()
    {
        partnerSources.Clear(); partnerFootprint=null; nextCapture=0;
    }
    private void ResetPartnerHand()
    {
        partnerLease.Reset(); ClearPartnerRecords();
    }
    private void TickPartnerHand()
    {
        using var actorScope=EnterPartnerOperation();
        if(!PartnerHasHand) return;
        double now=HandClock;
        if(partnerLease.Tick(now)) { ClearPartnerRecords(); return; }
        if(now<nextHandCheck) return;
        nextHandCheck=now+0.15;
        if(partnerLease.Items.All(CanStillHold)) return;
        partnerLease.Release(partnerLease.ActiveSerial,now); ClearPartnerRecords();
    }
    private bool SourceStillPresent(uint id,HeldSource source) =>
        model.Objects.TryGetValue(id,out var value) && ReferenceEquals(value,source.Item) &&
        source.Item is GameItemElement el && !el.IsDestroyed() && source.Item.parentInventory==source.Parent &&
        source.Item.unitCount==source.Count;

    private static bool ReadHandSerial(JObject data,out ulong serial)
    {
        serial=0;
        return data["hand"]?.Type==JTokenType.Integer && ulong.TryParse(data["hand"].ToString(),out serial) && serial!=0;
    }
    private static JArray DropSources(JObject command)
    {
        return (string)command["kind"] switch {
            "group"=>command["items"] as JArray,
            "move" or "containerDrop"=>new JArray(new JObject{["id"]=command["id"],["token"]=command["token"]}),
            "item" when (string)command["action"]=="target"=>new JArray(new JObject{["id"]=command["id"],["token"]=command["token"]}),
            _=>null };
    }
    private ShopResult BeginPartnerHand(ulong serial,JArray rows)
    {
        var declarations=new Dictionary<uint,JObject>();
        if(rows!=null && rows.Count is >0 and <=PickupLease.MaxItems)
            foreach(var row in rows)
            {
                if(row is not JObject fields || !uint.TryParse(fields["id"]?.ToString(),out uint id) || id==0 ||
                    fields["token"]?.Type!=JTokenType.String || declarations.ContainsKey(id)) { declarations.Clear(); break; }
                declarations.Add(id,fields);
            }
        var sources=new Dictionary<uint,HeldSource>();
        var refusal=ShopResult.Unavailable;
        var result=partnerLease.Acquire(serial,declarations.Keys.ToArray(),HandClock,id=>{
            var fields=declarations[id];
            if(!CurrentItem(fields,out var item)) { refusal=ShopResult.Stale; return false; }
            if(!HandBusinessOpen || HandTouches(item)) { refusal=ShopResult.Busy; return false; }
            if(!Accessible(item.parentInventory) || !item.MayRemove() || item.MaxNumRemove()<1) return false;
            sources.Add(id,new HeldSource { Item=item,Parent=item.parentInventory,Count=item.unitCount,Token=(string)fields["token"] });
            return true;
        });
        if(result!=ShopResult.Applied) return result==ShopResult.Unavailable ? refusal : result;
        partnerSources.Clear();
        foreach(var source in sources) partnerSources.Add(source.Key,source.Value);
        partnerFootprint=new HeldInteraction<GraphNodeStorage>(sources.Values.Select(s=>(GraphNodeStorage)s.Item),n=>n.children,n=>n.parents);
        heldDay=StoreStation.Instance.dayCounter; heldScene=HandScene; nextCapture=0;
        return result;
    }
    private bool CanStillHold(uint id) => partnerSources.TryGetValue(id,out var source) && SourceStillPresent(id,source) &&
        !HandTouches(source.Item) && Accessible(source.Parent) && source.Item.MayRemove() && source.Item.MaxNumRemove()>0 &&
        MoveToken(source.Item)==source.Token && heldDay==StoreStation.Instance.dayCounter && heldScene==HandScene && HandBusinessOpen;

    internal ulong LastPartnerDropSerial { get; private set; }
    internal ushort LastPartnerDropResult { get; private set; }
    private ushort ExecuteHand(JObject command)
    {
        ushort result = ExecuteHandCore(command);
        if (DropSources(command) != null && ReadHandSerial(command, out ulong serial))
        {
            LastPartnerDropSerial = serial;
            LastPartnerDropResult = result;
        }
        return result;
    }

    private ushort ExecuteHandCore(JObject command)
    {
        TickPartnerHand();
        if(!ReadHandSerial(command,out ulong serial)) return (ushort)ShopResult.Stale;
        switch((string)command["kind"])
        {
            case "handBegin": return (ushort)BeginPartnerHand(serial,command["items"] as JArray);
            case "handRelease":
                var released=partnerLease.Release(serial,HandClock);
                if(!PartnerHasHand) ClearPartnerRecords();
                return (ushort)released;
            case "handRenew":
                var renewed=partnerLease.Renew(serial,HandClock,CanStillHold);
                if(!PartnerHasHand) ClearPartnerRecords();
                return (ushort)renewed;
        }
        var rows=DropSources(command);
        if(rows==null || rows.Count is <1 or >PickupLease.MaxItems || rows.Any(r=>r is not JObject ||
            r["id"]?.Type!=JTokenType.Integer || !uint.TryParse(r["id"].ToString(),out var id) || id==0 ||
            r["token"]?.Type!=JTokenType.String)) return (ushort)ShopResult.Unavailable;
        // A short drag may end before its first player-loop tick. Validate
        // pickup and drop together in that one host transaction.
        if(partnerLease.ActiveSerial!=serial)
        {
            var begin=BeginPartnerHand(serial,rows);
            if(begin!=ShopResult.Applied) return (ushort)begin;
        }
        var ids=rows.Select(row=>(uint)row["id"]).ToArray();
        try
        {
            return (ushort)partnerLease.Commit(serial,ids,HandClock,()=>{
                for(int i=0;i<ids.Length;i++)
                    if(!CanStillHold(ids[i]) || partnerSources[ids[i]].Token!=(string)rows[i]["token"]) return ShopResult.Stale;
                committingPartner=true;
                try { return (ShopResult)ExecuteCore(command); }
                finally { committingPartner=false; }
            });
        }
        finally { if(!PartnerHasHand) ClearPartnerRecords(); }
    }

    internal bool PartnerTouches(GameItem item) => !committingPartner && PartnerHasHand && partnerFootprint?.TouchesItem(item)==true;
    internal bool BlocksLocalSlot(SlotMarker slot) => !committingPartner && PartnerHasHand &&
        (PartnerTouches(slot.item) || PartnerTouches(slot.targetItem) || partnerFootprint.TouchesDestination(slot.inventory));
    internal bool PartnerBlocksInventory(GameInventory inventory,bool allContents) => !committingPartner && PartnerHasHand &&
        (allContents ? partnerFootprint.TouchesContents(inventory) : partnerFootprint.TouchesDestination(inventory));
    internal void RevokePartnerHand()
    {
        if(!PartnerHasHand) return;
        partnerLease.Release(partnerLease.ActiveSerial,HandClock);
        ClearPartnerRecords();
    }
    internal bool RejectHeldInput(GameItem item)
    {
        if(!PartnerTouches(item)) return false;
        CommandNotice("合伙人正在移动这件物品。");
        return true;
    }
}
