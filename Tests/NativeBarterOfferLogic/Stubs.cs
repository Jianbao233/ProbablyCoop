using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
namespace HarmonyLib
{
    [AttributeUsage(AttributeTargets.Class)] public sealed class HarmonyPatch:Attribute { public HarmonyPatch(Type t,string name){} }
    [AttributeUsage(AttributeTargets.Method)] public sealed class HarmonyPriority:Attribute { public HarmonyPriority(int p){} }
    public static class Priority { public const int First=800; }
}
public class GraphNodeStorage { }
public class GameItem:GraphNodeStorage { public bool Owned; public int unitCount=1; }
public class GameInventory:GraphNodeStorage { public List<GameItem> childItems=new List<GameItem>(); }
public class EmporiumEntry
{
    public static EmporiumEntry Instance=new EmporiumEntry();
    public GameInventory frontInvinvElement=new GameInventory(),backInvinvElement=new GameInventory();
    public List<GameItem> GetAllNonOwnedItem()=>frontInvinvElement.childItems.Concat(backInvinvElement.childItems).Where(i=>!i.Owned).ToList();
}
public static class GeneralHelper { public static bool IsItemOwned(GameItem i)=>i.Owned; }
public class BarterOffer { public int currentSetIndex,Count=3; public bool UseOfferSets()=>Count>0; public int OfferSetCount()=>Count; }
public class StoreClient { public enum ClientIntent { BARTER,BUY } public ClientIntent clientIntent; public bool isBusinessComplete,isIntroduced=true; public BarterOffer barterOffer=new BarterOffer(); }
public class StoreClientInstance { public StoreClient Client=new StoreClient(); public StoreClient GetClientBlueprint()=>Client; }
public class PlayerStore
{
    public static PlayerStore Instance=new PlayerStore();
    public StoreClientInstance currentClientInstance=new StoreClientInstance();
    public enum StoreState {OPEN,CLOSED} public StoreState storeState; public bool isClientArrived=true;
    public void AcceptBarter(){}
}
public class MapUIManager { public static MapUIManager Instance=new MapUIManager(); public bool IsOutside; }
public class Dialogue { public bool isMainDialog; }
public class DialogUIManager { public static DialogUIManager Instance=new DialogUIManager(); public Dialogue Current; public Dialogue GetCurrentDialog()=>Current; }
public class NegociationUIManager
{
    public static NegociationUIManager Instance=new NegociationUIManager(); public static bool Locked; public int Cycles;
    public static bool IsTradeLocked()=>Locked;
    public void OnLeftArrowButtonClick()=>Cycle(-1); public void OnRightArrowButtonClick()=>Cycle(1);
    private void Cycle(int offset)
    {
        Cycles++; var offer=PlayerStore.Instance.currentClientInstance.Client.barterOffer;
        offer.currentSetIndex=(offer.currentSetIndex+offset+offer.Count)%offer.Count;
        var e=EmporiumEntry.Instance;
        e.frontInvinvElement.childItems.RemoveAll(i=>!i.Owned); e.backInvinvElement.childItems.RemoveAll(i=>!i.Owned);
        e.frontInvinvElement.childItems.Add(new GameItem());
    }
    public void OnOfferAcceptClick(){}
}
namespace ProbablyCoop.Core { internal enum ShopResult:ushort { None,Applied,Stale,Unavailable,NoSpace,Busy,Failed } }
namespace ProbablyCoop
{
    internal sealed class NativeActorSession
    {
        internal static NativeActorSession Current; internal bool Faulted,Terminal; internal Exception Failure;
        internal void MarkFault(Exception error) { Failure=error; Faulted=true; }
    }
    internal static class NativeGuest { internal static bool Active; }
    internal class TestModel
    {
        private readonly Dictionary<object,uint> ids=new Dictionary<object,uint>(); private uint next=1;
        internal uint Id(object value) { if(!ids.TryGetValue(value,out uint id)) ids[value]=id=next++; return id; }
        internal uint ReplicaId(object value)=>Id(value);
    }
    internal class TestHeld
    {
        internal HashSet<GraphNodeStorage> Items=new HashSet<GraphNodeStorage>();
        internal bool TouchesItem(GraphNodeStorage item)=>Items.Contains(item);
        internal bool TouchesContents(GameInventory inventory)=>Items.Contains(inventory) || inventory.childItems.Any(TouchesItem);
    }
    internal class TestShop { internal bool Settling; }
    internal sealed partial class NativeWorld
    {
        internal static NativeWorld Host,Guest,RemoteUiExecution;
        private bool host; private TestModel model=new TestModel(); private TestShop shop=new TestShop();
        private uint replicaCustomerId; private string tradeToken="proof"; private double nextCapture=1;
        internal bool Ready=true; internal TestHeld Held=new TestHeld(); internal JObject Sent; internal string Notice;
        internal NativeWorld(bool host) { this.host=host; replicaCustomerId=model.Id(PlayerStore.Instance.currentClientInstance); }
        private TestHeld CaptureHostHand(bool includePartner)=>Held;
        private string TradeToken()=>tradeToken;
        private void SealOperation(JObject command)=>command["context"]=1;
        private bool Submit(JObject command) { Sent=command; return true; }
        private void CommandNotice(string text)=>Notice=text;
        internal JObject Command(int offset)
        {
            var c=PlayerStore.Instance.currentClientInstance;
            return new JObject { ["kind"]="barterOffer",["offset"]=offset,["client"]=model.Id(c),["index"]=c.Client.barterOffer.currentSetIndex,
                ["count"]=c.Client.barterOffer.Count,["token"]=tradeToken,["goods"]=BarterGoodsProof() };
        }
        internal ushort Execute(JObject command)=>ExecuteBarterOffer(command);
        internal void SetSettling(bool value)=>shop.Settling=value;
        internal double CaptureAt=>nextCapture;
    }
}
