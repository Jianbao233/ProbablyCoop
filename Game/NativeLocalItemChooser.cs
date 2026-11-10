using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace ProbablyCoop;

// Original choose-item frontend, fed with read-only presentation items. These
// items never enter the shared model, mutate shared items or get saved.
internal static class NativeLocalItemChooser
{
    internal sealed class Choice
    {
        internal GameItemElement Source;
        internal string Label, Atlas="Items/items_container", Sprite="bag";
        internal GridShape Shape;
        internal RichText Normal,Advanced;
        internal bool Enabled=true,Response;
        internal Func<bool> Select;
    }
    private static readonly ConditionalWeakTable<GameItemElement,Choice> owned=new ConditionalWeakTable<GameItemElement,Choice>();
    private static object activeOwner;
    private static Action cancelled;
    private static bool response;
    private static NativeLocalResponseFrontend responseFrontend;
    internal static bool ResponseVisible=>responseFrontend?.Visible==true;
    internal static List<GameItem> ResponseItems=>responseFrontend?.Items;
    private static bool showing;
    internal static bool Owns(GameItemElement item)=>owned.TryGetValue(item,out _);
    internal static GameItemElement SourceOf(GameItemElement item)=>owned.TryGetValue(item,out var choice) ? choice.Source : null;
    internal static bool Ability(GameItem item,string method,out bool value)
    {
        value=false;
        if(item is not GameItemElement element || !owned.TryGetValue(element,out var choice)) return false;
        value=choice.Response && choice.Enabled && method is "MayActivateSlotItem" or "CanActivateSlotItem";
        return true;
    }
    internal static bool Activate(GameItem item)
    {
        if(item is not GameItemElement element || !owned.TryGetValue(element,out var choice)) return false;
        if(choice.Response && choice.Enabled) choice.Select?.Invoke();
        return true;
    }
    private static Dictionary<GameItem,Choice> Build(IEnumerable<Choice> choices,bool forResponse)
    {
        var actions=new Dictionary<GameItem,Choice>();
        foreach(var choice in choices)
        {
            choice.Response=forResponse;
            var source=choice.Source;
            var item=new GameItemElement("probably_coop_local_choice",source?.spriteAtlasPath ?? choice.Atlas,source?.spritePath ?? choice.Sprite);
            var shape=choice.Shape ?? source?.shape;
            if(shape!=null) { item.shape=shape.Clone(); item.modifiedShape=shape.Clone(); }
            item.name=choice.Label;
            item.SetAllMayFalse(); item.EnableTag("CHOICE_PREVIEW_TAG");
            if(forResponse)
            {
                GeneralHelper.SetItemOwned(item);
                item.activateDefault=true;
                item.mayInventoryAddItemFunc=(_,_)=>true;
            }
            owned.Add(item,choice);
            if(choice.Normal!=null) NativeReplicaTooltip.Bind(item,choice.Normal,choice.Advanced ?? choice.Normal);
            else if(source!=null) NativeReplicaTooltip.Bind(item,source.GetTooltip(),source.GetTooltipAdvanced());
            item.Validate();
            actions.Add(item,choice);
        }
        return actions;
    }
    internal static void Show(object owner,string title,IEnumerable<Choice> choices,Action onCancel=null,
        string no="Cancel",ButtonElement.Color color=ButtonElement.Color.Red,bool disabled=false,bool forResponse=false)
    {
        if(activeOwner!=null && !ReferenceEquals(activeOwner,owner))
        {
            var replaced=activeOwner; var onReplaced=cancelled;
            Close(replaced); onReplaced?.Invoke();
        }
        else Close(owner);
        var actions=Build(choices,forResponse);
        showing=true;
        try
        {
            if(forResponse)
            {
                responseFrontend=new NativeLocalResponseFrontend();
                responseFrontend.Show(new StoreResponse { title=title,closable=!disabled,
                    choices=actions.Select(pair=>(pair.Key,(Action)(()=>pair.Value.Select?.Invoke()))).ToList() },()=>
                {
                    if(!ReferenceEquals(activeOwner,owner)) return;
                    var callback=cancelled; Close(owner); callback?.Invoke();
                });
            }
            else
                OverlayHandler.current.ShowChooseItem(actions.Keys.ToList(),title,(Func<GameItem,bool>)(item=>
                {
                    if(!ReferenceEquals(activeOwner,owner) || !actions.TryGetValue(item,out var choice) ||
                        !choice.Enabled || choice.Select?.Invoke()!=true) return false;
                    activeOwner=null; cancelled=null; return true;
                }),null,no,color,disabled);
            activeOwner=owner; cancelled=onCancel; response=forResponse;
        }
        finally { showing=false; }
    }
    internal static void Close(object owner)
    {
        if(!ReferenceEquals(activeOwner,owner)) return;
        activeOwner=null; cancelled=null;
        if(response) { responseFrontend?.Close(); responseFrontend=null; }
        else OverlayHandler.current?.HideChooseItem();
    }
    [HarmonyPatch]
    private static class Replaced
    {
        private static IEnumerable<MethodBase> TargetMethods()=>AccessTools.GetDeclaredMethods(typeof(OverlayHandler))
            .Where(method=>method.Name==nameof(OverlayHandler.ShowChooseItem) || method.Name==nameof(OverlayHandler.HideChooseItem))
            .Concat(new[]{AccessTools.Method(typeof(EmporiumEntry),nameof(EmporiumEntry.ShowResponseWindow)),
                AccessTools.Method(typeof(EmporiumEntry),nameof(EmporiumEntry.HideResponseWindow))});
        private static void Prefix(MethodBase __originalMethod)
        {
            if(showing || NativeWorld.RemoteUiExecution!=null || activeOwner==null || response!=(__originalMethod.DeclaringType==typeof(EmporiumEntry))) return;
            if(response && __originalMethod.Name==nameof(EmporiumEntry.HideResponseWindow)) return;
            var callback=cancelled; activeOwner=null; cancelled=null; callback?.Invoke();
            if(response) { responseFrontend?.Close(); responseFrontend=null; }
        }
    }
}
