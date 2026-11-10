using System.Runtime.CompilerServices;
using HarmonyLib;

namespace ProbablyCoop;

// GameSlotInventory.UncheckedAccept mixes rendering with inventory callbacks.
// Reproduce only its reusable embedded-window contract for replica slots.
internal static class NativeReplicaSlotPresentation
{
    private sealed class Binding
    {
        internal PixelWindow Window;
        internal PixelElement Content;
    }
    private static readonly ConditionalWeakTable<GameSlotInventory,Binding> bindings=new ConditionalWeakTable<GameSlotInventory,Binding>();
    private static readonly System.Reflection.FieldInfo Stolen=AccessTools.Field(typeof(GameSlotInventory),"stolenUI");
    private static readonly System.Reflection.FieldInfo Inner=AccessTools.Field(typeof(GameSlotInventory),"stolenInner");

    internal static bool Matches(GameSlotInventory slot)
    {
        var window=slot.stealUI ? slot.childItem?.child as PixelWindow : null;
        var content=window?.childElement;
        if(content==null) return Stolen.GetValue(slot)==null;
        return ReferenceEquals(Stolen.GetValue(slot),content) && window.isLocked &&
            content.handler.rectTransform.parent==((TreeNodeRender)Inner.GetValue(slot)).rectTransform;
    }

    internal static void Release(GameSlotInventory slot)
    {
        if(bindings.TryGetValue(slot,out var binding))
        {
            if(binding.Content.handler?.gameObject!=null && binding.Window.handler?.gameObject!=null)
                binding.Content.SetRenderingParent(binding.Window);
            if(binding.Window.handler?.gameObject!=null) binding.Window.Unlock();
            bindings.Remove(slot);
        }
        else if(Stolen.GetValue(slot) is PixelElement content && slot.childItem?.child is PixelWindow window)
        {
            content.SetRenderingParent(window);
            window.Unlock();
        }
        Stolen.SetValue(slot,null);
    }

    internal static void Refresh(GameSlotInventory slot)
    {
        var window=slot.stealUI ? slot.childItem?.child as PixelWindow : null;
        var content=window?.childElement;
        if(content==null && Stolen.GetValue(slot)==null && !bindings.TryGetValue(slot,out _)) return;
        if(bindings.TryGetValue(slot,out var previous) &&
            ReferenceEquals(previous.Window,window) && ReferenceEquals(previous.Content,content) &&
            ReferenceEquals(Stolen.GetValue(slot),content) && Matches(slot)) return;
        if(NativeReplicaGraph.DeferSlot(slot)) return;
        Release(slot);
        if(window!=null && content!=null)
        {
            Stolen.SetValue(slot,content);
            content.SetRenderingParent(slot);
            content.handler.rectTransform.SetParent(((TreeNodeRender)Inner.GetValue(slot)).rectTransform,false);
            window.Lock();
            bindings.Add(slot,new Binding { Window=window,Content=content });
        }
        NativeReplicaGraph.Validate(slot);
    }
}
