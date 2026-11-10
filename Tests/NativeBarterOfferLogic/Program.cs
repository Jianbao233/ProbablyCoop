using System;
using System.Reflection;
using Newtonsoft.Json.Linq;
using ProbablyCoop;
using ProbablyCoop.Core;
internal static class Program
{
    private static int checks;
    private static void Check(bool value,string name) { checks++; if(!value) throw new Exception(name); }
    private static NativeWorld Fresh()
    {
        PlayerStore.Instance=new PlayerStore(); EmporiumEntry.Instance=new EmporiumEntry(); NegociationUIManager.Instance=new NegociationUIManager();
        NegociationUIManager.Locked=false; MapUIManager.Instance.IsOutside=false; DialogUIManager.Instance.Current=null;
        NativeActorSession.Current=null; NativeGuest.Active=false; NativeWorld.RemoteUiExecution=null;
        EmporiumEntry.Instance.frontInvinvElement.childItems.Add(new GameItem());
        return NativeWorld.Host=new NativeWorld(true);
    }
    public static int Main()
    {
        var w=Fresh(); var first=w.Command(1);
        Check(w.Execute(first)==(ushort)ShopResult.Applied,"next applied");
        Check(w.CaptureAt==0,"accepted native change schedules projection");
        Check(PlayerStore.Instance.currentClientInstance.Client.barterOffer.currentSetIndex==1,"next index");
        Check(w.Execute(first)==(ushort)ShopResult.Stale,"repeated prior index stale");
        Check(w.Execute(w.Command(-1))==(ushort)ShopResult.Applied,"previous applied");
        Check(w.Execute(first)==(ushort)ShopResult.Stale && NegociationUIManager.Instance.Cycles==2,"ABA goods proof stale, factories twice");
        foreach(string field in new[]{"client","index","count","token","goods"})
        {
            w=Fresh(); var command=w.Command(1); command[field]=field is "token" or "goods" ? new JValue("wrong") : new JValue(999);
            Check(w.Execute(command)==(ushort)ShopResult.Stale,"stale "+field);
        }
        foreach(string field in new[]{"offset","client","index","count","token","goods"})
        {
            w=Fresh(); var command=w.Command(1); command.Remove(field);
            Check(w.Execute(command)==(ushort)ShopResult.Unavailable,"missing "+field);
        }
        w=Fresh(); Check(w.Execute(w.Command(0))==(ushort)ShopResult.Unavailable,"offset zero");
        foreach(int place in new[]{0,1})
        {
            w=Fresh(); var e=EmporiumEntry.Instance; var item=new GameItem();
            (place==0 ? e.frontInvinvElement : e.backInvinvElement).childItems.Add(item); w.Held.Items.Add(item);
            Check(w.Execute(w.Command(1))==(ushort)ShopResult.Busy && NegociationUIManager.Instance.Cycles==0,"held offer busy "+place);
            Check(!w.AllowNativeBarterOffer(1),"host selector held protection "+place);
            Check(!w.AllowNativeBarterAccept(),"host accept held protection "+place);
        }
        w=Fresh(); w.Held.Items.Add(new GameItem()); Check(w.Execute(w.Command(1))==(ushort)ShopResult.Applied,"unrelated hand allowed");
        w=Fresh(); var owned=new GameItem {Owned=true}; EmporiumEntry.Instance.backInvinvElement.childItems.Add(owned); w.Held.Items.Add(owned);
        Check(w.AllowNativeBarterOffer(1),"owned food not destroyed by selector"); Check(!w.AllowNativeBarterAccept(),"owned held input blocks accept");
        w=Fresh(); w.SetSettling(true); Check(w.Execute(w.Command(1))==(ushort)ShopResult.Busy,"settling busy");
        w=Fresh(); NativeActorSession.Current=new NativeActorSession {Faulted=true}; Check(!w.AllowNativeBarterAccept(),"faulted accept blocked");
        w=Fresh(); NativeActorSession.Current=new NativeActorSession {Terminal=true}; Check(w.Execute(w.Command(1))==(ushort)ShopResult.Busy,"terminal selector blocked");
        w=Fresh(); DialogUIManager.Instance.Current=new Dialogue {isMainDialog=true}; Check(w.Execute(w.Command(1))==(ushort)ShopResult.Busy,"main dialogue busy");
        w=Fresh(); PlayerStore.Instance.currentClientInstance.Client.isBusinessComplete=true; Check(w.Execute(w.Command(1))==(ushort)ShopResult.Unavailable,"completed unavailable");
        w=Fresh(); MapUIManager.Instance.IsOutside=true; Check(w.Execute(w.Command(1))==(ushort)ShopResult.Unavailable,"outside unavailable");
        w=Fresh(); NativeWorld.Guest=new NativeWorld(false); Check(NativeWorld.Guest.RequestBarterOffer(1),"guest submits");
        Check(NegociationUIManager.Instance.Cycles==0 && (uint)NativeWorld.Guest.Sent["client"]!=0,"guest never runs factory and uses customer instance");
        var finalizer=typeof(NativeBarterOfferRequests).GetMethod("Finalizer",BindingFlags.Static|BindingFlags.NonPublic);
        var actor=new NativeActorSession(); var error=new Exception("partial factory");
        finalizer.Invoke(null,new object[]{error,true,actor}); Check(actor.Faulted && actor.Failure==error,"native cycle error marks session");
        actor=new NativeActorSession(); finalizer.Invoke(null,new object[]{error,false,actor}); Check(!actor.Faulted,"skipped original does not mark session");
        Console.WriteLine("PASS barter selector production logic with STUB game model checks="+checks+"; NOT Unity/Harmony/gameplay validation"); return 0;
    }
}
