using System.Linq;
using HarmonyLib;
using ProbablyCoop.Core;

namespace ProbablyCoop;
internal sealed partial class NativeBackgroundQa
{
    private int sortStep;
    private double sortNext,sortUntil;
    private GameItem[] sortItems;
    private GridShape[] sortOrigins;
    private bool RunSortQa(ReplicaLink link,double now)
    {
        if(sortStep==5) return true;
        if(now<sortNext || QaWaiting(link)) return false;
        var inventory=EmporiumEntry.Instance.invElement;
        if(sortStep==0)
        {
            Require(InventorySortHelper.CanSort(inventory),"native sort fixture unavailable");
            sortItems=inventory.childItems.ToArray(); sortOrigins=sortItems.Select(i=>i.modifiedShape.Clone()).ToArray();
            TriggerNativeSort(inventory,"buttonSortBigEnd",false);
            Require(QaWaiting(link),"native right-click sort emitted no request"); sortStep=1; sortUntil=now+25; return false;
        }
        Require(now<sortUntil,"sort/undo timeout");
        if(sortStep==1 || sortStep==3)
        {
            Require(QaResult(link)==(ushort)ShopResult.Applied,"native sort command rejected: "+QaResult(link));
            sortStep++; sortNext=now+0.8;
            return false;
        }
        if(sortStep==4)
        {
                for(int i=0;i<sortItems.Length;i++)
                {
                    var shape=sortItems[i].modifiedShape; var original=sortOrigins[i];
                    Require(sortItems[i].parentInventory==inventory && shape.minX==original.minX && shape.minY==original.minY &&
                        shape.orientation==original.orientation && shape.flipped==original.flipped,"sort undo did not restore exact native item transform");
                }
                Require(!InventorySortHelper.MayUndo(inventory),"guest retained consumed sort undo");
                Record("PASS native-inventory-sort-undo","original right-click menu callbacks -> host native sorter/undo; exact item transforms and original UI undo availability restored");
                sortStep=5; return true;
        }
        Require(InventorySortHelper.MayUndo(inventory) && InventorySortHelper.CanUndo(inventory),"original guest sort menu does not offer undo");
        Require(sortItems.Any((item)=> { int i=System.Array.IndexOf(sortItems,item); return item.modifiedShape.minX!=sortOrigins[i].minX || item.modifiedShape.minY!=sortOrigins[i].minY; }),"sort did not exercise any movement");
        TriggerNativeSort(inventory,"buttonSortUndo",true);
        Require(QaWaiting(link),"native right-click undo emitted no request"); sortStep=3; return false;
    }
    private static void TriggerNativeSort(GameGridInventory inventory,string button,bool undo)
    {
        var handler=ItemContextHandler.current;
        handler.Check();
        AccessTools.Field(typeof(ItemContextHandler),"currentInventory").SetValue(handler,inventory);
        AccessTools.Field(typeof(ItemContextHandler),"canSort").SetValue(handler,!undo);
        AccessTools.Field(typeof(ItemContextHandler),"canUndoSort").SetValue(handler,undo);
        var element=AccessTools.Field(typeof(ItemContextHandler),button).GetValue(handler);
        AccessTools.Method(typeof(ItemContextHandler),"TriggerButton").Invoke(handler,new[]{element});
    }
}
