#if PROBABLY_COOP_GRID_QA
using System;
using System.Collections.Generic;
using System.Linq;
using ProbablyCoop.Sync;
using UnityEngine;

namespace ProbablyCoop;

internal sealed partial class NativeInventoryPilot
{
    private sealed class GridQa : IDisposable
    {
        private const byte Signal = 7;
        private const int Juice = 1001, Paper = 1002, Bag = 1003;
        private const int Stock = 1010, Machine = 1011, Water = 1012, Strip = 1013;
        private const string StackId = "probably_coop_qa_stack";
        private const ulong LastSequence = 11;
        private static readonly InventoryAddress BagContents = new InventoryAddress(InventoryRoot.Nested, Bag);
        private static readonly InventoryAddress[] Inventories = {
            Address(InventoryRoot.Main), Address(InventoryRoot.FrontCounter),
            Address(InventoryRoot.BackCounter), Address(InventoryRoot.Showcase), BagContents,
        };
        private readonly NativeInventoryPilot pilot;
        private readonly bool rejoining = Environment.GetCommandLineArgs().Contains("--probably-coop-grid-rejoin");
        private GameItem juice, paper, bag, stock, machine, water, strip;
        private GameInventory bottleSlot;
        private int adds, removes, phase;
        private bool raceReady, raceAttempted, done, holdChecked;

        internal GridQa(NativeInventoryPilot pilot)
        {
            this.pilot = pilot;
            pilot.room.Datagram += OnDatagram;
        }

        internal static void AddFixtures(List<InventoryEntry> rows)
        {
            rows.Add(new InventoryEntry(Stock, StackId, 8, Placement(InventoryRoot.Main, 0, 6)));
            rows.Add(new InventoryEntry(Machine, "water_purifier", 1, Placement(InventoryRoot.Main, 10, 5)));
            rows.Add(new InventoryEntry(Strip, "water_test_strip", 1, Placement(InventoryRoot.Main, 21, 5)));
            var bottle = DirectoryMaster.Item("bottled_water");
            Check(bottle != null, "water fixture uses the registered original bottle");
            WaterHelper.AddWater(bottle, grade: 3, addAmount: 250000, initWaterFeature: false);
            Check(WaterHelper.GetTotalVolume(bottle) > 0, "water fixture filled");
            rows.Add(new InventoryEntry(Water, bottle.identifier, 1, Placement(InventoryRoot.Main, 7, 6),
                NativeInventoryFacts.Capture(bottle)));
            bottle.Destroy();
        }

        internal static void RegisterFixtures()
        {
            if (DirectoryMaster.Has<GameItem>(StackId)) return;
            var directory = UnityEngine.Object.FindFirstObjectByType<ModItemDirectory>(FindObjectsInactive.Include);
            Check(directory != null && directory.Add(StackId, () =>
            {
                var item = DirectoryMaster.Item("toilet_paper");
                item.mayStackItemFunc = (a, b) => a.identifier == b.identifier;
                item.maxSlotAmountItemFunc = (_, _, _) => 16;
                return item;
            }), "register ordinary stackable content through native mod item directory");
        }

        internal void Tick()
        {
            if (!pilot.Ready) return;
            if (done)
            {
                if (pilot.localHost && !holdChecked && pilot.session.Host.Sequence == 0)
                {
                    Check(pilot.held == null, "the previous connection's drag must be cancelled");
                    holdChecked = true;
                    Debug.Log("[probably_coop GRID] PASS host released the old drag across reconnect");
                }
                return;
            }
            if (phase == 0)
            {
                NativeQaPlatformWrites.Verify();
                juice = Find(Juice);
                paper = Find(Paper);
                bag = Find(Bag);
                stock = Find(Stock);
                machine = Find(Machine);
                water = Find(Water);
                strip = Find(Strip);
                bottleSlot = GraphUtils.FindChildrenType<GameInventory>(machine)
                    .Single(slot => slot is GameSlotInventory && slot.MayHaveValidInventorySlot(water));
                Check(juice is GameItemElement && paper is GameItemElement && bag is GameItemElement,
                    "factory objects must have native UI");
                foreach (InventoryAddress address in Inventories)
                {
                    pilot.world.Grid(address).onSlotAddItemLateFunc += (_, _, _) => adds++;
                    pilot.world.Grid(address).onSlotRemoveItemLateFunc += (_, _, _) => removes++;
                }
                phase = 1;
                if (rejoining)
                {
                    Check(!pilot.localHost && FinalPlacements() && FinalEffects(), "rejoin checkpoint content");
                    Finish("native grid rejoin restored authoritative placements");
                    return;
                }
                if (!pilot.localHost)
                {
                    Drop(paper, Address(InventoryRoot.BackCounter), 7, 1);
                    Drop(paper, Address(InventoryRoot.Showcase), 2, 1);
                    Drop(paper, BagContents, 1, 1);
                    Check(pilot.session.Guest.PendingCount == 3 && At(paper, BagContents, 1, 1),
                        "three original cross-inventory drops before acknowledgement");
                    Debug.Log("[probably_coop GRID] PASS guest consecutive native drops before acknowledgement");
                }
            }

            if (pilot.localHost)
            {
                if (phase == 1 && raceReady && At(paper, BagContents, 1, 1))
                {
                    Check(adds == 0 && removes == 0, "remote guest placement replayed original callbacks");
                    Drop(bag, Address(InventoryRoot.Showcase), 1, 1);
                    Drop(juice, Address(InventoryRoot.FrontCounter), 11, 1, orientation: 1);
                    Check(adds == 2 && removes == 2, "local host drops did not invoke original callbacks exactly once");
                    Check(pilot.room.SendLoose(Signal, new byte[] { 1 }), "race signal");
                    phase = 2;
                }
                else if (phase == 2 && pilot.session.Host.Sequence >= 6)
                {
                    Check(adds == 2 && removes == 2 && FinalPlacements(), "host must keep the winning placements");
                    Debug.Log("[probably_coop GRID] PASS host original callback and same-slot arbitration");
                    phase = 3;
                }
                else if (phase == 3 && pilot.session.Host.Sequence >= 9)
                {
                    Check(stock.unitCount == 8 && bottleSlot.childItems.SingleOrDefault() == water &&
                        machine.spritePath == "purifier_w", "host applied machine load and its native sprite change");
                    Debug.Log("[probably_coop GRID] PASS host applied split merge and machine load");
                    Check(pilot.room.SendLoose(Signal, new byte[] { 3 }), "machine load confirmation");
                    phase = 4;
                }
                else if (phase == 4 && pilot.session.Host.Sequence >= LastSequence)
                {
                    Check(FinalEffects(), "host applied machine unload and original strip effects");
                    Check(pilot.BeginDrag(paper), "host reservation before guest disconnect");
                    Debug.Log("[probably_coop GRID] PASS host applied machine unload and tool facts");
                    done = true;
                }
                return;
            }

            if (phase == 1 && pilot.session.Guest.Sequence == 3 && pilot.session.Guest.PendingCount == 0)
            {
                Check(pilot.room.SendLoose(Signal, new byte[] { 2 }), "guest ready to race");
                phase = 2;
            }
            if (!raceAttempted || pilot.session.Guest.PendingCount != 0 || pilot.session.Guest.Sequence < 6) return;
            if (phase == 2)
            {
                Check(FinalPlacements(), "guest same-slot rollback returned item to the moved container");
                Check(pilot.checkpointLoads == 1 && adds == 4 && removes == 4 &&
                    ReferenceEquals(juice, Find(Juice)) && ReferenceEquals(paper, Find(Paper)) && ReferenceEquals(bag, Find(Bag)),
                    "replication must preserve native object identity and avoid duplicate callbacks");
                RunEffects();
                phase = 3;
            }
            else if (phase == 4 && pilot.session.Guest.Sequence >= LastSequence)
            {
                Check(FinalEffects(), "guest effects settled on both ends");
                Debug.Log("[probably_coop GRID] PASS guest split merge machine load unload and tool use");
                Finish("guest original placements and local rollback");
            }
        }

        private void OnDatagram(byte channel, byte[] data)
        {
            if (channel != Signal || data.Length != 1 || rejoining || done || !pilot.Ready) return;
            if (pilot.localHost)
            {
                if (data[0] == 2) raceReady = true;
                return;
            }
            if (data[0] == 3 && phase == 3)
            {
                Drop(water, Address(InventoryRoot.Main), 7, 6);
                Check(strip.MayTarget(water) && strip.CanTarget(water), "original strip can target filled bottle");
                ItemBehaviourManager.current.Target(strip, water);
                if (strip.MayTarget(water) && strip.CanTarget(water)) strip.Target(water);
                Check(FinalEffects(), "original strip ran immediately on the initiating client " + DescribeEffects());
                phase = 4;
                return;
            }
            if (raceAttempted) return;
            Check(data.Length == 1 && data[0] == 1 && At(juice, Address(InventoryRoot.Main), 1, 1),
                "guest must act before receiving host commit");
            Drop(paper, Address(InventoryRoot.FrontCounter), 11, 1);
            raceAttempted = true;
        }

        private void RunEffects()
        {
            var main = pilot.world.Grid(Address(InventoryRoot.Main));
            var split = main.TryInventorySlot(stock, 3, new Vector2(float.PositiveInfinity, float.PositiveInfinity),
                new GridShapeBuilder(stock.modifiedShape).SetPosition(3, 6), stock.modifiedState);
            Check(split.IsValid() && split.TryAcceptOnce(3) == 3 && stock.unitCount == 5 && split.item != stock &&
                split.item.unitCount == 3 && split.item.uniqueId > 0, "original split created a stable new stock item");
            GameItem fragment = split.item;
            var merge = stock.GetStackSlot(fragment);
            Check(merge != null && merge.TryAcceptOnce(3) == 3 && stock.unitCount == 8 && fragment.parentInventory == null,
                "original merge before first acknowledgement");
            fragment.Destroy();
            Check(!(fragment as GameItemElement).IsDestroyed(), "depleted pending native instance kept for rollback");
            var load = bottleSlot.TryFindOneValidInventorySlot(water);
            Check(load != null && load.TryAcceptOnce() == 1 && water.parentInventory == bottleSlot &&
                machine.spritePath == "purifier_w", "original machine accepted bottle and applied load callback");
        }

        private void Drop(GameItem item, InventoryAddress address, int x, int y, int orientation = 0)
        {
            GridShape placement = new GridShapeBuilder(item.modifiedShape).SetTransform(x, y, flipped: false, orientation);
            var slot = pilot.world.Grid(address).TryInventorySlot(item, item.unitCount,
                new Vector2(float.PositiveInfinity, float.PositiveInfinity), placement, item.modifiedState);
            Check(slot.IsValid() && slot.TryAcceptOnce(item.unitCount) == item.unitCount &&
                At(item, address, x, y) && item.modifiedShape.orientation == orientation,
                "original grid placement in " + address.Root);
        }

        private void Finish(string evidence)
        {
            done = true;
            Debug.Log("[probably_coop GRID] PASS " + evidence);
            var escape = UnityEngine.Object.FindFirstObjectByType<EscapeUIManager>(FindObjectsInactive.Include);
            Check(escape != null, "original Escape menu is available");
            escape.OnMainMenu();
        }

        internal bool ShouldVerifyMenu => done && !pilot.localHost;

        private static InventoryAddress Address(InventoryRoot root) => new InventoryAddress(root);

        private static InventoryPlacement Placement(InventoryRoot root, ushort x, ushort y) =>
            new InventoryPlacement(Address(root), new GridPose(x, y, 0));

        private GameItem Find(int id) => Inventories.SelectMany(address => pilot.world.Grid(address).childItems)
            .Single(item => item.uniqueId == id);

        private bool At(GameItem item, InventoryAddress address, int x, int y) =>
            item.parentInventory == pilot.world.Grid(address) && item.modifiedShape.minX == x && item.modifiedShape.minY == y;

        private bool FinalPlacements() => At(juice, Address(InventoryRoot.FrontCounter), 11, 1) &&
            juice.modifiedShape.orientation == 1 &&
            At(bag, Address(InventoryRoot.Showcase), 1, 1) && At(paper, BagContents, 1, 1);

        private bool FinalEffects() => stock.unitCount == 8 && At(water, Address(InventoryRoot.Main), 7, 6) &&
            bottleSlot.childItems.Count == 0 && machine.spritePath == "purifier" &&
            strip.IsTag("IS_SPENT") && strip.spritePath.Length == 6 &&
            strip.spritePath.StartsWith("strip", StringComparison.Ordinal) && char.IsDigit(strip.spritePath[5]) &&
            strip.GetTagReadonly("TEST_WATER_QUALITY").GetInt() == WaterHelper.GetWaterPurity(water) &&
            strip.GetTagReadonly("ASSOCIATED_ITEM_ID").GetInt() == water.uniqueId && strip.unitValue == 0 &&
            WaterHelper.GetTotalVolume(water) > 0;

        private string DescribeEffects() => $"stock={stock.unitCount} water={water.modifiedShape.minX},{water.modifiedShape.minY} " +
            $"inSlot={bottleSlot.childItems.Count} machine={machine.spritePath} spent={strip.IsTag("IS_SPENT")} " +
            $"strip={strip.spritePath}/{strip.unitValue} volume={WaterHelper.GetTotalVolume(water)} " +
            $"pending={pilot.session.Guest.PendingCount}";

        private static void Check(bool passed, string condition)
        {
            if (!passed) throw new InvalidOperationException("Inventory QA: " + condition);
        }

        public void Dispose() => pilot.room.Datagram -= OnDatagram;
    }
}
#endif
