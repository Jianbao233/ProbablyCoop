using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Core;
using UnityEngine;

namespace ProbablyCoop;

// CustomUI helpers keep their original widgets and input state. Only their
// configuration writes cross this boundary; no delegate or factory is replayed
// on a replica, and a local draft never edits the confirmed item tags.
internal sealed partial class NativeWorld
{
    internal static bool IsLogisticsFrontend(FrontendKind kind)=>kind is FrontendKind.Hopper or FrontendKind.StorageCoordinator or FrontendKind.MindReprogrammer;
    internal static readonly string[] HopperConfigKeys={HopperHelper.ORDER_INT,HopperHelper.KEEP_INT,HopperHelper.FILTER_MODE_INT,
        HopperHelper.FILTER_STRING,HopperHelper.ON_ADD_BOOL,HopperHelper.ON_MOVE_BOOL,HopperHelper.LINK_INT};
    private sealed class LogisticsDraft
    {
        internal GameItem Item;
        internal FrontendKind Kind;
        internal JObject Base,Desired,Sent;
        internal ActivityRequests.Entry Attempt;
        internal double Due;
    }
    private readonly Dictionary<GameItem,LogisticsDraft> logisticsDrafts=new Dictionary<GameItem,LogisticsDraft>();
    private readonly Dictionary<FrontendKind,string> logisticsRendered=new Dictionary<FrontendKind,string>();
    private readonly Dictionary<string,(string Name,Sprite Sprite)> logisticsDescriptions=new Dictionary<string,(string,Sprite)>(StringComparer.Ordinal);
    private static string[] LogisticsKeys(FrontendKind kind)=>kind==FrontendKind.Hopper ? HopperConfigKeys :
        new[]{MachineStorageCoordinator.STORAGE_COORDINATOR_PRESETS_STRING};
    private static TagState RawLogisticsTag(GameItem item,string key)=>item.modifiedState.dict.TryGetValue(key,out var tag) ? tag.Clone() : new TagState(key);
    private static JToken LogisticsValue(GameItem item,string key)
    {
        var tag=RawLogisticsTag(item,key);
        if(key==HopperHelper.FILTER_STRING || key==MachineStorageCoordinator.STORAGE_COORDINATOR_PRESETS_STRING) return tag.GetString() ?? "";
        if(key==HopperHelper.ON_ADD_BOOL || key==HopperHelper.ON_MOVE_BOOL) return tag.GetBool();
        return tag.GetInt();
    }
    private static JObject LogisticsConfig(GameItem item,FrontendKind kind)=>new JObject(LogisticsKeys(kind).Select(key=>new JProperty(key,LogisticsValue(item,key))));
    private static void SetLogisticsValue(TagState tag,string key,JToken value)
    {
        tag.Enable();
        if(key==HopperHelper.FILTER_STRING || key==MachineStorageCoordinator.STORAGE_COORDINATOR_PRESETS_STRING) tag.SetString((string)value);
        else if(key==HopperHelper.ON_ADD_BOOL || key==HopperHelper.ON_MOVE_BOOL) tag.SetBool((bool)value);
        else tag.SetInt((int)value);
    }
    internal bool EditLogisticsTag(GameItem item,string key,Action<TagState> edit)
    {
        if(host || item==null) return false;
        var kind=HopperHelper.IsIntake(item) ? FrontendKind.Hopper : item.IsTag(MachineStorageCoordinator.STORAGE_COORDINATOR_TAG) ? FrontendKind.StorageCoordinator : 0;
        if(kind==0 || !LogisticsKeys(kind).Contains(key)) return false;
        if(!Ready || edit==null || !TryData(item,out _) || !localFrontends.Values.Any(view=>ReferenceEquals(view.Target,item))) return true;
        if(!logisticsDrafts.TryGetValue(item,out var draft))
        {
            if(logisticsDrafts.Count>=16) { CommandNotice("待确认的设备设置过多，请稍后再试。"); return true; }
            var basis=LogisticsConfig(item,kind);
            draft=new LogisticsDraft { Item=item,Kind=kind,Base=basis,Desired=(JObject)basis.DeepClone() };
            logisticsDrafts.Add(item,draft);
        }
        var tag=RawLogisticsTag(item,key); SetLogisticsValue(tag,key,draft.Desired[key]); edit(tag);
        draft.Desired[key]=key==HopperHelper.FILTER_STRING || key==MachineStorageCoordinator.STORAGE_COORDINATOR_PRESETS_STRING ?
            JToken.FromObject(tag.GetString() ?? "") : key==HopperHelper.ON_ADD_BOOL || key==HopperHelper.ON_MOVE_BOOL ? JToken.FromObject(tag.GetBool()) : JToken.FromObject(tag.GetInt());
        draft.Due=Time.realtimeSinceStartupAsDouble+0.12;
        return true;
    }
    internal bool ReadLogisticsTag(GameItem item,string key,out TagState tag)
    {
        tag=null;
        if(!logisticsDrafts.TryGetValue(item,out var draft) || draft.Desired[key]==null) return false;
        tag=RawLogisticsTag(item,key); SetLogisticsValue(tag,key,draft.Desired[key]); return true;
    }
    private JObject LogisticsCommand(GameItem item,FrontendKind kind,string action)
    {
        if(!localFrontends.TryGetValue(kind,out var view) || !ReferenceEquals(view.Target,item) || !TryData(item,out var data)) return null;
        return new JObject { ["kind"]="frontend",["type"]=(int)kind,["view"]=view.Serial,["id"]=(uint)data["entity"],
            ["token"]=data["moveToken"],["action"]=action,["actor"]=(int)NativeActorRun.Slot.Partner };
    }
    internal void TickLogistics()
    {
        if(host || !Ready) return;
        ReconcileLogistics();
        foreach(var draft in logisticsDrafts.Values.ToArray())
        {
            if(draft.Attempt!=null || Time.realtimeSinceStartupAsDouble<draft.Due) continue;
            if(JToken.DeepEquals(draft.Base,draft.Desired)) { logisticsDrafts.Remove(draft.Item); continue; }
            var command=LogisticsCommand(draft.Item,draft.Kind,"configure");
            if(command==null) { logisticsDrafts.Remove(draft.Item); continue; }
            command["expected"]=HashData(draft.Base); command["settings"]=draft.Desired.DeepClone();
            if(Submit(command)) { draft.Sent=(JObject)draft.Desired.DeepClone(); draft.Attempt=UiRequestState(LastSubmittedUiRequest); if(NativeBackgroundQa.Enabled) NativeBackgroundQa.RememberLogisticsCommand(command); }
        }
    }
    private void ReconcileLogistics()
    {
        if(host) return;
        foreach(var draft in logisticsDrafts.Values.ToArray())
        {
            if(draft.Item.parentInventory==null || !TryData(draft.Item,out _)) { logisticsDrafts.Remove(draft.Item); continue; }
            var attempt=draft.Attempt;
            if(attempt==null || !attempt.Result.HasValue || attempt.Result==(ushort)ShopResult.Applied && attempt.Serial>frontendThrough) continue;
            var actual=LogisticsConfig(draft.Item,draft.Kind);
            bool accepted=attempt.Result==(ushort)ShopResult.Applied && JToken.DeepEquals(actual,draft.Sent);
            if(accepted && !JToken.DeepEquals(draft.Sent,draft.Desired))
            { draft.Base=actual; draft.Attempt=null; draft.Sent=null; continue; }
            logisticsDrafts.Remove(draft.Item);
            logisticsRendered[draft.Kind]=HashData(actual);
            NativeLogisticsUi.Refresh(draft.Kind,draft.Item,!accepted);
        }
        foreach(var pair in localFrontends)
        {
            if(!IsLogisticsFrontend(pair.Key) || !NativeLogisticsUi.Visible(pair.Key,pair.Value.Target) || logisticsDrafts.ContainsKey(pair.Value.Target)) continue;
            string proof=pair.Key==FrontendKind.MindReprogrammer ? MindProof(pair.Value.Target) : HashData(LogisticsConfig(pair.Value.Target,pair.Key));
            if(logisticsRendered.TryGetValue(pair.Key,out var old) && old==proof) continue;
            logisticsRendered[pair.Key]=proof;
            NativeLogisticsUi.Refresh(pair.Key,pair.Value.Target,true);
        }
    }
    internal bool RequestLogisticsRun(GameItem item)
    {
        if(logisticsDrafts.ContainsKey(item)) { CommandNotice("请等待漏斗设置确认后再转移物品。"); return false; }
        var command=LogisticsCommand(item,FrontendKind.Hopper,"run");
        if(command==null) return false;
        command["expected"]=HashData(LogisticsConfig(item,FrontendKind.Hopper));
        command["contents"]=LogisticsRunProof(item,true);
        return Submit(command);
    }
    internal bool RequestMindApply(GameItem item,StartingPerk definition)
    {
        if(definition==null) return false;
        var command=LogisticsCommand(item,FrontendKind.MindReprogrammer,"perk");
        if(command==null) return false;
        command["expected"]=MindProof(item); command["perk"]=definition.id;
        bool submitted=Submit(command);
        if(submitted && NativeBackgroundQa.Enabled) NativeBackgroundQa.RememberMindCommand(command);
        return submitted;
    }
    private static string MindProof(GameItem item)=>HashData(new JObject {
        ["charge"]=MindReprogrammerHelper.GetCharge(item),["cooldown"]=MindReprogrammerHelper.GetCooldown(item),
        ["perks"]=new JArray(NativeActorFieldAccess.Get_startingPerks(PlayerStore.Instance).Select(perk=>perk.id).OrderBy(id=>id,StringComparer.Ordinal)) });
    private string LogisticsRunProof(GameItem item,bool replica)
    {
        var outlet=HopperHelper.FindOutlet(item);
        JArray Rows(GameInventory inventory)=>new JArray((inventory?.childItems ?? new List<GameItem>()).Select(child=>new JObject {
            ["id"]=replica ? model.ReplicaId(child) : model.Id(child),["token"]=replica ? ReplicaMoveToken(child) : MoveToken(child) }).OrderBy(row=>(uint)row["id"]));
        return HashData(new JObject { ["source"]=Rows(item.parentInventory),["target"]=Rows(outlet?.parentInventory),
            ["outlet"]=replica ? model.ReplicaId(outlet) : model.Id(outlet) });
    }
    private ushort ExecuteLogistics(JObject command,FrontendKind kind)
    {
        if((int?)command["actor"]!=(int)OperationActor || !nativeFrontends.TryGetValue(kind,out var view) ||
            view.Serial!=(ulong)command["view"] || !ValidFrontend(view) || !CurrentItem(command,out var item) ||
            !ReferenceEquals(view.Target,item)) return (ushort)ShopResult.Stale;
        if(!GeneralHelper.IsItemEffectivelyOwned(item) || !Accessible(item.parentInventory)) return (ushort)ShopResult.Unavailable;
        if(HandTouches(item)) return (ushort)ShopResult.Busy;
        string action=(string)command["action"];
        if(kind==FrontendKind.MindReprogrammer)
        {
            if(action!="perk" || (string)command["expected"]!=MindProof(item)) return (ushort)ShopResult.Stale;
            var perk=MindReprogrammerHelper.GetEditablePerks().SingleOrDefault(value=>value.id==(string)command["perk"]);
            if(perk==null || !item.IsTag("MIND_REPROGRAMMER_TAG") || !MindReprogrammerHelper.CanApply(item,perk)) return (ushort)ShopResult.Unavailable;
            MindReprogrammerHelper.Apply(item,perk);
        }
        else
        {
            if((string)command["expected"]!=HashData(LogisticsConfig(item,kind))) return (ushort)ShopResult.Stale;
            if(kind==FrontendKind.Hopper && action=="run")
            {
                if((string)command["contents"]!=LogisticsRunProof(item,false)) return (ushort)ShopResult.Stale;
                var destination=HopperHelper.FindOutlet(item)?.parentInventory;
                if(destination==null || !Accessible(destination)) return (ushort)ShopResult.Unavailable;
                if(HandInside(item.parentInventory) || HandInside(destination)) return (ushort)ShopResult.Busy;
                HopperHelper.RunAndRecord(item);
            }
            else if(action=="configure" && command["settings"] is JObject settings)
            {
                if(!ValidateLogisticsSettings(item,kind,settings)) return (ushort)ShopResult.Unavailable;
                if(kind==FrontendKind.StorageCoordinator)
                    MachineStorageCoordinator.SavePresets(item,ParsePresets((string)settings[MachineStorageCoordinator.STORAGE_COORDINATOR_PRESETS_STRING]));
                else
                {
                    foreach(string key in HopperConfigKeys) item.ModifyTag(key,tag=>SetLogisticsValue(tag,key,settings[key]));
                    if(HopperHelper.IsOnAdd(item)) AccessTools.Method(typeof(HopperHelper),"Watch").Invoke(null,new object[]{item,item.parentInventory});
                }
            }
            else return (ushort)ShopResult.Unavailable;
        }
        using(NativeActorSession.Current?.Enter(NativeActorRun.Slot.Host))
            NativeLogisticsUi.Refresh(kind,item,true);
        nextCapture=0; return (ushort)ShopResult.Applied;
    }
    private static List<MachineStorageCoordinator.Preset> ParsePresets(string json)
    {
        if(string.IsNullOrEmpty(json)) return new List<MachineStorageCoordinator.Preset>();
        var value=JObject.Parse(json);
        if(value.Count!=1 || value["presets"] is not JArray array || array.Count>64) throw new InvalidDataException("Invalid bounded coordinator presets.");
        foreach(var node in array)
        {
            if(node is not JObject row || row.Count!=4 || row["name"]?.Type!=JTokenType.String || ((string)row["name"]).Length>256 ||
                row["keyCode"]?.Type!=JTokenType.Integer || row["itemIds"] is not JArray ids || ids.Count>256 ||
                row["itemTabs"] is not JArray tabs || tabs.Count>256 || ids.Any(id=>id.Type!=JTokenType.Integer || (int)id<=0) || ids.Distinct(JToken.EqualityComparer).Count()!=ids.Count)
                throw new InvalidDataException("Invalid coordinator preset row.");
            int code=(int)row["keyCode"];
            if(!Enum.IsDefined(typeof(KeyCode),code) || code>=(int)KeyCode.Mouse0) throw new InvalidDataException("Invalid coordinator key.");
            foreach(var tab in tabs)
                if(tab is not JObject t || t.Count!=2 || t["itemId"]?.Type!=JTokenType.Integer || !ids.Any(id=>(int)id==(int)t["itemId"]) ||
                    t["tabs"]?.Type!=JTokenType.String || ((string)t["tabs"]).Length>1024 || ((string)t["tabs"]).Any(c=>c!=',' && (c<'0'||c>'9')))
                    throw new InvalidDataException("Invalid coordinator tab selection.");
        }
        return array.ToObject<List<MachineStorageCoordinator.Preset>>();
    }
    private bool ValidateLogisticsSettings(GameItem item,FrontendKind kind,JObject settings)
    {
        if(settings.Count!=LogisticsKeys(kind).Length || LogisticsKeys(kind).Any(key=>settings[key]==null)) return false;
        if(kind==FrontendKind.StorageCoordinator)
        {
            if(settings[MachineStorageCoordinator.STORAGE_COORDINATOR_PRESETS_STRING]?.Type!=JTokenType.String) return false;
            string json=(string)settings[MachineStorageCoordinator.STORAGE_COORDINATOR_PRESETS_STRING];
            if(json.Length>65536) return false;
            List<MachineStorageCoordinator.Preset> rows;
            try { rows=ParsePresets(json); } catch(Newtonsoft.Json.JsonException) { return false; } catch(InvalidDataException) { return false; }
            var old=new HashSet<int>(MachineStorageCoordinator.LoadPresets(item).SelectMany(p=>p.itemIds));
            foreach(int id in rows.SelectMany(p=>p.itemIds).Distinct())
                if(!old.Contains(id) && !model.Objects.Values.OfType<GameItem>().Any(target=>target.uniqueId==id && target.parentInventory!=null &&
                    Accessible(target.parentInventory) && GeneralHelper.IsItemEffectivelyOwned(target) && target.contentWindow!=null && !target.contentWindow.isLocked)) return false;
            return true;
        }
        foreach(string key in new[]{HopperHelper.ORDER_INT,HopperHelper.KEEP_INT,HopperHelper.FILTER_MODE_INT,HopperHelper.LINK_INT})
            if(settings[key].Type!=JTokenType.Integer) return false;
        if(settings[HopperHelper.ON_ADD_BOOL].Type!=JTokenType.Boolean || settings[HopperHelper.ON_MOVE_BOOL].Type!=JTokenType.Boolean ||
            settings[HopperHelper.FILTER_STRING].Type!=JTokenType.String || (int)settings[HopperHelper.ORDER_INT]<1 || (int)settings[HopperHelper.ORDER_INT]>15 ||
            (int)settings[HopperHelper.KEEP_INT]<0 || (int)settings[HopperHelper.KEEP_INT]>20 || (int)settings[HopperHelper.FILTER_MODE_INT]<0 || (int)settings[HopperHelper.FILTER_MODE_INT]>2) return false;
        // Linking a new pair remains the original target action. This editor can
        // retain a link or clear it, never link arbitrary remote inventories.
        int linkId=(int)settings[HopperHelper.LINK_INT];
        if(linkId!=0 && linkId!=RawLogisticsTag(item,HopperHelper.LINK_INT).GetInt()) return false;
        string filter=(string)settings[HopperHelper.FILTER_STRING];
        var ids=filter.Split(';');
        if(filter.Length>32768 || ids.Length>128 || ids.Any(id=>id.Length>256)) return false;
        var oldFilter=new HashSet<string>((RawLogisticsTag(item,HopperHelper.FILTER_STRING).GetString() ?? "").Split(';'),StringComparer.Ordinal);
        // Native CopySettings/PasteSettings can copy a previously recorded
        // filter even when no specimen of that item currently exists.
        foreach(var other in model.Objects.Values.OfType<GameItem>().Where(target=>target.parentInventory!=null &&
            Accessible(target.parentInventory) && HopperHelper.IsIntake(target)))
            oldFilter.UnionWith((RawLogisticsTag(other,HopperHelper.FILTER_STRING).GetString() ?? "").Split(';'));
        return ids.Where(id=>id.Length!=0).All(id=>oldFilter.Contains(id) || model.Objects.Values.OfType<GameItem>().Any(target=>
            target.identifier==id && target.parentInventory!=null && Accessible(target.parentInventory) && GeneralHelper.IsItemEffectivelyOwned(target)));
    }
    private void ClearLogistics()
    { logisticsDrafts.Clear(); logisticsRendered.Clear(); logisticsDescriptions.Clear(); }

    private JObject CaptureLogisticsPresentation(FrontendKind kind,GameItem target)
    {
        if(kind!=FrontendKind.Hopper) return null;
        var descriptions=new JArray();
        foreach(string id in HopperHelper.GetFilterList(target).Distinct().Take(129))
        {
            if(descriptions.Count>=128) throw new InvalidOperationException("Native hopper filter exceeds bounded presentation.");
            // This is the same original one-time description constructor used
            // by its real source window. It never runs on the guest.
            var result=((string name,Sprite sprite))AccessTools.Method(typeof(HopperHelper),"Describe").Invoke(null,new object[]{id});
            descriptions.Add(new JObject { ["id"]=id,["name"]=result.name,["sprite"]=NativeLogisticsUi.SpriteData(result.sprite) });
        }
        return new JObject { ["descriptions"]=descriptions };
    }
    private void ApplyLogisticsPresentation(FrontendKind kind,GameItem target,JObject data)
    {
        if(kind!=FrontendKind.Hopper) return;
        if(data?["descriptions"] is not JArray rows || rows.Count>128) throw new InvalidDataException("Missing hopper presentation.");
        bool changed=false;
        foreach(JObject row in rows)
        {
            string id=(string)row["id"],name=(string)row["name"];
            if(string.IsNullOrEmpty(id) || id.Length>256 || name==null || name.Length>8192) throw new InvalidDataException("Invalid hopper description.");
            var sprite=NativeLogisticsUi.ReadSprite(row["sprite"] as JObject);
            if(!logisticsDescriptions.TryGetValue(id,out var old) || old.Name!=name || old.Sprite!=sprite) changed=true;
            logisticsDescriptions[id]=(name,sprite);
        }
        if(changed && NativeLogisticsUi.Visible(kind,target)) NativeLogisticsUi.Refresh(kind,target,false);
    }
    internal (string name,Sprite sprite) DescribeLogisticsItem(string id)
    {
        if(logisticsDescriptions.TryGetValue(id,out var cached)) return cached;
        var item=model.Objects.Values.OfType<GameItemElement>().FirstOrDefault(value=>value.identifier==id);
        return item!=null ? (item.GetDisplayName(),item.ResolveSpriteByName(item.spritePath)) : (LocHelper.GetLocalizedItem("item_"+id+"_name"),null);
    }
}
