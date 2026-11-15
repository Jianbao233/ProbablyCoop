#if PROBABLY_COOP_GRID_QA
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Sync;
using UnityEngine;

namespace ProbablyCoop;

internal sealed partial class NativeInventoryPilot
{
    private static readonly string[] QaArgs = Environment.GetCommandLineArgs();
    private static readonly int FullStockArg = Array.IndexOf(QaArgs, "--probably-coop-grid-full-stock");
    private static bool FullStockQa => FullStockArg >= 0;
    private static readonly bool StockActivationQa = QaArgs.Contains("--probably-coop-grid-activation-qa");
    private static readonly bool StockFaultQa = QaArgs.Contains("--probably-coop-grid-activation-fault-qa");
    private static InventoryEntry[] stockBeforeMenuExit;
    private double fullStockStarted;
    private int fullStockPhase;
    private GameItem activationPack, winningTicket, chemicalPack, consumable, bulkContainer, pinnedCargo, looseCargo;
    private int[] originalTickets;
    private HashSet<int> beforeChemicalIds;
    private int initialCash, scratchStarted, scratchFinished, scratchWinnings, initialLuck;

    private static void LoadFullStockQa(PlayerStore store)
    {
        CampaignCheck(NativeBackgroundQa.IsHostProduct && FullStockArg + 1 < QaArgs.Length &&
            Path.IsPathRooted(QaArgs[FullStockArg + 1]), "full stock fixture must be copied to the isolated run first");
        var fixture = JObject.Parse(File.ReadAllText(QaArgs[FullStockArg + 1]));
        var decode = System.Diagnostics.Stopwatch.StartNew();
        var shop = EmporiumEntry.Instance;
        var roots = new System.Collections.Generic.List<(string name, GameInventory grid)>
        {
            ("mainInvJSON", shop.invElement), ("backInvJSON", shop.backInvinvElement),
            ("showcaseInvJSON", shop.showcaseElement),
        };
        if (fixture["docInvJSON"]?.Type == JTokenType.String)
        {
            if ((bool?)fixture["dossierUpgraded"] == true)
            {
                NetworkUpgrade.MarkUnlockedSilently("DOSSIER_UPGRADE");
                shop.UpgradeDossier();
            }
            roots.Add(("docInvJSON", (GameInventory)shop.dossier.contentWindow.child));
        }
        foreach (var root in roots)
        {
            foreach (var item in root.grid.childItems.ToArray()) item.Destroy();
            string saved = (string)fixture[root.name];
            store.DecodeSaveItem(saved, root.grid);
            int expected = ((JArray)JObject.Parse(saved)["saveItems"]).Count - 1;
            int actual = GraphUtils.FindAllChildrenType<GameItem>(root.grid).Count;
            CampaignCheck(actual == expected, $"full stock root {root.name} loaded {actual}/{expected}");
            if (root.name == "docInvJSON") Debug.Log($"[probably_coop STOCK] original dossier loaded {actual} archive items");
        }
        if (fixture["savedItemFeatureList"] is JArray archivedFeatures)
        {
            var items = roots.SelectMany(root => GraphUtils.FindAllChildrenType<GameItem>(root.grid))
                .GroupBy(item => item.uniqueId).ToDictionary(group => group.Key, group => group.First());
            foreach (var item in items.Values) item.RemoveAllFeature();
            int restored = 0;
            foreach (JObject row in archivedFeatures)
            {
                if (!items.TryGetValue((int)row["parentItemUniqueId"], out var item)) continue;
                item.itemFeatures.Add(NativeMirrorProbe.ReadArchivedFeature(row));
                restored++;
            }
            Debug.Log($"[probably_coop STOCK] restored original saved item features={restored}/{archivedFeatures.Count}");
        }
        store.playerCash = (int)fixture["playerCash"];
        store.currentUniqueId = Math.Max(store.currentUniqueId, shop.GetAllItems().Max(item => item.uniqueId));
        if (StockActivationQa) SeedStockActivationQa(shop);
        if (ActivityQa) SeedActivityQa(shop, store);
        if (MachineMatrixQa) SeedMachineMatrixQa(shop);
        if (ShopUiQa) SeedShopUiQa(store);
        Debug.Log($"[probably_coop STOCK] original decoder loaded the archive inventory ms={decode.ElapsedMilliseconds} source={(string)fixture["sha256"]}");
    }

    private static void SeedStockActivationQa(EmporiumEntry shop)
    {
        var ticket = GraphUtils.FindAllChildrenType<GameItem>(shop.invElement).First(item => item.IsID("scratcher"));
        ticket.EnableTag("QA_STOCK_WINNER");
        LotteryHelper.SetSpawnLuck(ticket, int.MaxValue);
        for (int slot = 1; slot <= 3; slot++)
            ticket.ModifyTag(ScratcherHelper.SCRATCHER_SYMBOL + slot, tag => tag.Enable().SetInt(1));

        GameItem Place(string catalog, string tag, GraphNodeStorage destination)
        {
            var item = DirectoryMaster.Item(catalog);
            CampaignCheck(item != null, "stock action fixture factory " + catalog);
            item.EnableTag(tag);
            GeneralHelper.SetItemOwned(item, true);
            CampaignCheck(GraphUtils.TryAcceptAll(destination, item) == 0 && item.parentInventory != null,
                "stock action fixture fits in " + tag);
            return item;
        }
        Place("liquid_luck", "QA_STOCK_CONSUMABLE", shop.backInvinvElement);
        var bag = Place("backpack_small", "QA_STOCK_BULK", shop.backInvinvElement);
        Place("toilet_paper", "QA_STOCK_PINNED", bag);
        Place("processed_juice", "QA_STOCK_LOOSE", bag);
    }

    private void TickFullStockQa()
    {
        if (MachineMatrixQa)
        {
            TickMachineMatrixQa();
            return;
        }
        if (ActivityQa && (ResumingCampaign || rejoinCampaign))
        {
            VerifyCampaignRestore(2);
            return;
        }
        double now = Time.realtimeSinceStartupAsDouble;
        if (fullStockStarted == 0)
        {
            fullStockStarted = now;
            int expected = StockActivationQa ? 586 : 582 + (ActivityQa ? 12 : 0);
            CampaignCheck(InventoryCheckpoint.TryDecode(world.Capture(), out var rows), "full stock checkpoint decodes");
            if (StockActivationQa && QaRejoinedFromMenu)
                CampaignCheck(stockBeforeMenuExit != null && stockBeforeMenuExit.Length == rows.Length &&
                    stockBeforeMenuExit.Zip(rows, (before, after) => before.Id == after.Id && before.Units == after.Units &&
                        before.CatalogId == after.CatalogId && before.Placement.Equals(after.Placement)).All(match => match),
                    "the rejoining guest keeps every settled stock identity, amount and position");
            var byId = rows.ToDictionary(row => row.Id);
            var rootCounts = rows.GroupBy(row => row.Placement.Address.TryResolveRoot(row.Id, byId, out var root)
                ? root.ToString() : "unbound").ToDictionary(group => group.Key, group => group.Count());
            int fixtureItems = rootCounts.Where(row => row.Key is nameof(InventoryRoot.Cassette) or nameof(InventoryRoot.Dossier))
                .Sum(row => row.Value);
            CampaignCheck(StockActivationQa && QaRejoinedFromMenu || rows.Length - fixtureItems == expected,
                $"full stock expected {expected} regular rows, got {rows.Length - fixtureItems} ({string.Join(", ", rootCounts.Select(row => $"{row.Key}={row.Value}"))})");
            if (!localHost)
                session.Guest.Bounced += (tx, result) => Debug.Log(
                    $"[probably_coop STOCK] rejected inventory serial={tx.Serial} cause={result}");
            Debug.Log($"[probably_coop STOCK] PASS role={RoleName()} imported items={rows.Length}");
        }
        if (StockActivationQa)
        {
            VerifyStockActivation();
            return;
        }
        if (localHost && fullStockPhase == 0 && now >= fullStockStarted + (ActivityQa ? 3 : 12))
        {
            StoreShutterButton.Instance.OpenShutter();
            SeedBusinessQa(StoreStation.GetDayCounter());
            fullStockPhase = 1;
        }
        if (!localHost && fullStockPhase == 0 && PlayerStore.Instance.storeState == PlayerStore.StoreState.OPEN &&
            RunGuestBusinessQa(StoreStation.GetDayCounter()) && RunGuestHeaterQa())
            fullStockPhase = 1;
        bool hostHeaterReady = !localHost || fullStockPhase == 1 && CheckHostHeaterQa();
        if (now < fullStockStarted + (ActivityQa ? 12 : 38) || fullStockPhase != 1 || !hostHeaterReady ||
            localHost && (businessPurchases != 1 || businessSales != 1)) return;
        if (ActivityQa && !(localHost ? RunHostActivityQa(now) : RunGuestActivityQa(now))) return;
        campaignDone = true;
        if (ActivityQa)
            Debug.Log($"[probably_coop ACTIVITIES] PASS role={RoleName()} full stock settlement {CampaignFacts(StoreStation.GetDayCounter())}");
        Debug.Log($"[probably_coop STOCK] PASS role={RoleName()} full stock frame sample and original trading");
        if (ActivityQa && !localHost) RequestExit();
    }

    private void VerifyStockActivation()
    {
        var escape = StoreUIManager.Instance.escapeUIManager;
        if (!localHost && QaRejoinedFromMenu)
        {
            Debug.Log("[probably_coop STOCK] PASS same guest process rejoined with unpacked stock");
            campaignDone = true;
            escape.OpenEscapeMenu();
            escape.OnMainMenu();
            return;
        }

        if (activationPack == null)
        {
            activationPack = FullStockItems().Where(item => item.IsID("scratcher_stack")).OrderBy(item => item.uniqueId).First();
            originalTickets = FullStockItems().Where(item => item.IsID("scratcher")).Select(item => item.uniqueId).ToArray();
            winningTicket = FindStockQa("QA_STOCK_WINNER");
            chemicalPack = FullStockItems().Single(item => item.IsID("common_chemical"));
            consumable = FindStockQa("QA_STOCK_CONSUMABLE");
            bulkContainer = FindStockQa("QA_STOCK_BULK");
            pinnedCargo = FindStockQa("QA_STOCK_PINNED");
            looseCargo = FindStockQa("QA_STOCK_LOOSE");
            var store = PlayerStore.Instance;
            initialCash = store.playerCash;
            scratchStarted = store.scratchersStartedTotal;
            scratchFinished = store.scratchersFinishedTotal;
            scratchWinnings = store.scratcherWinningsTotal;
            initialLuck = store.healthData.liquidLuckDaysLeft;
            if (!localHost)
            {
                CampaignCheck(activationPack.CanActivateSlotItem(), "original stock pack can be opened");
                CampaignCheck(!activationPack.parentInventory.Expel(activationPack), "unscoped guest removal is refused");
                activationPack.Destroy();
                ClickNativeStockQa(activationPack);
                ClickNativeStockQa(activationPack);
                CampaignCheck(activationPack.parentInventory != null && activationPack is GameItemElement element &&
                    !element.IsDestroyed(), "guest preserves its original pack until acceptance");
            }
            return;
        }

        if (!VerifyStockEffects()) return;
        campaignDone = true;
        if (localHost) return;
        CampaignCheck(InventoryCheckpoint.TryDecode(world.Capture(), out stockBeforeMenuExit),
            "capture the settled stock before leaving the native shop");
        QaJoinAgain = room.Code;
        if (StockFaultQa) throw new InvalidOperationException("QA-injected room error after stock activation");
        escape.OpenEscapeMenu();
        escape.OnMainMenu();
    }

    private bool VerifyStockEffects()
    {
        var store = PlayerStore.Instance;
        var scratcher = ScratcherUIManager.Instance;
        var confirm = GenericConfirmationUIManager.Instance;
        switch (fullStockPhase)
        {
            case 0:
                if (activationPack.parentInventory != null) return false;
                var generated = FullStockItems().Where(item => item.IsID("scratcher") && !originalTickets.Contains(item.uniqueId))
                    .OrderBy(item => item.uniqueId).ToArray();
                CampaignCheck(generated.Length == 5 && generated.All(GeneralHelper.IsItemOwned),
                    "two activations of the same pack produce exactly five owned tickets");
                string facts = string.Join("/", generated.Select(item => item.uniqueId + ":" + string.Join("", Enumerable.Range(1, 3)
                    .Select(slot => item.GetTagReadonly(ScratcherHelper.SCRATCHER_SYMBOL + slot).GetInt()))));
                Debug.Log($"[probably_coop STOCK] PASS role={RoleName()} unpacked five native tickets facts={facts}");
                if (!localHost)
                {
                    ClickNativeStockQa(winningTicket);
                    CampaignCheck(scratcher.ui.activeSelf && ReferenceEquals(scratcher.currentActiveScratcherItem, winningTicket),
                        "native ticket window opens locally");
                    scratcher.scratchInputs[0].OnAnyScratch();
                    for (int slot = 1; slot <= 3; slot++) scratcher.OnScratchComplete(slot);
                    scratcher.OnScratchComplete(3);
                }
                break;
            case 1:
                if (!ScratcherHelper.IsFullyScratched(winningTicket)) return false;
                CampaignCheck(store.scratchersStartedTotal == scratchStarted + 1 && store.scratchersFinishedTotal == scratchFinished + 1 &&
                    store.scratcherWinningsTotal == scratchWinnings + 350 && winningTicket.unitValue == 350 &&
                    !winningTicket.IsGameItemType("LOTTERY") && scratcher.ui.activeSelf != localHost,
                    "scratch symbols, counters and item type converge with the window only on the guest");
                if (!localHost)
                {
                    ClickNativeStockQa(winningTicket);
                    ClickNativeStockQa(winningTicket);
                    CampaignCheck(store.playerCash == initialCash, "credit waits for authoritative redemption");
                }
                break;
            case 2:
                if (winningTicket.parentInventory != null) return false;
                CampaignCheck(store.playerCash == initialCash + 350 && !scratcher.ui.activeSelf, "redeem once and close only the retired view");
                beforeChemicalIds = FullStockItems().Select(item => item.uniqueId).ToHashSet();
                if (!localHost)
                {
                    ClickNativeStockQa(chemicalPack);
                    CampaignCheck(confirm.panel.activeSelf && chemicalPack.parentInventory != null, "original warning opens before unpacking");
                    confirm.CancelButtonPressed();
                    CampaignCheck(!confirm.panel.activeSelf && chemicalPack.parentInventory != null, "cancel keeps the original package");
                    ClickNativeStockQa(chemicalPack);
                    confirm.ConfirmButtonPressed();
                }
                break;
            case 3:
                if (chemicalPack.parentInventory != null) return false;
                var stock = FullStockItems().ToDictionary(item => item.uniqueId);
                var supplies = stock.Values.Where(item => !beforeChemicalIds.Contains(item.uniqueId)).ToArray();
                CampaignCheck(!confirm.panel.activeSelf &&
                    beforeChemicalIds.Except(stock.Keys).SequenceEqual(new[] { chemicalPack.uniqueId }) &&
                    supplies.Select(item => item.identifier).OrderBy(id => id, StringComparer.Ordinal).SequenceEqual(
                        new[] { "med_bottle_blue", "med_bottle_red", "med_bottle_small_pink", "mini_bottle", "mini_bottle" }) &&
                    supplies.All(item => GeneralHelper.IsItemOwned(item) && WaterHelper.GetTotalVolume(item) > 0),
                    "native chemical confirmation only replaces the package with the five filled, owned supplies");
                if (!localHost) ClickNativeStockQa(consumable);
                break;
            case 4:
                if (consumable.parentInventory != null) return false;
                CampaignCheck(store.healthData.liquidLuckDaysLeft == initialLuck + (localHost ? 0 : 3),
                    "shared consumption affects the acting partner's health only");
                if (!localHost) ItemPinHelper.SetPinned(pinnedCargo, true);
                break;
            case 5:
                if (!ItemPinHelper.IsPinned(pinnedCargo)) return false;
                if (!localHost) bulkContainer.TryUnloadAll(skipPinned: true);
                break;
            case 6:
                if (!localHost && session.Guest.PendingCount != 0) return false;
                if (looseCargo.parentInventory != bulkContainer.parentInventory) return false;
                CampaignCheck(pinnedCargo.parentInventory?.GetParentItem() == bulkContainer && ItemPinHelper.IsPinned(pinnedCargo),
                    "native unload leaves pinned cargo in its own container");
                Debug.Log($"[probably_coop STOCK] PASS role={RoleName()} scratch and redeem, confirmed unpack, personal consumption, pin and batch unload");
                return true;
        }
        fullStockPhase++;
        return false;
    }

    private static void ClickNativeStockQa(GameItem item) => AccessTools.Method(typeof(ItemMouseDoubleClickHandler), "ClickAction")
        .Invoke(ItemMouseDoubleClickHandler.current, new object[] { item, Vector2.zero, false, true });

    private GameItem FindStockQa(string tag) => FullStockItems().Single(item => item.IsTag(tag));

    private System.Collections.Generic.IEnumerable<GameItem> FullStockItems() =>
        new[] { InventoryRoot.Main, InventoryRoot.BackCounter, InventoryRoot.Showcase, InventoryRoot.FrontCounter }
            .SelectMany(root => GraphUtils.FindAllChildrenType<GameItem>(world.Grid(new InventoryAddress(root))));
}
#endif
