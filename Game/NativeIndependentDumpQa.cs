#if PROBABLY_COOP_GRID_QA
using System;
using System.Linq;
using ProbablyCoop.Core;
using UnityEngine;

namespace ProbablyCoop;

internal sealed partial class NativeBackgroundQa
{
    private readonly bool independentDumpQa = Array.IndexOf(Environment.GetCommandLineArgs(),
        "--probably-coop-qa-independent-dump") >= 0;
    private GameItem dumpStoryLocker, dumpGroundMarker;
    private GameGridInventory sharedDumpGround, dumpHomeGround;
    private GameObject dumpLocalPaper;
    private int dumpInitialAttempts, dumpHomeEarly, dumpHomeLate;

    private void TickIndependentDump(NativeShop shop, double now)
    {
        var world = host ? NativeWorld.Host : NativeWorld.Guest;
        if (world?.IndependentTravel != true) return;
        const string site = AfterhourLocation.DUMPING_GROUND;
        var store = PlayerStore.Instance;
        var map = MapUIManager.Instance;
        var entry = EmporiumEntry.Instance;
        string peerSite = world.QaOtherTravelLocation;
        if (host)
        {
            switch (independentStep)
            {
                case 0:
                    dumpHomeGround = entry.afterhourInventory;
                    OpenDumpLocalPaper();
                    ModHook.OnReturningStoreEarly += OnDumpReturnEarly;
                    ModHook.OnReturningStoreLate += OnDumpReturnLate;
                    store.isAfterhoursIntroduced = true;
                    store.storeState = PlayerStore.StoreState.CLOSED;
                    store.visitLeftTonight = 1;
                    // Both arrivals must reset their previous attempts independently.
                    store.scavengingAttempts = 2;
                    NativeActorSession.Current.Run.Partner.scavengingAttempts = 3;
                    store.storeClientData.louLockerDay = StoreStation.GetDayCounter();
                    Require(store.storeClientData.louCapturedDay < 0 && store.storeClientData.louLockerPaidDay < 0 &&
                        !store.storeClientData.isLouLockerShort, "unused native locker event required");
                    dumpStoryLocker = DirectoryMaster.Item("lou_locker");
                    GeneralHelper.SetItemOwned(dumpStoryLocker);
                    Require(dumpStoryLocker.IsTag("LOU_LOCKER_TAG") && TryPlaceInEmptyCell(dumpStoryLocker, entry.frontInvinvElement),
                        "could not put Lou's original locker on the home counter after closing");
                    independentStep = 1;
                    break;
                case 1:
                    if (peerSite != site) return;
                    Require(!map.IsOutside && store.storeClientData.isLouLockerShort && dumpStoryLocker.parentInventory == null &&
                        dumpLocalPaper.activeSelf && dumpHomeEarly == 0 && dumpHomeLate == 0,
                        "a remote departure did not deliver the original home-counter locker");
                    Record("PASS independent native locker handoff", "guest departure processed the shared original event from the shop counter; host stayed home");
                    sharedDumpGround = world.QaExpeditionGround(site);
                    dumpGroundMarker = DirectoryMaster.Item(QaFoodMarker);
                    GeneralHelper.SetItemOwned(dumpGroundMarker);
                    Require(TryPlaceInEmptyCell(dumpGroundMarker, sharedDumpGround), "could not seed the shared ground");
                    map.Travel(AfterhourLocation.Get(site));
                    Require(map.Current?.Id == site && ReferenceEquals(entry.afterhourInventory, sharedDumpGround) &&
                        store.scavengingAttempts == 0 && ScavHelper.GetScavTimeLeft() > 0 && !dumpLocalPaper.activeSelf,
                        "host did not enter the original shared dumpster or reset personal attempts");
                    independentStep = 2;
                    break;
                case 2:
                    var actors = NativeActorSession.Current;
                    if (actors.Run.Partner.scavengingAttempts == 0) return;
                    Require(actors.Run.Partner.scavengingAttempts == 1 && store.scavengingAttempts == 0,
                        "guest scavenging did not consume exactly one personal attempt");
                    OpenDumpLocalPaper();
                    map.Return();
                    Require(!map.IsOutside && !GenericConfirmationUIManager.Instance.panel.activeSelf &&
                        ReferenceEquals(dumpGroundMarker.parentInventory, sharedDumpGround) &&
                        ReferenceEquals(world.QaExpeditionGround(site), sharedDumpGround),
                        "nonfinal return asked to clean the shared dumpster or cleared the remaining person's items");
                    Require(dumpHomeEarly == 1 && dumpHomeLate == 1 && !dumpLocalPaper.activeSelf && store.visitLeftTonight == 0,
                        "local return did not restore native shop hooks once, close local papers or preserve the spent visit");
                    OpenDumpLocalPaper();
                    Require(shop?.CanReloadWorld == false, "the host could replace a scene with its partner still outside");
                    Record("PASS independent shared dumpster host", "guest spent one attempt; host left without prompting or clearing the same shared ground");
                    independentStep = 3;
                    break;
                case 3:
                    if (peerSite.Length != 0) return;
                    Require(world.QaExpeditionGround(site) == null && sharedDumpGround.childItems.Count == 0 &&
                        shop?.CanReloadWorld == true && store.visitLeftTonight == 0,
                        "the final confirmed return did not clear its site or reset world admission");
                    Require(dumpHomeEarly == 1 && dumpHomeLate == 1 && dumpLocalPaper.activeSelf,
                        "the final remote return ran host-local hooks or closed the host's paper");
                    ModHook.OnReturningStoreEarly -= OnDumpReturnEarly;
                    ModHook.OnReturningStoreLate -= OnDumpReturnLate;
                    Record("PASS independent native shop return", "host hooks ran once with the home grid; the guest's return preserved host papers");
                    independentFinishAt = now + 2;
                    independentStep = 4;
                    break;
                case 4:
                    if (now >= independentFinishAt) Finish("PASS independent dumpster host");
                    break;
            }
            return;
        }

        if (independentStep == 0 && store.storeState == PlayerStore.StoreState.CLOSED &&
            store.visitLeftTonight == 1 && !world.HasPendingWork)
        {
            OpenDumpLocalPaper();
            map.Travel(AfterhourLocation.Get(site));
            independentStep = 1;
        }
        if (independentStep == 1 && map.Current?.Id == site && peerSite == site && !world.HasPendingWork)
        {
            Require(entry.afterhourInventory.childItems.Any(item => item.identifier == QaFoodMarker),
                "guest did not see the item on the host's shared dumpster grid");
            dumpInitialAttempts = ScavHelper.GetScavTimeLeft();
            Require(store.scavengingAttempts == 0 && dumpInitialAttempts > 0 && !dumpLocalPaper.activeSelf,
                "guest arrival did not reset its own scavenging and close the previous scene's paper");
            ScavHelper.ScavengeDumpingGrounds();
            independentStep = 2;
        }
        if (independentStep == 2 && peerSite.Length == 0 && !world.HasPendingWork)
        {
            Require(map.Current?.Id == site && ScavHelper.GetScavTimeLeft() == dumpInitialAttempts - 1 &&
                entry.afterhourInventory.childItems.Any(item => item.identifier == QaFoodMarker),
                "the remaining guest lost an item, scene or attempt count on host return");
            OpenDumpLocalPaper();
            map.Return();
            var confirm = GenericConfirmationUIManager.Instance;
            Require(confirm.panel.activeSelf, "the final visitor did not get the original dumping ground warning");
            confirm.CancelButtonPressed();
            Require(map.IsOutside && entry.afterhourInventory.childItems.Any(item => item.identifier == QaFoodMarker),
                "cancelling the final return changed the ground");
            map.Return();
            Require(confirm.panel.activeSelf, "the final leave warning could not be reopened");
            confirm.ConfirmButtonPressed();
            independentStep = 3;
        }
        if (independentStep == 3 && !map.IsOutside && !world.HasPendingWork)
        {
            Require(store.visitLeftTonight == 0 && store.saveSlotId == -1 && !dumpLocalPaper.activeSelf,
                "confirmed guest return granted an extra visit, left its paper open or created a slot");
            CompleteIndependentGuest("PASS independent dumpster guest");
        }
    }

    private void OpenDumpLocalPaper()
    {
        if (dumpLocalPaper == null)
        {
            dumpLocalPaper = new GameObject("QA expedition paper");
            dumpLocalPaper.transform.SetParent(PaperUIManager.Instance.paperUIGroup.transform, false);
        }
        dumpLocalPaper.SetActive(true);
    }

    private void OnDumpReturnEarly()
    {
        Require(!MapUIManager.Instance.IsOutside && ReferenceEquals(EmporiumEntry.Instance.afterhourInventory, dumpHomeGround),
            "the native early return hook must see the host shop and its ground");
        dumpHomeEarly++;
    }

    private void OnDumpReturnLate()
    {
        Require(dumpHomeEarly == 1 && !MapUIManager.Instance.IsOutside &&
            ReferenceEquals(EmporiumEntry.Instance.afterhourInventory, dumpHomeGround),
            "the native late return hook must follow early return at the host shop");
        dumpHomeLate++;
    }
}
#endif
