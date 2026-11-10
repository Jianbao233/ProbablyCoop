using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Core;

namespace ProbablyCoop;

// Rename is a capability of the native storage-tab factory, not an item-ID
// list. No native delegate or network-selected method name crosses the wire.
internal static class NativeStorageTabs
{
    private sealed class Witness { internal GameItem Bay; internal Action<int> Callback; }
    private sealed class Replica { internal uint Bay; internal Action<int> Callback; }
    private static readonly ConditionalWeakTable<MachineryTabs,Witness> Native=new();
    private static readonly ConditionalWeakTable<MachineryTabs,Replica> Replicas=new();
    internal static MachineryTabs Tabs(GameItem bay)=>(bay?.contentWindow?.childElement as MachineryPanelRow)?.GetPanel("storage")?.content as MachineryTabs;
    internal static bool TryNativeOwner(MachineryTabs tabs,out GameItem bay)
    {
        bay=null;
        if(!Native.TryGetValue(tabs,out var witness) || !ReferenceEquals(witness.Callback,tabs.onRenameRequest) ||
            !ReferenceEquals(Tabs(witness.Bay),tabs)) return false;
        bay=witness.Bay; return true;
    }
    internal static void BindReplica(MachineryTabs tabs,uint bay)
    {
        if(bay==0) { tabs.onRenameRequest=null; Replicas.Remove(tabs); return; }
        if(!Replicas.TryGetValue(tabs,out var value))
        {
            value=new Replica();
            var binding=value;
            value.Callback=index=>NativeWorld.Guest?.OpenStorageTabEditor(tabs,binding.Bay,index);
            Replicas.Add(tabs,value);
        }
        value.Bay=bay; tabs.onRenameRequest=value.Callback;
    }
    [HarmonyPatch(typeof(StorageExpansionHelper),nameof(StorageExpansionHelper.InitTabNames))]
    private static class BindNative
    {
        private static void Postfix(GameItem bay)
        {
            var tabs=Tabs(bay);
            if(NativeGuest.Active || tabs?.onRenameRequest==null ||
                tabs.onRenameRequest.Method.Module.Assembly!=typeof(GameItem).Assembly) return;
            Native.Remove(tabs); Native.Add(tabs,new Witness { Bay=bay,Callback=tabs.onRenameRequest });
        }
    }
}

internal sealed partial class NativeWorld
{
    private sealed class StorageTabEditor
    {
        internal GameItem Bay;
        internal MachineryTabs Tabs;
        internal int Index;
        internal ulong Context;
        internal string Expected,Desired,Sent;
        internal ActivityRequests.Entry Attempt;
        internal Action<string> Done;
    }
    private StorageTabEditor storageTabEditor;
    private readonly List<StorageTabEditor> storageTabEdits=new();
    private static readonly System.Reflection.FieldInfo StorageTabDone=AccessTools.Field(typeof(LabelerBlueUIManager),"onTextDone");
    private static bool StorageTabOwnsCallback(StorageTabEditor view)=>LabelerBlueUIManager.Instance!=null &&
        ReferenceEquals(StorageTabDone.GetValue(LabelerBlueUIManager.Instance),view.Done);
    private bool StorageTabVisible(StorageTabEditor view)=>LabelerBlueUIManager.Instance?.panel.activeSelf==true &&
        StorageTabOwnsCallback(view);
    internal void OpenStorageTabEditor(MachineryTabs tabs,uint bayId,int index)
    {
        if(host || !Ready || !model.Objects.TryGetValue(bayId,out var value) || value is not GameItem bay ||
            !ReferenceEquals(NativeStorageTabs.Tabs(bay),tabs) || index<0 || index>=tabs.count ||
            !GeneralHelper.IsItemEffectivelyOwned(bay) || !Accessible(bay.parentInventory)) return;
        if(storageTabEdits.Count>=16) { CommandNotice("待确认的分页名称过多，请稍后再试。"); return; }
        var view=new StorageTabEditor { Bay=bay,Tabs=tabs,Index=index,Context=observedOperationContext,
            Expected=StorageExpansionHelper.GetTabName(bay,index) ?? "" };
        view.Done=text=>RequestStorageTabRename(view,text);
        // Retire the previous closure before the original input loses focus.
        storageTabEditor=view;
        storageTabEdits.Add(view);
        LabelerBlueUIManager.Instance.OpenUI(view.Expected,view.Done,()=>view.Done(""),StorageExpansionHelper.TAB_NAME_MAX_LENGTH);
    }
    private bool RequestStorageTabRename(StorageTabEditor view,string text)
    {
        // Native CloseUI disables the panel before TMP emits its last
        // onEndEdit. That callback still belongs to this editor until the
        // original CloseUI clears it; scene teardown retires our binding first.
        if(!ReferenceEquals(storageTabEditor,view) || !StorageTabOwnsCallback(view)) return false;
        text=(text ?? "").Replace('\n',' ').Trim();
        if(text.Length>StorageExpansionHelper.TAB_NAME_MAX_LENGTH) text=text.Substring(0,StorageExpansionHelper.TAB_NAME_MAX_LENGTH);
        view.Desired=text;
        return SubmitStorageTabRename(view);
    }
    private bool SubmitStorageTabRename(StorageTabEditor view)
    {
        if(view.Context!=observedOperationContext || !TryData(view.Bay,out var data) ||
            !ReferenceEquals(NativeStorageTabs.Tabs(view.Bay),view.Tabs) || !Accessible(view.Bay.parentInventory)) return false;
        string text=view.Desired;
        if(text==null) return false;
        if(view.Attempt!=null || text==view.Expected) return true;
        var command=new JObject { ["kind"]="storageTab",["id"]=(uint)data["entity"],["token"]=data["moveToken"],
            ["tabs"]=model.ReplicaId(view.Tabs),["index"]=view.Index,["count"]=view.Tabs.count,["previous"]=view.Expected,["text"]=text };
        if(!Submit(command)) return false;
        view.Sent=text;
        view.Attempt=UiRequestState(LastSubmittedUiRequest);
        if(NativeBackgroundQa.Enabled) NativeBackgroundQa.RememberStorageTabCommand(command);
        return true;
    }
    private ushort ExecuteStorageTabRename(JObject command)
    {
        if(shop.Settling) return (ushort)ShopResult.Busy;
        if(!CurrentItem(command,out var bay) || !Accessible(bay.parentInventory) || !GeneralHelper.IsItemEffectivelyOwned(bay))
            return (ushort)ShopResult.Stale;
        var tabs=NativeStorageTabs.Tabs(bay);
        if(tabs==null || !NativeStorageTabs.TryNativeOwner(tabs,out var owner) || !ReferenceEquals(owner,bay) ||
            command["tabs"]?.Type!=JTokenType.Integer || !model.Objects.TryGetValue((uint)command["tabs"],out var tabObject) ||
            !ReferenceEquals(tabObject,tabs) || (int?)command["count"]!=tabs.count || command["index"]?.Type!=JTokenType.Integer)
            return (ushort)ShopResult.Stale;
        int index=(int)command["index"];
        if(index<0 || index>=tabs.count || (string)command["previous"]!=(StorageExpansionHelper.GetTabName(bay,index) ?? ""))
            return (ushort)ShopResult.Stale;
        if(HandTouches(bay)) return (ushort)ShopResult.Busy;
        if(command["text"]?.Type!=JTokenType.String || ((string)command["text"]).Length>StorageExpansionHelper.TAB_NAME_MAX_LENGTH)
            return (ushort)ShopResult.Unavailable;
        StorageExpansionHelper.SetTabName(bay,index,(string)command["text"]);
        nextCapture=0; return (ushort)ShopResult.Applied;
    }
    private void ReconcileStorageTabEditor()
    {
        foreach(var view in storageTabEdits.ToArray()) ReconcileStorageTabEditor(view);
    }
    private void ReconcileStorageTabEditor(StorageTabEditor view)
    {
        bool visible=ReferenceEquals(storageTabEditor,view) && StorageTabVisible(view);
        if(!visible && ReferenceEquals(storageTabEditor,view)) storageTabEditor=null;
        if(view.Context!=observedOperationContext || !TryData(view.Bay,out _) ||
            !ReferenceEquals(NativeStorageTabs.Tabs(view.Bay),view.Tabs) || view.Index>=view.Tabs.count ||
            view.Bay.parentInventory==null || !Accessible(view.Bay.parentInventory))
        {
            storageTabEdits.Remove(view);
            if(ReferenceEquals(storageTabEditor,view)) storageTabEditor=null;
            if(visible) { LabelerBlueUIManager.Instance.CloseUI(); StoreUIManager.Instance.StopTyping(); }
            return;
        }
        var attempt=view.Attempt;
        if(attempt==null) { if(!visible) storageTabEdits.Remove(view); return; }
        if(!attempt.Result.HasValue ||
            attempt.Result==(ushort)ShopResult.Applied && attempt.Serial>frontendThrough) return;
        view.Expected=StorageExpansionHelper.GetTabName(view.Bay,view.Index) ?? "";
        view.Attempt=null;
        // Only a later explicit edit depends on the successful previous edit.
        // A rejected draft is not automatically rebased over the other player.
        if(attempt.Result!=(ushort)ShopResult.Applied || view.Expected!=view.Sent) view.Desired=null;
        else if(view.Desired!=null && view.Desired!=view.Expected) SubmitStorageTabRename(view);
        // Closing the panel never drops a later explicit OnEditEnd already
        // queued behind an in-flight edit. It also never reopens that panel.
        if(!visible && view.Attempt==null) storageTabEdits.Remove(view);
    }
    private void ClearStorageTabEditor()
    {
        var view=storageTabEditor; storageTabEditor=null; storageTabEdits.Clear();
        if(view!=null && StorageTabVisible(view))
        { LabelerBlueUIManager.Instance.CloseUI(); StoreUIManager.Instance.StopTyping(); }
    }
}
