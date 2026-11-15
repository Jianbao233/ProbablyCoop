using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Net;
using ProbablyCoop.Sync;
using UnityEngine;

namespace ProbablyCoop;

internal sealed partial class NativeEffectCampaign
{
    private sealed class LogisticsDraft
    {
        internal readonly JObject Pending = new JObject();
        internal JObject Sent;
        internal ulong Base;
        internal double Expires;
    }

    private static readonly System.Reflection.MethodInfo WatchHopper = AccessTools.Method(typeof(HopperHelper), "Watch");
    private static readonly System.Reflection.MethodInfo ScheduleHopper = AccessTools.Method(typeof(HopperHelper), "Schedule");
    private readonly Dictionary<GameItem, LogisticsDraft> logisticsDrafts = new Dictionary<GameItem, LogisticsDraft>();

    internal static bool IsLogisticsSetting(GameItem item, string key) =>
        HopperHelper.IsIntake(item) && NativeWorld.HopperConfigKeys.Contains(key) ||
        item?.IsTag(MachineStorageCoordinator.STORAGE_COORDINATOR_TAG) == true &&
            key == MachineStorageCoordinator.STORAGE_COORDINATOR_PRESETS_STRING;

    private static JToken SettingValue(string key, TagState tag)
    {
        if (key is HopperHelper.FILTER_STRING or MachineStorageCoordinator.STORAGE_COORDINATOR_PRESETS_STRING)
            return JValue.CreateString(tag.GetString() ?? "");
        if (key is HopperHelper.ON_ADD_BOOL or HopperHelper.ON_MOVE_BOOL) return new JValue(tag.GetBool());
        return new JValue(tag.GetInt());
    }

    private static void WriteSetting(string key, TagState tag, JToken value)
    {
        tag.Enable();
        if (key is HopperHelper.FILTER_STRING or MachineStorageCoordinator.STORAGE_COORDINATOR_PRESETS_STRING)
            tag.SetString((string)value);
        else if (key is HopperHelper.ON_ADD_BOOL or HopperHelper.ON_MOVE_BOOL) tag.SetBool((bool)value);
        else tag.SetInt((int)value);
    }

    internal void EditLogistics(GameItem owner, string key, Action<TagState> action)
    {
        if (action == null || !inventory.CanInteract(owner) || !GeneralHelper.IsItemEffectivelyOwned(owner)) return;
        var tag = owner.GetTagReadonly(key);
        action(tag);
        if (!logisticsDrafts.TryGetValue(owner, out var draft))
            logisticsDrafts[owner] = draft = new LogisticsDraft();
        draft.Pending[key] = SettingValue(key, tag);
    }

    internal bool ReadLogisticsDraft(GameItem owner, string key, out TagState tag)
    {
        tag = null;
        if (!logisticsDrafts.TryGetValue(owner, out var draft)) return false;
        var value = draft.Pending[key] ?? draft.Sent?[key];
        if (value == null) return false;
        tag = new TagState(key);
        WriteSetting(key, tag, value);
        return true;
    }

    private void UpdateReplicaLogistics()
    {
        if (logisticsDrafts.Count == 0) return;
        double now = Time.realtimeSinceStartupAsDouble;
        foreach (var pair in logisticsDrafts.ToArray())
        {
            var item = pair.Key;
            var draft = pair.Value;
            if (!inventory.CanInteract(item))
            {
                logisticsDrafts.Remove(item);
                NativeLogisticsUi.Close(LogisticsFrontend(item), item);
                continue;
            }
            ulong proof = inventory.ControlProof(item);
            if (draft.Sent != null)
            {
                if (proof == draft.Base && now < draft.Expires) continue;
                bool accepted = draft.Sent.Properties().All(property =>
                    JToken.DeepEquals(property.Value, SettingValue(property.Name,
                        item.modifiedState.dict.TryGetValue(property.Name, out var actual) ? actual : new TagState(property.Name))));
                draft.Sent = null;
                if (!accepted) draft.Pending.RemoveAll();
            }
            if (draft.Pending.Count > 0)
            {
                string data = draft.Pending.ToString(Formatting.None);
                if (Utf8.GetByteCount(data) <= ActivityIntent.MaxTextBytes)
                {
                    if (!RequestActivity(ActivityAction.LogisticsSetting, data, owner: item)) continue;
                    draft.Base = proof;
                    draft.Expires = now + 1.5;
                    draft.Sent = (JObject)draft.Pending.DeepClone();
                    draft.Pending.RemoveAll();
                    continue;
                }
            }
            logisticsDrafts.Remove(item);
            NativeLogisticsUi.Refresh(LogisticsFrontend(item), item, resetDraft: true);
        }
    }

    private static NativeWorld.FrontendKind LogisticsFrontend(GameItem item) => HopperHelper.IsIntake(item) ?
        NativeWorld.FrontendKind.Hopper : NativeWorld.FrontendKind.StorageCoordinator;

    internal bool MayRunHopper(GameItem intake, byte actor)
    {
        if (MapUIManager.Instance.IsOutside || !HopperHelper.IsIntake(intake) ||
            !inventory.TryFindAccessible(intake.uniqueId, actor, out var source) || !ReferenceEquals(intake, source)) return false;
        var outlet = HopperHelper.FindOutlet(intake);
        return outlet != null && HopperHelper.CanLink(intake, outlet) && intake.parentInventory != outlet.parentInventory &&
            inventory.TryFindAccessible(outlet.uniqueId, actor, out _) &&
            !TouchesHand(intake.parentInventory.childItems.Concat(outlet.parentInventory.childItems));
    }

    private (int id, GameInventory parent, int units)[] HopperArrivals(SyncTx tx)
    {
        if (MapUIManager.Instance.IsOutside) return Array.Empty<(int, GameInventory, int)>();
        return tx.Ops.Where(op => op.Kind == SyncOpKind.Put && op.Key.Space == SyncSpace.Item && op.Key.Id > 0 &&
            op.Key.Id <= int.MaxValue).Select(op =>
        {
            int id = (int)op.Key.Id;
            return inventory.TryFind(id, out var item) ? (id, item.parentInventory, item.unitCount) : (id, null, 0);
        }).ToArray();
    }

    private void ScheduleHopperArrivals((int id, GameInventory parent, int units)[] arrivals)
    {
        foreach (var arrival in arrivals)
        {
            if (!inventory.TryFind(arrival.id, out var item) ||
                item.parentInventory == arrival.parent && item.unitCount <= arrival.units) continue;
            if (HopperHelper.IsIntake(item))
            {
                WatchHopper.Invoke(null, new object[] { item, item.parentInventory });
                if (HopperHelper.IsOnMove(item)) ScheduleHopper.Invoke(null, new object[] { item });
            }
            else if (!HopperHelper.IsHopper(item))
                foreach (var intake in item.parentInventory.childItems)
                    if (HopperHelper.IsIntake(intake) && HopperHelper.IsOnAdd(intake))
                        ScheduleHopper.Invoke(null, new object[] { intake });
        }
    }

    private void RunGuestLogistics(ActivityIntent intent, NativeActorSession actors)
    {
        if (!inventory.TryFindAccessible(intent.Owner, NetSession.GuestPeer, out var item) ||
            !GeneralHelper.IsItemEffectivelyOwned(item) || TouchesHand(new[] { item }) ||
            inventory.ControlProof(item) != intent.Proof || !BeginAuthorityAction()) return;
        bool complete = false;
        try
        {
            using (actors.Enter(NativeActorRun.Slot.Partner))
            {
                if (intent.Action == ActivityAction.HopperRun)
                {
                    if (!MayRunHopper(item, NetSession.GuestPeer)) return;
                    complete = HopperHelper.RunAndRecord(item) > 0;
                }
                else complete = ApplyLogistics(item, intent.Text);
            }
            if (complete) NativeLogisticsUi.Refresh(LogisticsFrontend(item), item,
                resetDraft: intent.Action == ActivityAction.LogisticsSetting);
        }
        finally { EndAuthorityAction(complete); }
    }

    private bool ApplyLogistics(GameItem item, string json)
    {
        JObject settings;
        List<MachineStorageCoordinator.Preset> presets = null;
        try
        {
            settings = JObject.Parse(json);
            if (settings.Count == 0 || settings.Count > NativeWorld.HopperConfigKeys.Length ||
                settings.Properties().Any(property => !IsLogisticsSetting(item, property.Name))) return false;
            foreach (var property in settings.Properties())
            {
                var value = property.Value;
                switch (property.Name)
                {
                    case MachineStorageCoordinator.STORAGE_COORDINATOR_PRESETS_STRING:
                        if (value.Type != JTokenType.String) return false;
                        presets = NativeWorld.ParsePresets((string)value);
                        var previous = MachineStorageCoordinator.LoadPresets(item).SelectMany(preset => preset.itemIds).ToHashSet();
                        if (presets.SelectMany(preset => preset.itemIds).Distinct().Any(id => id == item.uniqueId ||
                            !previous.Contains(id) && (!inventory.TryFindAccessible(id, NetSession.GuestPeer, out var target) ||
                                !GeneralHelper.IsItemEffectivelyOwned(target) || target.contentWindow?.isLocked != false))) return false;
                        break;
                    case HopperHelper.LINK_INT:
                        if (!Integer(value) || (int)value != 0 &&
                            (!inventory.TryFindAccessible((int)value, NetSession.GuestPeer, out var outlet) ||
                                !HopperHelper.CanLink(item, outlet) || TouchesHand(new[] { outlet }))) return false;
                        break;
                    case HopperHelper.ORDER_INT:
                        if (!Integer(value, 1, 15)) return false;
                        break;
                    case HopperHelper.KEEP_INT:
                        if (!Integer(value, 0, 20)) return false;
                        break;
                    case HopperHelper.FILTER_MODE_INT:
                        if (!Integer(value, 0, 2)) return false;
                        break;
                    case HopperHelper.ON_ADD_BOOL:
                    case HopperHelper.ON_MOVE_BOOL:
                        if (value.Type != JTokenType.Boolean) return false;
                        break;
                    case HopperHelper.FILTER_STRING:
                        if (value.Type != JTokenType.String ||
                            ((string)value).Split(';').Where(id => id.Length > 0)
                                .Any(id => id.Length > 128 || !DirectoryMaster.Has<GameItem>(id))) return false;
                        break;
                }
            }
        }
        catch (Exception error) when (error is JsonException or ArgumentException or OverflowException or InvalidDataException)
        {
            return false;
        }
        if (presets != null)
        {
            MachineStorageCoordinator.SavePresets(item, presets);
            return true;
        }
        foreach (var property in settings.Properties())
        {
            if (property.Name == HopperHelper.LINK_INT)
            {
                if ((int)property.Value == 0) HopperHelper.Unlink(item);
                else if (inventory.TryFindAccessible((int)property.Value, NetSession.GuestPeer, out var outlet))
                    HopperHelper.Link(item, outlet);
            }
            else if (property.Name == HopperHelper.FILTER_STRING)
                HopperHelper.SetFilterList(item, ((string)property.Value).Split(';').Where(id => id.Length > 0).ToList());
            else item.ModifyTag(property.Name, tag => WriteSetting(property.Name, tag, property.Value));
        }
        if (HopperHelper.IsOnAdd(item)) WatchHopper.Invoke(null, new object[] { item, item.parentInventory });
        return true;
    }

    private void RefreshLogisticsViews(SyncTx tx)
    {
        foreach (var kind in new[] { NativeWorld.FrontendKind.Hopper, NativeWorld.FrontendKind.StorageCoordinator })
        {
            var item = NativeLogisticsUi.OpenItem(kind);
            if (item != null && !logisticsDrafts.ContainsKey(item) && tx.Ops.Any(op => op.Key.Equals(EntityKey.Item(item.uniqueId))))
            {
                if (!inventory.CanInteract(item)) NativeLogisticsUi.Close(kind, item);
                else NativeLogisticsUi.Refresh(kind, item, resetDraft: true);
            }
        }
    }
}
