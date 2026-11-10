using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ProbablyCoop;

// Build the exact original response UI, but keep its inventory/window bindings
// outside the host projection. A local option icon is never shared stock.
internal sealed class NativeLocalResponseFrontend
{
    private static readonly FieldInfo Choices=AccessTools.Field(typeof(EmporiumEntry),"choices");
    private sealed class State
    {
        internal PixelWindow Window,Closable;
        internal GameGridInventory Inventory,ClosableInventory;
        internal object Actions;
        internal static State Read(EmporiumEntry e)=>new State { Window=e.responseWindow,Closable=e.responseWindowClosable,
            Inventory=e.responseInventory,ClosableInventory=e.responseInventoryClosable,Actions=Choices.GetValue(e) };
        internal void Install(EmporiumEntry e)
        {
            e.responseWindow=Window; e.responseWindowClosable=Closable;
            e.responseInventory=Inventory; e.responseInventoryClosable=ClosableInventory; Choices.SetValue(e,Actions);
        }
    }
    private readonly EmporiumEntry entry;
    private State state;
    private bool closed;
    internal NativeLocalResponseFrontend()
    {
        entry=EmporiumEntry.Instance;
        var previous=State.Read(entry);
        try { entry.InitResponseWindow(); state=State.Read(entry); state.Actions=null; }
        finally { previous.Install(entry); }
    }
    private void Bound(Action action)
    {
        var previous=State.Read(entry); state.Install(entry);
        try { action(); }
        finally { state=State.Read(entry); previous.Install(entry); }
    }
    internal void Show(StoreResponse response,Action close)
    {
        Bound(()=>entry.ShowResponseWindow(response));
        if(response.closable) state.Closable.closeCallback=close;
    }
    internal bool Visible=>!closed && (state.Window.IsVisible() || state.Closable.IsVisible());
    internal List<GameItem> Items=>state.ClosableInventory.childItems;
    internal void Close()
    {
        if(closed) return; closed=true;
        Bound(()=>entry.HideResponseWindow());
        GraphHandler.windowGraphNodeRoots?.Remove(state.Window);
        GraphHandler.windowGraphNodeRoots?.Remove(state.Closable);
        UnityEngine.Object.Destroy(state.Window.handler.gameObject);
        UnityEngine.Object.Destroy(state.Closable.handler.gameObject);
        state.Actions=null;
    }
}
