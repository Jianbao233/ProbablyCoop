using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Core;

namespace ProbablyCoop;

internal sealed partial class NativeWorld
{
    private const BindingFlags BusinessFlags=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    private static bool BusinessValue(FieldInfo f) => !f.IsStatic && !f.IsInitOnly &&
        (f.FieldType.IsPrimitive || f.FieldType.IsEnum || f.FieldType==typeof(string) || f.FieldType==typeof(decimal) ||
         f.FieldType==typeof(List<string>) || f.FieldType==typeof(List<int>) || f.FieldType==typeof(Dictionary<string,int>));
    private static readonly FieldInfo[] ServiceFields=typeof(StoreService).GetFields(BusinessFlags).Where(BusinessValue).ToArray();
    private static readonly FieldInfo[] UpgradeFields=typeof(NetworkUpgrade).GetFields(BusinessFlags).Where(BusinessValue).ToArray();
    private static readonly FieldInfo[] HealthFields=typeof(HealthData).GetFields(BusinessFlags).Where(BusinessValue).ToArray();
    private static readonly FieldInfo[] ClientDataFields=typeof(StoreClientData).GetFields(BusinessFlags).Where(BusinessValue).ToArray();
    private static readonly FieldInfo[] ReputationFields=typeof(StoreReputation).GetFields(BusinessFlags).Where(BusinessValue).ToArray();
    private static readonly FieldInfo[] PerkFields=typeof(StoreReputationPerk).GetFields(BusinessFlags).Where(BusinessValue).ToArray();
    private static readonly FieldInfo[] StartingPerkFields=typeof(StartingPerk).GetFields(BusinessFlags).Where(BusinessValue).ToArray();
    private static readonly FieldInfo[] CommissaryFields=typeof(CommissaryItemData).GetFields(BusinessFlags).Where(BusinessValue).ToArray();
    private static readonly FieldInfo[] StationFields=typeof(StoreStation).GetFields(BusinessFlags).Where(BusinessValue).ToArray();
    private static readonly FieldInfo[] EventFields=typeof(StoreEvent).GetFields(BusinessFlags).Where(BusinessValue).ToArray();
    private static readonly FieldInfo[] EventManagerFields=typeof(StoreEventManager).GetFields(BusinessFlags).Where(BusinessValue).ToArray();
    private static readonly FieldInfo[] NegoFields=typeof(NegociationData).GetFields(BusinessFlags).Where(BusinessValue).ToArray();
    private static readonly FieldInfo[] ClientManagerFields=typeof(StoreClientManager).GetFields(BusinessFlags).Where(BusinessValue).ToArray();
    private static readonly FieldInfo[] RequestFields=typeof(StoreRequest).GetFields(BusinessFlags).Where(BusinessValue).ToArray();
    private static readonly FieldInfo[] RunStatsFields=typeof(RunStats).GetFields(BusinessFlags).Where(BusinessValue).ToArray();
    private static readonly FieldInfo[] SecurityFields=typeof(SecData).GetFields(BusinessFlags).Where(BusinessValue).ToArray();
    private static readonly FieldInfo[] OutingFields=typeof(AfterhourLocationState).GetFields(BusinessFlags).Where(BusinessValue).ToArray();
    private static readonly FieldInfo[] OutingEventFields=typeof(AfterhourActiveEvent).GetFields(BusinessFlags).Where(BusinessValue).ToArray();
    private static readonly FieldInfo[] ExchangeFields=typeof(ExchangeManager).GetFields(BusinessFlags).Where(BusinessValue).ToArray();
    private static readonly FieldInfo ActiveEvents=AccessTools.Field(typeof(StoreEventManager),"activeEvents");
    internal int NormalCustomers { get; private set; }
    private JObject business;
    private static JObject BusinessFields(object value,FieldInfo[] fields)
    {
        var data=new JObject(); foreach(var f in fields) data[f.Name]=NativeSnapshotCodec.FieldValue(f.GetValue(value)); return data;
    }
    private static void ReadBusinessFields(object target,FieldInfo[] fields,JObject data)
    {
        if(data.Count!=fields.Length) throw new InvalidOperationException("Business field contract mismatch");
        foreach(var f in fields) { var v=data[f.Name]; if(v==null) throw new InvalidOperationException("Missing business field"); f.SetValue(target,v.Type==JTokenType.Null ? null : v.ToObject(f.FieldType)); }
    }
    private static JObject CaptureBusiness(NativeDeferredFields deferred,bool includePersonal=true)
    {
        JObject Fields(object source,FieldInfo[] contract) => deferred.CaptureBusinessFields(source,contract);
        var store=PlayerStore.Instance;
        var data=new JObject {
            ["runStats"]=store.runStats==null ? null : Fields(store.runStats,RunStatsFields),
            ["security"]=Fields(store.secData,SecurityFields),["wanted"]=JObject.FromObject(store.wantedStatus),
            ["exchange"]=Fields(store.exchangeManager,ExchangeFields),
            ["lottery"]=new JObject { ["started"]=store.scratchersStartedTotal,["finished"]=store.scratchersFinishedTotal,
                ["winnings"]=store.scratcherWinningsTotal,["coinGiven"]=store.isGamblersCoinRewardGiven },
            ["outings"]=new JObject(store.afterhourState.Select(p=>new JProperty(p.Key,new JObject { ["data"]=Fields(p.Value,OutingFields),
                ["events"]=new JArray((p.Value.activeEvents ?? new List<AfterhourActiveEvent>()).Select(e=>Fields(e,OutingEventFields))) }))),
            ["services"]=new JArray(store.storeService.Select(s=>Fields(s,ServiceFields))),
            ["upgrades"]=new JObject(store.networkUpgrade.Select(p=>new JProperty(p.Key,Fields(p.Value,UpgradeFields)))),
            ["clientData"]=Fields(store.storeClientData,ClientDataFields),
            ["station"]=Fields(StoreStation.Instance,StationFields),
            ["eventManager"]=Fields(StoreStation.Instance.storeEventManager,EventManagerFields),
            ["activeEvents"]=new JArray(((List<StoreEvent>)ActiveEvents.GetValue(StoreStation.Instance.storeEventManager)).Select(e=>CaptureEvent(e,deferred))),
            ["futureEvents"]=new JArray(StoreStation.Instance.storeEventManager.futurEvents.Select(e=>CaptureEvent(e,deferred))),
            ["clientManager"]=Fields(store.storeClientManager,ClientManagerFields),
            ["clientQueue"]=new JArray(store.storeClientManager.clientStack.Select(client=>CaptureClient(client))),
            ["normalCustomers"]=NativeCustomerCountQuery.ReadHost(),
            ["requests"]=new JArray(store.storeRequests.Select(r=>Fields(r,RequestFields))),
            ["reputations"]=new JArray(store.storeReputations.Select(r=>new JObject { ["data"]=Fields(r,ReputationFields),["perks"]=new JArray(r.perks.Select(p=>Fields(p,PerkFields))) })),
            ["tokens"]=store.commissaryData.redeemedCredit,["commissary"]=new JArray(store.commissaryData.commissaryItemDatas.Select(c=>Fields(c,CommissaryFields))),
            ["nightLogs"]=JArray.FromObject(store.nightLogs),["dialogHistory"]=JArray.FromObject(store.dialogHistory)
        };
        // Deferred captures retain their destination until the worker packs the
        // snapshot. Do not capture an actor block and then remove its target.
        if(includePersonal)
        {
            data["health"]=Fields(store.healthData,HealthFields);
            data["startingPerks"]=new JArray(store.startingPerks.Select(p=>Fields(p,StartingPerkFields)));
        }
        return data;
    }
    private Action ApplyBusinessFacts(JObject data,HashSet<string> changed)
    {
        business=data;
        if(changed.Count==0) return null;
        var store=PlayerStore.Instance;
        bool servicesChanged=changed.Contains("services");
        bool securityChanged=changed.Contains("security");
        bool newsChanged=changed.Contains("activeEvents");
        bool reputationChanged=changed.Contains("reputations");
        var oldWanted=store.wantedStatus;
        if(securityChanged) ReadBusinessFields(store.secData,SecurityFields,(JObject)data["security"]);
        if(changed.Contains("exchange")) ReadBusinessFields(store.exchangeManager,ExchangeFields,(JObject)data["exchange"]);
        if(changed.Contains("lottery"))
        {
            store.scratchersStartedTotal=(int)data["lottery"]["started"]; store.scratchersFinishedTotal=(int)data["lottery"]["finished"];
            store.scratcherWinningsTotal=(int)data["lottery"]["winnings"]; store.isGamblersCoinRewardGiven=(bool)data["lottery"]["coinGiven"];
        }
        if(changed.Contains("wanted")) store.wantedStatus=data["wanted"].ToObject<Dictionary<string,int>>();
        if(changed.Contains("outings"))
        {
            var outings=new Dictionary<string,AfterhourLocationState>();
            foreach(var entry in ((JObject)data["outings"]).Properties())
            {
                var state=store.afterhourState.TryGetValue(entry.Name,out var existing) ? existing : new AfterhourLocationState();
                ReadBusinessFields(state,OutingFields,(JObject)entry.Value["data"]);
                state.activeEvents=((JArray)entry.Value["events"]).Cast<JObject>().Select(e=> { var v=new AfterhourActiveEvent(); ReadBusinessFields(v,OutingEventFields,e); return v; }).ToList();
                outings.Add(entry.Name,state);
            }
            store.afterhourState=outings;
        }
        if(changed.Contains("runStats"))
        {
            if(data["runStats"] is JObject stats) { store.runStats ??= new RunStats(); ReadBusinessFields(store.runStats,RunStatsFields,stats); }
            else store.runStats=null;
        }
        if(servicesChanged)
        {
            var services=new List<StoreService>();
            foreach(JObject s in (JArray)data["services"])
            {
                var service=store.storeService.FirstOrDefault(v=>v.id==(string)s["id"]) ?? new StoreService();
                ReadBusinessFields(service,ServiceFields,s); services.Add(service);
            }
            store.storeService=services;
        }
        if(changed.Contains("upgrades"))
        {
            foreach(var p in ((JObject)data["upgrades"]).Properties())
            {
                if(!store.networkUpgrade.TryGetValue(p.Name,out var upgrade)) { upgrade=new NetworkUpgrade(p.Name); store.networkUpgrade.Add(p.Name,upgrade); }
                ReadBusinessFields(upgrade,UpgradeFields,(JObject)p.Value);
            }
            foreach(string id in store.networkUpgrade.Keys.Where(id=>data["upgrades"][id]==null).ToArray()) store.networkUpgrade.Remove(id);
        }
        if(!HasActorProjection && changed.Contains("health")) ReadBusinessFields(store.healthData,HealthFields,(JObject)data["health"]);
        if(changed.Contains("clientData")) ReadBusinessFields(store.storeClientData,ClientDataFields,(JObject)data["clientData"]);
        if(!HasActorProjection && changed.Contains("startingPerks"))
        {
            var startingPerks=new List<StartingPerk>();
            foreach(JObject p in (JArray)data["startingPerks"]) { var perk=new StartingPerk(); ReadBusinessFields(perk,StartingPerkFields,p); startingPerks.Add(perk); }
            store.startingPerks=startingPerks;
        }
        if(changed.Contains("station")) ReadBusinessFields(StoreStation.Instance,StationFields,(JObject)data["station"]);
        if(changed.Contains("eventManager")) ReadBusinessFields(StoreStation.Instance.storeEventManager,EventManagerFields,(JObject)data["eventManager"]);
        if(newsChanged) ActiveEvents.SetValue(StoreStation.Instance.storeEventManager,((JArray)data["activeEvents"]).Cast<JObject>().Select(ReadEvent).ToList());
        if(changed.Contains("futureEvents")) StoreStation.Instance.storeEventManager.futurEvents=((JArray)data["futureEvents"]).Cast<JObject>().Select(ReadEvent).ToList();
        if(changed.Contains("clientManager")) ReadBusinessFields(store.storeClientManager,ClientManagerFields,(JObject)data["clientManager"]);
        if(changed.Contains("clientQueue")) store.storeClientManager.clientStack=((JArray)data["clientQueue"]).Cast<JObject>().Select(client=>ReadClient(client)).ToList();
        NormalCustomers=(int)data["normalCustomers"];
        if(changed.Contains("requests")) store.storeRequests=((JArray)data["requests"]).Cast<JObject>().Select(r=> { var request=new StoreRequest(); ReadBusinessFields(request,RequestFields,r); return request; }).ToList();
        if(reputationChanged)
        {
            var reputations=new List<StoreReputation>();
            foreach(JObject r in (JArray)data["reputations"])
            {
                var reputation=store.storeReputations.FirstOrDefault(v=>v.factionId==(string)r["data"]["factionId"]) ?? new StoreReputation();
                ReadBusinessFields(reputation,ReputationFields,(JObject)r["data"]);
                var perks=new List<StoreReputationPerk>();
                foreach(JObject p in (JArray)r["perks"])
                {
                    var perk=reputation.perks.FirstOrDefault(v=>v.perkId==(string)p["perkId"]) ?? new StoreReputationPerk();
                    ReadBusinessFields(perk,PerkFields,p); perks.Add(perk);
                }
                reputation.perks=perks;
                reputations.Add(reputation);
            }
            store.storeReputations=reputations;
        }
        store.commissaryData.redeemedCredit=(int)data["tokens"];
        if(changed.Contains("commissary"))
        {
            store.commissaryData.commissaryItemDatas.Clear();
            foreach(JObject c in (JArray)data["commissary"]) { var stock=new CommissaryItemData(); ReadBusinessFields(stock,CommissaryFields,c); store.commissaryData.commissaryItemDatas.Add(stock); }
        }
        if(changed.Contains("nightLogs")) store.nightLogs=data["nightLogs"].ToObject<List<string>>();
        if(changed.Contains("dialogHistory")) store.dialogHistory=new Queue<string>(data["dialogHistory"].ToObject<List<string>>());
        // These native views read inventories, customer state and scene facts.
        // Publish them only after the complete replica graph is readable.
        return () => {
            if(servicesChanged && EndOfDayUIManager.Instance.IsOpen) EndOfDayUIManager.Instance.InitUI();
            if((changed.Contains("commissary") || changed.Contains("tokens")) && CommissaryUIManager.Instance.panel.activeSelf) CommissaryUIManager.Instance.RefreshUI();
            if(securityChanged && RecordUIManager.Instance?.UIPanel.activeSelf==true) RecordUIManager.Instance.Init();
            foreach(var wanted in store.wantedStatus)
            if(wanted.Value==1 && (!oldWanted.TryGetValue(wanted.Key,out int prior) || prior!=1)) WantedUIManager.Instance?.OnArrested(wanted.Key);
            if(newsChanged && NewsUIManager.Instance.uiGameObject.activeSelf) NewsUIManager.Instance.PopulateUI();
            if(reputationChanged) ReplicaReputationView.Refresh();
        };
    }
    private static JObject CaptureEvent(StoreEvent value,NativeDeferredFields deferred) => new JObject { ["fields"]=deferred.CaptureBusinessFields(value,EventFields),
        ["negotiation"]=new JArray((value.negociationDatas ?? new List<NegociationData>()).Select(n=>deferred.CaptureBusinessFields(n,NegoFields))) };
    private static StoreEvent ReadEvent(JObject data)
    {
        var value=new StoreEvent(); ReadBusinessFields(value,EventFields,(JObject)data["fields"]);
        foreach(JObject n in (JArray)data["negotiation"]) { var modifier=new NegociationData("",0,"",""); ReadBusinessFields(modifier,NegoFields,n); value.negociationDatas.Add(modifier); }
        return value;
    }
    internal void RequestService(StoreService service,bool on) => Submit(new JObject { ["kind"]="service",["id"]=service.id,["on"]=on,["token"]=HashData(new JObject { ["services"]=business["services"].DeepClone() }) });
    private ushort ExecuteService(JObject command)
    {
        if(shop.Settling || PlayerStore.Instance.storeState!=PlayerStore.StoreState.CLOSED || MapUIManager.Instance.IsOutside || !PlayerStore.Instance.CanEndDay()) return (ushort)ShopResult.Busy;
        var services=new JArray(PlayerStore.Instance.storeService.Select(service=>BusinessFields(service,ServiceFields)));
        if((string)command["token"]!=HashData(new JObject { ["services"]=services })) return (ushort)ShopResult.Stale;
        var service=PlayerStore.Instance.storeService.FirstOrDefault(s=>s.id==(string)command["id"]);
        if(service==null || !service.unlocked || service.isMandatory || service.IsOnCooldown()) return (ushort)ShopResult.Unavailable;
        AccessTools.Method(typeof(EndOfDayUIManager),"OnToggled").Invoke(EndOfDayUIManager.Instance,new object[]{service,(bool)command["on"]});
        nextCapture=0; return (ushort)ShopResult.Applied;
    }
}

// The native forecast calls Roll(100) for two guaranteed modifiers. Suppress only
// these guaranteed rolls while obtaining its read model; snapshots must not advance RNG.
[HarmonyPatch(typeof(StoreClientManager),nameof(StoreClientManager.GetNormalCustomerCount))]
internal static class NativeCustomerCountQuery
{
    internal static bool Reading;
    internal static int ReadHost()
    { Reading=true; try { return PlayerStore.Instance.storeClientManager.GetNormalCustomerCount(); } finally { Reading=false; } }
    private static bool Prefix(ref int __result)
    { if(!NativeGuest.Active || NativeWorld.Guest?.Ready!=true) return true; __result=NativeWorld.Guest.NormalCustomers; return false; }
}
[HarmonyPatch(typeof(RNG),nameof(RNG.Roll),new[]{typeof(int)})]
internal static class NativeForecastGuaranteedRoll
{ private static bool Prefix(int __0,ref bool __result) { if(!NativeCustomerCountQuery.Reading || __0!=100) return true; __result=true; return false; } }

[HarmonyPatch(typeof(EndOfDayUIManager),"OnToggled")]
internal static class ReplicaNightService
{
    private static bool Prefix(StoreService storeService,bool on)
    { if(!NativeGuest.Active) return true; NativeWorld.Guest?.RequestService(storeService,on); EndOfDayUIManager.Instance.InitUI(); return false; }
}
