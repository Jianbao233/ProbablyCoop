using System.Collections.Generic;
using System.Linq;

namespace ProbablyCoop;

internal sealed partial class NativeBackgroundQa
{
    private const string DenseTag = "PROBABLY_COOP_QA_DENSE";
    private int denseCount;
    private readonly List<GameItem> denseItems = new List<GameItem>();
    private ulong denseLastApplied;
    private int denseSamples,denseNodes,denseUnchanged,denseRoots,denseUnchangedRoots;
    private readonly SortedDictionary<string,int> denseChanges=new SortedDictionary<string,int>(System.StringComparer.Ordinal);

    private void SampleDenseProjection()
    {
        if(denseCount==0) return;
        var world=NativeWorld.Guest;
        if(world.QaAppliedRevision==denseLastApplied) return;
        denseLastApplied=world.QaAppliedRevision;
        var index=world.QaAppliedIndex;
        denseSamples++; denseNodes+=index.Nodes.Count; denseUnchanged+=index.Unchanged.Count;
        foreach(Newtonsoft.Json.Linq.JObject root in world.QaAppliedRoots)
            foreach(var item in root["items"])
            {
                denseRoots++;
                if(index.Unchanged.Contains((uint)item["data"])) denseUnchangedRoots++;
            }
        foreach(string key in index.ChangedTopLevel)
        {
            denseChanges.TryGetValue(key,out int changed);
            denseChanges[key]=changed+1;
        }
    }

    private void ReportDenseProjection()
    {
        if(denseCount==0) return;
        Require(denseSamples>=soakCommits && denseRoots>=denseSamples*denseCount,"insufficient dense applied graph samples");
        Record("TRACE dense-projection-reuse",$"snapshots={denseSamples} nodes={denseNodes} identicalNodes={denseUnchanged} topItems={denseRoots} identicalTopItems={denseUnchangedRoots} changedSections="+
            string.Join(",",denseChanges.Select(entry=>entry.Key+"="+entry.Value)));
    }

    private void SeedDenseStock()
    {
        if (denseCount == 0) return;
        var store = EmporiumEntry.Instance;
        // Keep separate physical objects even if the game's normal transfer
        // would stack them. Use only valid empty cells on real inventory grids.
        var grids = new[] { store.afterhourInventory, store.bazarLeftinvElement, store.invElement };
        for (int i = 0; i < denseCount; i++)
        {
            var item = DirectoryMaster.Item(i % 2 == 0 ? "toilet_paper" : "processed_meat");
            GeneralHelper.SetItemOwned(item, true);
            item.EnableTag(DenseTag);
            bool placed = grids.Any(grid => TryPlaceInEmptyCell(item,grid));
            Require(placed, "cannot seed a distinct dense stock item at index " + i);
            denseItems.Add(item);
        }
        Record("BEGIN dense-native-stock", "count=" + denseItems.Count + " additional owned items in native grids");
    }

    private static bool TryPlaceInEmptyCell(GameItem item,GameGridInventory grid)
    {
        if(grid==null) return false;
        var shape=new GridShapeBuilder(item.modifiedShape);
        int quantity=item.unitCount;
        for(int y=0;y<grid.inventoryShape.height;y++)
        for(int x=0;x<grid.inventoryShape.width;x++)
        {
            shape.SetTransform(x,y,false,0);
            var slot=grid.TryInventorySlot(item,quantity,shape);
            if(slot?.IsValid()!=true || slot.targetItem!=null || slot.numTransfer<quantity) continue;
            return slot.TryAcceptOnce(quantity)==quantity && item.parentInventory==grid;
        }
        return false;
    }

    private void CheckDenseStock()
    {
        if (denseCount == 0) return;
        int count = NativeWorld.Guest.Objects.Values.OfType<GameItem>().Count(i => i.IsTag(DenseTag) && i.parentInventory != null);
        Require(count == denseCount, "guest dense stock mismatch: " + count);
    }

    private void ReleaseDenseStockAfterProbe()
    {
        if (denseItems.Count == 0 || qaWater.parentInventory != Container(qaMachine)) return;
        Require(NativeCaptureComparison.Current==null,"paired comparison did not finish before dense items were removed");
        foreach (var item in denseItems) item.Destroy();
        Require(denseItems.All(i => i.parentInventory == null), "dense fixture still attached before normal gameplay");
        Record("PASS dense-native-cleared", "removed only the " + denseItems.Count + " tagged QA stock objects before daily saves");
        denseItems.Clear();
    }
}

internal sealed partial class NativeWorld
{
    internal ulong QaAppliedRevision=>applied;
    internal NativeSnapshotIndex QaAppliedIndex=>appliedIndex;
    internal Newtonsoft.Json.Linq.JArray QaAppliedRoots=>appliedRoots;
}
