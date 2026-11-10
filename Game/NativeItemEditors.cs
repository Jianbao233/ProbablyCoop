using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Core;

namespace ProbablyCoop;

internal sealed partial class NativeWorld
{
    private ulong labelRequest;
    internal bool RequestEditor(GameItem target,string action,JObject arguments)
    {
        if(target==null || !EditorTool(action,target,out var tool) || !TryData(target,out var data)) return false;
        arguments["kind"]="editor"; arguments["action"]=action; arguments["id"]=(uint)data["entity"];
        arguments["token"]=(string)data["moveToken"]; arguments["tool"]=model.ReplicaId(tool);
        if(action=="labels")
        {
            if(arguments["changes"] is not JArray changes || data["features"] is not JArray features) return false;
            foreach(JObject row in changes)
            {
                int index=(int)row["index"];
                if(index<0 || index>=features.Count || features[index] is not JObject feature) return false;
                row.Remove("index");
                row["feature"]=(uint)feature["entity"];
                row["expected"]=LabelProof(feature);
            }
        }
        arguments["trade"]=tradeToken;
        if(NativeBackgroundQa.Enabled && action=="labels") NativeBackgroundQa.RememberFirstLabelCommand(arguments);
        bool accepted=Submit(arguments);
        if(accepted && action=="labels") labelRequest=LastSubmittedUiRequest;
        return accepted;
    }
    private static string LabelProof(JObject feature)
    {
        var copy=(JObject)feature.DeepClone();
        // WaterFeatureHelper refreshes this formatted caption after a label edit.
        // Identity, exposure, conditions and all other feature data remain bound.
        ((JObject)copy["fields"]).Remove("publicDisplay");
        return HashData(copy);
    }
    private void RefreshLabelEditor(NativeSnapshotIndex index,ulong commandThrough)
    {
        // An older in-flight snapshot must not undo the actor's just-selected
        // option. The host watermark covers rejected edits as well as commits.
        if(labelRequest>commandThrough) return;
        bool confirm=labelRequest!=0;
        labelRequest=0;
        var ui=LabelerUIManager.Instance;
        if(ui==null || !ui.panel.activeSelf) return;
        var target=(GameItem)AccessTools.Field(typeof(LabelerUIManager),"curItem").GetValue(ui);
        uint id=model.ReplicaId(target);
        if(id==0 || target.parentInventory==null) { ui.CloseUI(); return; }
        if(!confirm && !index.PropertyChanged(id,"features")) return;
        var features=target.itemFeatures.Where(LabelerHelper.IsEditableLabelFeature).ToArray();
        var rows=ui.verticalLayoutGroup.GetComponentsInChildren<LabelerElement>();
        if(rows.Length!=features.Length || !rows.Select((row,i)=>ReplicaLabelInit.Matches(row,features[i])).All(match=>match))
        {
            ui.OpenUI(target);
            return;
        }
        for(int i=0;i<rows.Length;i++)
        {
            var feature=features[i];
            var condition=feature.GetCurrentItemCondition();
            rows[i].currentFeatureDisplay.text=LocHelper.GetLocalizedMechanic("mech_label_condition_value",condition.display,condition.GetModValueFormated());
            int choice=ItemConditionList.conditionLookup[feature.category].FindIndex(c=>c.identifier==condition.identifier);
            if(choice>=0) rows[i].dropdown.SetValueWithoutNotify(choice);
        }
        ui.repeatButton.SetActive(ui.CanRepeat());
    }
    private ushort ExecuteEditor(JObject command)
    {
        if(shop.Settling) return (ushort)ShopResult.Busy;
        if(!CurrentItem(command,out var target) || !Accessible(target.parentInventory)) return (ushort)ShopResult.Stale;
        if(!model.Objects.TryGetValue((uint)command["tool"],out var obj) || obj is not GameItem tool ||
            tool.parentInventory==null || !Accessible(tool.parentInventory) || !tool.MayTarget(target) || !tool.CanTarget(target)) return (ushort)ShopResult.Unavailable;
        if(HandTouches(target) || HandTouches(tool)) return (ushort)ShopResult.Busy;
        string action=(string)command["action"];
        if(action=="rename")
        {
            if(tool.identifier!="labeler_blue" || !GeneralHelper.IsItemOwned(target)) return (ushort)ShopResult.Unavailable;
            string text=(string)command["text"] ?? "";
            if(text.Length>256 || (string)command["previous"]!=GeneralHelper.GetCustomName(target)) return (ushort)ShopResult.Stale;
            if(text.Length==0) GeneralHelper.RemoveCustomName(target); else GeneralHelper.SetCustomName(target,text);
        }
        else if(action=="labels")
        {
            if(tool.identifier!="labeler" || !GeneralHelper.IsItemOwned(target)) return (ushort)ShopResult.Unavailable;
            if(command["changes"] is not JArray changes || changes.Count<1 || changes.Count>64) return (ushort)ShopResult.Unavailable;
            var edits=new List<(ItemFeature feature,ItemCondition condition,int index)>();
            foreach(JObject change in changes)
            {
                if(!uint.TryParse(change["feature"]?.ToString(),out uint id) || !model.Objects.TryGetValue(id,out var entity) ||
                    entity is not ItemFeature feature || !target.itemFeatures.Contains(feature) ||
                    (string)change["expected"]!=LabelProof(NativeMirrorProbe.FeatureData(feature,model))) return (ushort)ShopResult.Stale;
                if(!LabelerHelper.IsEditableLabelFeature(feature)) return (ushort)ShopResult.Unavailable;
                var choices=ItemConditionList.conditionLookup[feature.category];
                int choice=choices.FindIndex(c=>c.identifier==(string)change["condition"]);
                if(choice<0 || edits.Any(e=>ReferenceEquals(e.feature,feature))) return (ushort)ShopResult.Unavailable;
                edits.Add((feature,choices[choice],choice));
            }
            bool repeat=(bool?)command["repeat"]==true;
            foreach(var edit in edits)
            {
                edit.feature.fakeCondition=edit.condition; edit.feature.SetExposed(false);
                if(!repeat) WaterFeatureHelper.OnWaterLabelUpdated(edit.feature,edit.index);
            }
            EmporiumAudioPlayer.Instance.audioLabeler.Play();
            if(!repeat) AchievementTracker.Instance.CheckLowerLevelsSpecial();
        }
        else if(action=="confront")
        {
            if(tool.identifier!="magnifier" || GeneralHelper.IsItemOwned(target) || target.IsTag("ALREADY_CONFRONTED_TAG") ||
                target.parentInventory!=EmporiumEntry.Instance.backInvinvElement || NegociationUIManager.IsTradeLocked() || (string)command["trade"]!=TradeToken())
                return (ushort)ShopResult.Stale;
            int flags=(int)command["issues"]; if(flags<1 || flags>31) return (ushort)ShopResult.Unavailable;
            var ui=InspectionUIManager.Instance; var previous=ui.currentInspectingItem;
            var toggles=new[]{ui.toggle1,ui.toggle2,ui.toggle3,ui.toggle4,ui.toggle5}; var old=toggles.Select(t=>t.isOn).ToArray();
            bool wasOpen=ui.panel.activeSelf;
            try { ui.currentInspectingItem=target; for(int i=0;i<5;i++) toggles[i].SetIsOnWithoutNotify((flags&(1<<i))!=0); ui.OnConfront(); }
            finally { ui.currentInspectingItem=previous; for(int i=0;i<5;i++) toggles[i].SetIsOnWithoutNotify(old[i]); ui.panel.SetActive(wasOpen); }
        }
        else if(action=="estimate")
        {
            int value=(int)command["value"];
            if(tool.identifier!="magnifier" || !target.IsTag("APPRAISAL_ITEM") || value<0 ||
                target.parentInventory!=EmporiumEntry.Instance.backInvinvElement || (string)command["trade"]!=TradeToken() ||
                PlayerStore.Instance.currentClientInstance?.GetClientBlueprint().clientIntent!=StoreClient.ClientIntent.APPRAISAL_SERVICE)
                return (ushort)ShopResult.Unavailable;
            AppraisalServiceHelper.OnEstimate(target,value);
        }
        else return (ushort)ShopResult.Unavailable;
        nextCapture=0; return (ushort)ShopResult.Applied;
    }
}

[HarmonyPatch(typeof(LabelerBlueUIManager),nameof(LabelerBlueUIManager.OnEditEnd))]
internal static class ReplicaRenameItem
{
    internal static GameItem Target => (GameItem)AccessTools.Field(typeof(LabelerBlueUIManager),"curItem").GetValue(LabelerBlueUIManager.Instance);
    internal static bool Send(string value)
    {
        var target=Target;
        if(target==null) return false;
        // Unity fires onEndEdit again when the input is deselected or its panel
        // closes. Do not enqueue a duplicate edit over the player's next action.
        if((value ?? "")== (GeneralHelper.GetCustomName(target) ?? "")) return true;
        return NativeWorld.Guest?.RequestEditor(target,"rename",new JObject { ["text"]=value,["previous"]=GeneralHelper.GetCustomName(target) })==true;
    }
    private static bool Prefix()
    { if(!NativeGuest.Active || Target==null) return true; StoreUIManager.Instance.StopTyping(); Send(LabelerBlueUIManager.Instance.inputField.text); return false; }
}
[HarmonyPatch(typeof(LabelerBlueUIManager),nameof(LabelerBlueUIManager.OnClearButtonClick))]
internal static class ReplicaClearItemName
{ private static bool Prefix() { if(!NativeGuest.Active || ReplicaRenameItem.Target==null) return true; if(ReplicaRenameItem.Send("")) LabelerBlueUIManager.Instance.inputField.SetTextWithoutNotify(""); return false; } }
[HarmonyPatch(typeof(LabelerElement),nameof(LabelerElement.InitElement))]
internal static class ReplicaLabelInit
{
    internal static int Depth;
    private sealed class Binding { internal ItemFeature Feature; internal string Category; }
    private static readonly ConditionalWeakTable<LabelerElement,Binding> bindings=new ConditionalWeakTable<LabelerElement,Binding>();
    private static void Prefix() { Depth++; }
    private static void Postfix(LabelerElement __instance,ItemFeature itemFeature)
    {
        if(!NativeGuest.Active) return;
        bindings.Remove(__instance);
        bindings.Add(__instance,new Binding{Feature=itemFeature,Category=itemFeature.category});
    }
    internal static bool Matches(LabelerElement row,ItemFeature feature) => bindings.TryGetValue(row,out var binding) &&
        ReferenceEquals(binding.Feature,feature) && binding.Category==feature.category;
    private static void Finalizer() { Depth--; }
}
[HarmonyPatch(typeof(LabelerElement),nameof(LabelerElement.OnDropdownValueChanged))]
internal static class ReplicaLabelChoice
{
    private static bool Prefix(LabelerElement __instance)
    {
        if(!NativeGuest.Active) return true;
        if(ReplicaLabelInit.Depth>0) return false;
        var target=(GameItem)AccessTools.Field(typeof(LabelerUIManager),"curItem").GetValue(LabelerUIManager.Instance);
        var feature=(ItemFeature)AccessTools.Field(typeof(LabelerElement),"thisFeature").GetValue(__instance);
        int index=target?.itemFeatures.IndexOf(feature) ?? -1;
        if(index>=0) NativeWorld.Guest?.RequestEditor(target,"labels",new JObject { ["changes"]=new JArray(new JObject { ["index"]=index,["condition"]=__instance.GetSelectedConditionIdentifier() }) });
        return false;
    }
}
[HarmonyPatch(typeof(LabelerUIManager),nameof(LabelerUIManager.Repeat))]
internal static class ReplicaRepeatLabels
{
    private static bool Prefix()
    {
        if(!NativeGuest.Active) return true;
        var ui=LabelerUIManager.Instance;
        var target=(GameItem)AccessTools.Field(typeof(LabelerUIManager),"curItem").GetValue(ui);
        var labels=(Dictionary<string,string>)AccessTools.Field(typeof(LabelerUIManager),"lastLabels").GetValue(null);
        if(target==null) return false;
        var changes=new JArray();
        for(int i=0;i<target.itemFeatures.Count;i++) if(labels.TryGetValue(target.itemFeatures[i].category,out string condition)) changes.Add(new JObject { ["index"]=i,["condition"]=condition });
        if(changes.Count>0) NativeWorld.Guest?.RequestEditor(target,"labels",new JObject { ["changes"]=changes,["repeat"]=true });
        return false;
    }
}
[HarmonyPatch(typeof(InspectionUIManager),nameof(InspectionUIManager.OnConfront))]
internal static class ReplicaConfront
{
    private static bool Prefix(InspectionUIManager __instance)
    {
        if(!NativeGuest.Active) return true;
        int mask=0,index=0; foreach(var toggle in new[]{__instance.toggle1,__instance.toggle2,__instance.toggle3,__instance.toggle4,__instance.toggle5}) { if(toggle.isOn) mask|=1<<index; index++; }
        if(NativeWorld.Guest?.RequestEditor(__instance.currentInspectingItem,"confront",new JObject { ["issues"]=mask })==true) __instance.CloseUI();
        return false;
    }
}
[HarmonyPatch(typeof(InspectionUIManager),nameof(InspectionUIManager.OnAppraisalServiceEstimated))]
internal static class ReplicaEstimate
{
    private static bool Prefix(InspectionUIManager __instance)
    {
        if(!NativeGuest.Active) return true;
        if(int.TryParse(__instance.apprisalServiceInputField.text,out int value) && NativeWorld.Guest?.RequestEditor(__instance.currentInspectingItem,"estimate",new JObject { ["value"]=value })==true) __instance.CloseUI();
        return false;
    }
}

// Appraisal typing is an actor-local draft. Only the Estimate button submits a
// business decision; snapshots must not overwrite half-entered input or mutate tags.
[HarmonyPatch(typeof(InspectionUIManager),nameof(InspectionUIManager.OnInspectItem))]
internal static class ReplicaAppraisalDraft
{
    internal static int Value;
    private static void Postfix(GameItem gameItem)
    { if(NativeGuest.Active) Value=gameItem.GetTagReadonly("APPRAISAL_DEFAULT_VALUE").GetInt(); }
}
[HarmonyPatch(typeof(InspectionUIManager),nameof(InspectionUIManager.UpdateAppraisalPanel))]
internal static class ReplicaAppraisalDisplay
{
    private static void Postfix(InspectionUIManager __instance)
    { if(NativeGuest.Active && __instance.appraisalPanel.activeSelf && !__instance.isEditingAppraisalField) __instance.apprisalServiceInputField.SetTextWithoutNotify(ReplicaAppraisalDraft.Value.ToString()); }
}
[HarmonyPatch(typeof(InspectionUIManager),nameof(InspectionUIManager.OnValueChanged))]
internal static class ReplicaAppraisalTyping
{
    private static bool Prefix(InspectionUIManager __instance)
    { if(!NativeGuest.Active) return true; if(int.TryParse(__instance.apprisalServiceInputField.text,out int value)) ReplicaAppraisalDraft.Value=value; return false; }
}
[HarmonyPatch(typeof(InspectionUIManager),nameof(InspectionUIManager.FinishEditingAppraisalField))]
internal static class ReplicaAppraisalEditEnd
{
    private static bool Prefix(InspectionUIManager __instance)
    { if(!NativeGuest.Active) return true; __instance.isEditingAppraisalField=false; StoreUIManager.Instance.StopTyping(); if(int.TryParse(__instance.apprisalServiceInputField.text,out int value)) ReplicaAppraisalDraft.Value=value; return false; }
}
[HarmonyPatch(typeof(InspectionUIManager),nameof(InspectionUIManager.ChangeAppraisalServicePrice))]
internal static class ReplicaAppraisalIncrement
{
    private static bool Prefix(int mod)
    { if(!NativeGuest.Active) return true; ReplicaAppraisalDraft.Value=(int)Math.Max(0,Math.Min(int.MaxValue,(long)ReplicaAppraisalDraft.Value+mod)); return false; }
}
