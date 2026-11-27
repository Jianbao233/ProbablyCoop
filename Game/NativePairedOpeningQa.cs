#if PROBABLY_COOP_GRID_QA
using System;
using System.Collections.Generic;
using System.Linq;
using DG.Tweening;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Core;
using UnityEngine;

namespace ProbablyCoop;

internal sealed partial class NativeBackgroundQa
{
    private readonly bool pairedOpeningQa = Array.IndexOf(Environment.GetCommandLineArgs(), "--probably-coop-qa-paired-opening") >= 0;
    private readonly bool modOpeningQa = Array.IndexOf(Environment.GetCommandLineArgs(), "--probably-coop-qa-mod-opening") >= 0;
    private readonly bool mentoredOpeningQa = Array.IndexOf(Environment.GetCommandLineArgs(), "--probably-coop-qa-mentored-opening") >= 0;
    private const string ModCareerId = "cooperative_brewer_fixture";
    private const string ModCareerItem = "Co-op brewer fixture";
    private const string ModPersonalPerk = "coop_probe_personal_perk";
    private const string ModSharedPerk = "coop_probe_shared_perk";
    private readonly bool survivorShotQa = Array.IndexOf(Environment.GetCommandLineArgs(), "--probably-coop-qa-survivor-shot") >= 0;
    private readonly bool fatalAugShotQa = Array.IndexOf(Environment.GetCommandLineArgs(), "--probably-coop-qa-fatal-aug-shot") >= 0;
    private static readonly bool WithholdOfficerDepartureCue =
        Array.IndexOf(Environment.GetCommandLineArgs(), "--probably-coop-qa-miss-officer-departure") >= 0;
    private static bool officerDepartureCueWithheld;
    private static readonly bool SharedFieldAudioQa = Array.IndexOf(Environment.GetCommandLineArgs(), "--probably-coop-qa-field-audio") >= 0;
    private static int guestDoorSounds;
    private readonly bool openingRentQa = Array.IndexOf(Environment.GetCommandLineArgs(), "--probably-coop-qa-opening-rent") >= 0;
    private readonly bool openingSentinelQa = Array.IndexOf(Environment.GetCommandLineArgs(), "--probably-coop-qa-opening-sentinel") >= 0;
    private readonly bool openingCommissaryQa = Array.IndexOf(Environment.GetCommandLineArgs(), "--probably-coop-qa-opening-commissary-ban") >= 0;
    private readonly bool labelLocaleQa = Array.IndexOf(Environment.GetCommandLineArgs(), "--probably-coop-qa-label-locale") >= 0;
    private readonly bool reverseOpeningQa = Array.IndexOf(Environment.GetCommandLineArgs(), "--probably-coop-qa-reverse-opening") >= 0;
    private readonly bool fenceOpeningQa = Array.IndexOf(Environment.GetCommandLineArgs(), "--probably-coop-qa-fence-opening") >= 0;
    private NewGameData.StartType PairedHostCareer => mentoredOpeningQa
        ? reverseOpeningQa ? NewGameData.StartType.WATER_MERCHANT : NewGameData.StartType.APPRENTICE
        : openingRentQa ? NewGameData.StartType.GUNSMITH :
        fenceOpeningQa ? NewGameData.StartType.FENCE : reverseOpeningQa ? NewGameData.StartType.DRUG_DEALER : NewGameData.StartType.WATER_MERCHANT;
    private NewGameData.StartType PairedPartnerCareer => mentoredOpeningQa
        ? reverseOpeningQa ? NewGameData.StartType.APPRENTICE : NewGameData.StartType.WATER_MERCHANT
        : openingRentQa ? NewGameData.StartType.AQUAFARMER :
        fenceOpeningQa ? NewGameData.StartType.GUNSMITH : reverseOpeningQa ? NewGameData.StartType.WATER_MERCHANT : NewGameData.StartType.DRUG_DEALER;
    private string PairedHostCareerId => modOpeningQa
        ? reverseOpeningQa ? ModCareerId : "water_merchant" : StartingScenario.GetByStartType(PairedHostCareer).id;
    private string PairedPartnerCareerId => modOpeningQa
        ? reverseOpeningQa ? "water_merchant" : ModCareerId : StartingScenario.GetByStartType(PairedPartnerCareer).id;
    private int pairedStep, pairedTravelStep, securityFrames;
    private Guid firstOpeningDraft;
    private string openingJoinCopy;
    private double openingRejoinAt;
    private int rentStep;
    private bool labelLocaleChecked, labelLocaleOpened;
    private GameItem labelLocaleWater, labelLocaleTool;
    private string labelLocaleSelected;
    private double securityAt;
    private Vector3 officerLastPosition;
    private bool officerSeen;
    private bool reportCustomerQueued;
    private bool pairedScavengeSent;
    private int pairedScavengeBefore;
    private bool survivorWoundSeen;
    private ulong survivorEpisode;
    private int policeApproachFrames, policeDepartureFrames, cashBeforeShot;
    private bool bodySeenAfterShot, policeArrivedDuringShot, policeMovedOffCamera;
    private bool policeDialogueSeen, policeDialogueEarly;
    private double nextShotDialogueAdvance;

    private void TickPairedOpening(RoomSelector room, CoopPanel panel, NativeShop shop, bool inMenu, double now)
    {
        Require(!Application.isBatchMode && now - started < (independentReconnectQa ? 180 : 130),
            "paired opening or security movement timed out");
        if (verifyOpeningRestartQa)
        {
            TickRestoredOpening(room, panel, shop, inMenu);
            return;
        }
        var lobby = panel.QaOpeningLobby;
        if (host && pairedStep == 0 && inMenu && room.IsHost)
        {
            Require(CoopRun.ListCheckpoints().Count == 0, "paired opening requires an empty isolated save profile");
            ModHelper.WriteModDataFileText("probably_coop", "qa-room.txt", room.Code);
            pairedStep = 1;
        }
        if (NativeJoinWizardQa.Enabled && (NativeJoinWizardQa.Blocked || NativeJoinWizardQa.LateFailure))
        {
            if (!host && pairedStep == 3 && inMenu && !room.IsGuest &&
                (NativeJoinWizardQa.Blocked || room.Status == "sync_failed"))
            {
                if (NativeJoinWizardQa.LateFailure) panel.VerifyJoinFailureQa();
                Require(!NativeGuest.Active && !PlayerStore.HasInstance &&
                    UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == "EmporiumMenu",
                    $"failed join cleanup incomplete: guest={NativeGuest.Active} store={PlayerStore.HasInstance}");
                Finish("PASS guest join review recovery");
                return;
            }
            if (host && (pairedStep is 3 or 4) && shop != null && room.IsHost && room.Session?.State != LinkState.Connected)
            {
                Require(CoopRun.VerifyCheckpoint(PlayerStore.Instance, 1), "rejected guest changed the host checkpoint");
                Finish("PASS host join review retained room");
                return;
            }
        }
        if (!host && NativeJoinWizardQa.OpeningHandoff && inMenu && pairedStep == 5)
        {
            if (now < openingRejoinAt) return;
            panel.QaRejoinOpening(openingJoinCopy);
            pairedStep = 6;
            return;
        }
        if (room.Session?.State != LinkState.Connected)
        {
            ObserveIndependentMerchantDisconnect(room, panel, shop);
            return;
        }
        if (!host && independentMerchantResumeQa && NativeWorld.Guest?.Ready == true) pairedStep = 3;
        if (host && pairedStep == 1 && inMenu)
        {
            panel.QaStartPairedOpening();
            var menu = MainMenuUIController.Instance;
            Require(NewGameSpecializationUI.IsOpen, "co-op start did not open the original career window");
            var career = StartingScenario.Get(PairedHostCareerId);
            Require(career != null, "missing native career: " + PairedHostCareerId);
            menu.OnScenarioContinue(career);
            if (menu.confirmPanelUIManager.panel.activeSelf && menu.confirmPanelUIManager.yesAction != null)
                menu.confirmPanelUIManager.OnYesClick();
            Require(NewGamePerksUI.IsOpen, "co-op start did not open the original host perk window");
            ChooseNativePerk("lucky");
            if (openingCommissaryQa) ChooseNativePerk("banned_commissary");
            AccessTools.Method(typeof(NewGamePerksUI), "OnContinueClicked").Invoke(null, null);
            Require(NewGameDifficultyUI.IsOpen, "co-op start did not open the original difficulty window");
            if (mentoredOpeningQa && NewGameData.Instance.skipIntro)
                AccessTools.Method(typeof(NewGameDifficultyUI), "OnSkipIntroClicked").Invoke(null, null);
            AccessTools.Method(typeof(NewGameDifficultyUI), "OnStartClicked").Invoke(null, null);
            pairedStep = 2;
        }
        if (host && pairedStep == 2 && lobby.QaValidated)
        {
            lobby.QaLaunch();
            pairedStep = 3;
        }
        if (!host && lobby.HasGuestDraft && NativeJoinWizardQa.OpeningHandoff && pairedStep == 0)
        {
            firstOpeningDraft = lobby.QaDraftId;
            openingJoinCopy = panel.QaOpenNativeModificationsFromOpening();
            openingRejoinAt = now + 1;
            pairedStep = 5;
            return;
        }
        if (!host && lobby.HasGuestDraft && (pairedStep == 0 || pairedStep == 6))
        {
            if (pairedStep == 6)
            {
                Require(lobby.QaDraftId != firstOpeningDraft && room.Code == openingJoinCopy &&
                    NewGamePerksUI.IsOpen == false && !PlayerStore.HasInstance,
                    "the resumed opening reused the old draft or started play before the new choice");
                Record("PASS opening Mod handoff rejoined the original room",
                    "native Modifications opened, copied room was retained, host opening issued a fresh guest draft");
                NativeOffscreenCapture.Capture("qa-opening-mod-rejoined");
            }
            lobby.QaChoosePerks(PairedPartnerCareerId);
            Require(NewGamePerksUI.IsOpen, "paired guest perk view did not open");
            var visible = (List<StartingPerk>)AccessTools.Field(typeof(NewGamePerksUI), "sorted").GetValue(null);
            ChooseNativePerk("working_pipes");
            if (independentShortSleeperQa) ChooseNativePerk("short_sleeper");
            if (openingSentinelQa) ChooseNativePerk("sentinel");
            if (modOpeningQa)
            {
                ChooseNativePerk(ModPersonalPerk);
                ChooseNativePerk(ModSharedPerk);
            }
            if (labelLocaleQa) ChooseNativePerk("maimed");
            AccessTools.Method(typeof(NewGamePerksUI), "OnContinueClicked").Invoke(null, null);
            Record("PASS guest-full-opening-picker", $"native menu contained {visible.Count} perks and submitted starting plumbing");
            pairedStep = 3;
        }
        if (pairedStep < 3 || (host ? shop == null || NativeWorld.Host?.PartnerSetupRequiredForQa != false : NativeWorld.Guest?.Ready != true))
            return;
        if (openingRestartQa)
        {
            CaptureOpeningBeforeRestart();
            return;
        }
        if (independentTravelQa)
        {
            if (independentMerchantQa) TickIndependentMerchant(room.Session, shop, now);
            else if (independentDumpQa) TickIndependentDump(shop, now);
            else TickIndependentJourney(shop, now);
            return;
        }
        if (modOpeningQa)
        {
            CheckRegisteredOpening();
            return;
        }
        if (openingRentQa)
        {
            TickOpeningRentQa(now);
            return;
        }
        if (mentoredOpeningQa)
        {
            TickMentoredOpening(now);
            return;
        }
        if (pairedStep == 3)
        {
            var store = PlayerStore.Instance;
            var itemIds = EmporiumEntry.Instance.invElement.childItems.Select(item => item.identifier).ToHashSet();
            Require(store.startType == PairedHostCareer && NativeOpening.UsesCareer(store, PairedPartnerCareer) &&
                EmporiumEntry.IsWaterTapUnlocked(), "the selected careers or the partner's shared opening perk were missing");
            if (fenceOpeningQa)
            {
                Require(store.networkUpgrade["RETIRED_GUNSMITH"].state == 2 && store.networkUpgrade["GUN_PERMIT_I"].state == 2 &&
                    itemIds.Contains("gun_part"), "the partner gunsmith did not receive the original opening goods and technology");
                if (host) CheckFenceGunsmithSchedule();
            }
            else
            {
                Require(itemIds.Contains("water_jug") && itemIds.Contains("chemistry_guide") &&
                    store.networkUpgrade["RETIRED_CHEMIST"].state == 2 && store.networkUpgrade["WATER_TRADER"].state == 2,
                    "the water merchant and chemist did not receive both original packages and technologies");
                var cartel = store.storeReputations.Single(rep => rep.factionId == StoreReputation.FACTION_CARTEL);
                Require(cartel.perks.Any(perk => perk.perkId == "CARTEL_AUTHORIZED_EXIT"),
                    "the chemist career started without the native cartel exit path");
                if (host) CheckPairedCareerSchedule();
                Record("PASS paired-career-reputation", "both processes have one cartel faction and its original exit perk");
            }
            Require(host ? CoopRun.VerifyCheckpoint(store, 1) : store.saveSlotId == -1,
                "two-player start did not isolate saves and create a host checkpoint");
            Require(StartingPerk.IsPerkActive("lucky") == host,
                "the host's personal starting perk leaked across roles or was not applied");
            if (openingSentinelQa || openingCommissaryQa) CheckSharedPerkStory();
            if (labelLocaleQa)
            {
                Require(AfterhourLocation.Get(AfterhourLocation.DUMPING_GROUND).IsDiscovered() == host,
                    "map discovery used a different actor's travel restriction");
                Record("PASS personal-map-discovery", "native dumping ground discovery followed the local actor's starting perk");
            }
            Record("PASS paired-opening", $"host={PairedHostCareer} partner={PairedPartnerCareer}, plumbing, day one, shared cash {store.playerCash}");
            NativeOffscreenCapture.Capture("qa-paired-opening");
            pairedStep = 4;
        }
        if (labelLocaleQa && !TickLabelLocaleQa()) return;
        if (!TickPairedTravel()) return;
        if (survivorShotQa) { TickButcherSurvivor(); return; }
        if (fatalAugShotQa) { TickFatalAugShot(now); return; }
        if (securityAt == 0) securityAt = now + 4;
        var ui = StoreUIManager.Instance;
        if (host)
        {
            if (pairedStep == 4 && now >= securityAt)
            {
                var store = PlayerStore.Instance;
                if (store.storeState == PlayerStore.StoreState.MORNING)
                {
                    if (store.CanStartDay()) StoreShutterButton.Instance.OnClick();
                    return;
                }
                if (!reportCustomerQueued)
                {
                    var client = StoreClientListWanted.CreateWanted2Return();
                    client.mainDialogue = null;
                    store.storeClientManager.AddNextClient(client);
                    store.TryCallNextClient();
                    reportCustomerQueued = true;
                    return;
                }
                var target = store.currentClientInstance?.GetClientBlueprint();
                if (target?.identifier != "wanted2_return" || !store.isClientArrived || ui.isAboutToStartMainDialog) return;
                StoreSecButton.Instance.OnClick();
                Require(target.reported, "the original security button did not report the selected customer");
                pairedStep = 5;
                securityAt = now + 8;
            }
            if (pairedStep == 5 && now >= securityAt)
            {
                ui.ClearArrestState();
                Record("PASS security-dispatch", "original pair dispatched and cleared");
                Record("PASS host paired opening and security", "allowing the partner to finish observing before exit");
                pairedStep = 6;
                securityAt = now + 8;
            }
            if (pairedStep == 6 && now >= securityAt) Finish("PASS host paired opening and security");
            return;
        }
        var officer = ui.secGameObject;
        if (officer.gameObject.activeSelf)
        {
            var position = officer.transform.localPosition;
            var path = officer.GetComponent<DOTweenPath>().tween;
            if (officerSeen && path != null && path.IsActive() && path.IsPlaying() &&
                Vector3.Distance(officerLastPosition, position) > 0.001f)
            {
                securityFrames++;
                if (securityFrames == 12) NativeOffscreenCapture.Capture("qa-security-local-path");
            }
            officerSeen = true;
            officerLastPosition = position;
        }
        else if (officerSeen)
        {
            Require(securityFrames >= 12, $"guest saw only {securityFrames} changing officer frames");
            if (ui.secGameObject2.gameObject.activeSelf) return;
            Require(NativeWorld.Guest.QaSecurityAlarms == 1, "the original report alarm was missing or replayed");
            Record("PASS security alarm audio", "the guest played the report alarm once through the original shop source");
            Record("PASS local-security-motion", $"{securityFrames} moving frames on local native path; both officers hidden");
            Finish("PASS guest paired opening and security");
        }
    }

    private void TickMentoredOpening(double now)
    {
        var store = PlayerStore.Instance;
        var entry = EmporiumEntry.Instance;
        Require(!store.skipIntro && store.startType == PairedHostCareer && NativeOpening.UsesCareer(store, PairedPartnerCareer) &&
            store.networkUpgrade["WATER_TRADER"].state == 2 && store.playerCash == 350,
            "apprentice and water merchant did not keep the normal tutorial, both starting roles, cash and water tech");
        string[] signs = { "sign_water", "sign_food", "sign_household" };
        int Count(string id) => entry.GetAllOwnedItems().Where(item => item.identifier == id).Sum(item => item.unitCount);
        if (host && pairedStep == 3)
        {
            Require(!store.isDisplayCaseIntroduced && signs.All(id => Count(id) == 0) && CoopRun.VerifyCheckpoint(store, 1),
                "the co-op tutorial must start from a clean verified opening without early sign grants");
            var lesson = StoreClientListStory.CreateMentorDisplayCase().mainDialogue.nextDialogue.nextDialogue;
            lesson.ExecuteEndAction();
            lesson.ExecuteEndAction();
            pairedStep = 4;
            securityAt = now + 3;
        }
        if (!store.isDisplayCaseIntroduced || !host && NativeWorld.Guest.HasPendingWork) return;
        Require(signs.All(id => Count(id) == 1) && (host || store.saveSlotId == -1),
            "the native showcase lesson did not give exactly one of each career's signs");
        if (host && now < securityAt) return;
        NativeOffscreenCapture.Capture("qa-paired-opening-showcase-lesson");
        Finish(host ? "PASS mentored paired opening host" : "PASS mentored paired opening guest");
    }

    private void TickButcherSurvivor()
    {
        var store = PlayerStore.Instance;
        var world = host ? NativeWorld.Host : NativeWorld.Guest;
        var ui = StoreUIManager.Instance;
        var client = store.currentClientInstance?.GetClientBlueprint();
        if (host && pairedStep == 4)
        {
            if (store.storeState == PlayerStore.StoreState.MORNING)
            {
                if (store.CanStartDay()) StoreShutterButton.Instance.OnClick();
                return;
            }
            Require(store.storeState == PlayerStore.StoreState.OPEN && !store.IsClientPresent(),
                "expected an open, empty store for the original Butcher visit");
            Shotgun.Unlock();
            store.storeClientManager.AddNextClient(StoreClientListWanted.CreateWanted2Return());
            store.TryCallNextClient();
            pairedStep = 5;
            return;
        }
        if (!host && pairedStep == 4)
        {
            if (client?.identifier != "wanted2_return" || !store.isClientArrived || ui.isAboutToStartMainDialog) return;
            Require(client.survivesShot, "the replica lost the native Butcher survivor flag");
            if (world.HasDialogue) { DialogUIManager.Instance.OnAdvancePressed(); return; }
            ui.shotgun.GetComponent<Shotgun>().OnClick();
            pairedStep = 5;
        }
        Require(!ui.body.activeSelf && !ui.secGameObject.gameObject.activeSelf && !store.isHandlingShootout,
            "a survivor shot incorrectly entered the lethal aftermath");
        if (!host && pairedStep == 5 && world.ShootingState == NativeWorld.ShootingPhase.Aiming &&
            ui.isShootingMode && ui.shootingUI.activeSelf)
        {
            ui.Shoot();
            survivorEpisode = world.ShootingEpisode;
            pairedStep = 6;
        }
        if (ui.shootingWoundRenderer.gameObject.activeSelf)
        {
            if (!survivorWoundSeen) NativeOffscreenCapture.Capture("qa-butcher-survivor");
            survivorWoundSeen = true;
        }
        if (!host && pairedStep == 6 && world.ShootingEpisode == survivorEpisode && world.HasDialogue)
            DialogUIManager.Instance.OnAdvancePressed();
        if (world.ShootingState != NativeWorld.ShootingPhase.Complete || !survivorWoundSeen ||
            ui.isShootingMode || client?.identifier == "wanted2_return" || ui.shootingWoundRenderer.gameObject.activeSelf) return;
        Require(ui.mainCamera.activeSelf && !ui.isShot && ui.shotClientInstance == null,
            "the original survivor scene left the camera or shooting state locked");
        if (host)
            Require(NativeShootingQaEvidence.SourceShots == 1 && NativeShootingQaEvidence.SourceShootouts == 0,
                "the original Butcher action did not run once");
        else
            Require(NativeShootingQaEvidence.LocalFlash == 1 && NativeShootingQaEvidence.LocalGunSounds == 1 &&
                NativeShootingQaEvidence.LocalBodySounds == 0, "the guest missed or replayed the survivor shot sound");
        if (pairedStep == 7) return;
        pairedStep = 7;
        NativeOffscreenCapture.Capture("qa-butcher-survivor-cleared");
        Record("PASS original Butcher survivor", "native wound, shot sound, dialogue and return to the shop without a body or police flags");
        Record(host ? "PASS host paired opening and security" : "PASS guest paired opening and security", "original Butcher survivor branch");
    }

    private void TickFatalAugShot(double now)
    {
        if (pairedStep == 7) return;
        const string customer = "QA_FATAL_AUG_SHOT";
        var store = PlayerStore.Instance;
        var ui = StoreUIManager.Instance;
        var world = host ? NativeWorld.Host : NativeWorld.Guest;
        var current = store.currentClientInstance?.GetClientBlueprint();
        if (pairedStep == 4 && host)
        {
            if (store.storeState == PlayerStore.StoreState.MORNING)
            {
                if (store.CanStartDay()) StoreShutterButton.Instance.OnClick();
                return;
            }
            Require(!store.IsClientPresent(), "fatal aug test requires an empty store");
            Shotgun.Unlock();
            var target = new StoreClient { identifier = customer, displayName = "QA visitor",
                spriteName = SpriteDict.GetRandomScavSpriteName(), isIntroduced = true, dismissable = true,
                clientIntent = StoreClient.ClientIntent.SELL, clientFaction = StoreClient.FACTION_LOWER,
                mainDialogue = new Dialogue().SetText("CLIENT", "QA visitor arrival") };
            AugHelper.MakeDisguisedAug(target, 1);
            Require(target.isAug && !target.survivesShot, "expected a susceptible disguised aug");
            cashBeforeShot = store.playerCash;
            store.storeClientManager.AddNextClient(target);
            store.TryCallNextClient();
            pairedStep = 5;
            return;
        }
        if (pairedStep == 4 && !host && current?.identifier == customer && store.isClientArrived && !ui.isAboutToStartMainDialog)
        {
            if (world.HasDialogue) { DialogUIManager.Instance.OnAdvancePressed(); return; }
            // Exercise different native frame rates without slowing the socket
            // or changing the host timeline.
            ui.bodyFrameDuration = 2.7f / Math.Max(1, ui.shootingBodySprites.Count);
            ui.shotgun.GetComponent<Shotgun>().OnClick();
            pairedStep = 5;
        }
        if (pairedStep == 5 && !host && world.ShootingState == NativeWorld.ShootingPhase.Aiming &&
            ui.isShootingMode && ui.shootingUI.activeSelf)
        {
            ui.Shoot();
            pairedStep = 6;
        }
        if (pairedStep < 5) return;
        bodySeenAfterShot |= ui.body.activeSelf || ui.shootingBodyRenderer.gameObject.activeSelf;
        var officer = ui.secGameObject;
        var path = officer.GetComponent<DOTweenPath>().tween;
        bool cameraVisible = ui.mainCamera.activeSelf && !ui.isShootingMode && !ui.blackscreen.gameObject.activeInHierarchy;
        var dialogue = DialogUIManager.Instance;
        if (!host && (dialogue.secBubble.gameObject.activeSelf || dialogue.secNameBubble.activeSelf))
        {
            bool atCounter = cameraVisible && officer.gameObject.activeSelf && path != null &&
                !path.IsBackwards() && path.ElapsedPercentage() >= 0.999f;
            policeDialogueEarly |= !atCounter;
            if (atCounter && !policeDialogueSeen) NativeOffscreenCapture.Capture("qa-fatal-aug-police-dialogue");
            policeDialogueSeen |= atCounter;
        }
        if (!host && world.ShootingState == NativeWorld.ShootingPhase.Police && !cameraVisible)
        {
            policeArrivedDuringShot |= ui.isShootingMode;
            policeMovedOffCamera |= officer.gameObject.activeSelf;
        }
        if (!host && officer.gameObject.activeSelf)
        {
            var position = officer.transform.localPosition;
            if (cameraVisible && officerSeen && path != null && path.IsActive() && path.IsPlaying() &&
                Vector3.Distance(officerLastPosition, position) > 0.001f)
            {
                if (path.IsBackwards())
                {
                    if (++policeDepartureFrames == 60 && WithholdOfficerDepartureCue)
                        NativeOffscreenCapture.Capture("qa-fatal-aug-police-recovered-departure");
                }
                else if (++policeApproachFrames == 60) NativeOffscreenCapture.Capture("qa-fatal-aug-police-arrival");
            }
            officerSeen = true;
            officerLastPosition = position;
        }
        if (host && world.ShootingState == NativeWorld.ShootingPhase.Police && world.HasDialogue && now >= nextShotDialogueAdvance)
        {
            if (nextShotDialogueAdvance == 0) { nextShotDialogueAdvance = now + 4; return; }
            nextShotDialogueAdvance = now + 0.4;
            DialogUIManager.Instance.OnAdvancePressed();
        }
        if (world.ShootingState != NativeWorld.ShootingPhase.Complete || ui.isShootingMode ||
            ui.body.activeSelf || ui.isShot || officer.gameObject.activeSelf || current?.identifier == customer) return;
        Require(bodySeenAfterShot && ui.mainCamera.activeSelf && !ui.secGameObject2.gameObject.activeSelf,
            "the original body or subsequent cleanup was missing");
        if (host)
        {
            Require(NativeShootingQaEvidence.SourceShots == 1 && NativeShootingQaEvidence.SourceShootouts == 1 &&
                NativeShootingQaEvidence.GoodKills == 1 && store.playerCash > cashBeforeShot,
                "the native fatal aug aftermath/reward must run once at the host");
        }
        else
        {
            Require(policeArrivedDuringShot && !policeMovedOffCamera && policeApproachFrames >= 12 && policeDepartureFrames >= 12 &&
                NativeShootingQaEvidence.LocalGunSounds == 1 && NativeShootingQaEvidence.LocalBodySounds == 1 &&
                NativeShootingQaEvidence.SourceShootouts == 0 && NativeShootingQaEvidence.GoodKills == 0,
                $"guest aug aftermath: approachFrames={policeApproachFrames} departureFrames={policeDepartureFrames} " +
                $"gun={NativeShootingQaEvidence.LocalGunSounds} body={NativeShootingQaEvidence.LocalBodySounds} " +
                $"duringShot={policeArrivedDuringShot} offCamera={policeMovedOffCamera}");
            Require(policeDialogueSeen && !policeDialogueEarly,
                $"security dialogue must wait for the guest's original walk: shown={policeDialogueSeen} early={policeDialogueEarly}");
            if (WithholdOfficerDepartureCue)
            {
                Require(officerDepartureCueWithheld, "officer departure cue was not withheld");
                Record("PASS guest coalesced officer departure", "using only the terminal hidden cue, officer walked out locally");
            }
            Record("PASS guest aug shooting aftermath", $"original path moving frames: arrival {policeApproachFrames}, departure {policeDepartureFrames}; local gun and body audio once");
            Record("PASS guest security dialogue timing", "native security speech and name appear only after the local officer reaches the counter");
        }
        pairedStep = 7;
        Record(host ? "PASS host paired opening and security" : "PASS guest paired opening and security", "native fatal aug shot and security cleanup");
    }

    [HarmonyPatch(typeof(NativeWorld), "ApplyOfficer")]
    private static class QaWithholdOfficerDeparture
    {
        private static bool Prepare() => WithholdOfficerDepartureCue;

        private static bool Prefix(JArray data)
        {
            if (!NativeGuest.Active || (NativeWorld.OfficerMotion)(int)data[1] != NativeWorld.OfficerMotion.Leaving)
                return true;
            if (!officerDepartureCueWithheld)
                Debug.Log("[probably_coop QA] withheld original officer departure cue");
            officerDepartureCueWithheld = true;
            return false;
        }
    }

    private void CheckSharedPerkStory()
    {
        var store = PlayerStore.Instance;
        if (openingCommissaryQa)
        {
            Require(store.isStampIntroduced && StartingPerk.IsBannedCommissary() == host &&
                AfterhourLocation.Get(AfterhourLocation.COMMISSARY).IsDiscovered() == !host,
                "a personal commissary ban altered the other role's access or removed the shared introduction");
            if (host)
            {
                Require(!StoreClientManager.IsClientBlockedByBannedCommissary(new StoreClient { identifier = "scavStamp" }),
                    "the host's personal commissary ban still suppressed the shared stamp seller");
                // Simulate the missing shared flag left by an earlier build
                // after the original introduction date has already passed.
                store.isStampIntroduced = false;
                NativeSharedPerkEvents.RestoreMissedCommissaryIntroduction(NativeActorSession.Current);
                Require(store.isStampIntroduced, "the cooperative load repair did not restore the missing introduction");
            }
            Record("PASS mixed commissary ban", "one shared introduction; only the banned role has no access");
        }
        if (!openingSentinelQa) return;
        Require(HealthData.IsSentinel() == !host && store.isInjectorIntroduced &&
            EmporiumEntry.Instance.GetAllOwnedItems().Count(item =>
                item.FindItemFeatureByID("CATEGORY_GENUINE_INJECTOR")?.realCondition?.identifier == "genuineInjector") == 1,
            "the sentinel effect was copied to the other role or the opening injector was omitted/duplicated");
        if (host)
        {
            var station = StoreStation.Instance;
            int originalDay = station.dayCounter;
            try
            {
                foreach (int day in new[] { 5, 6, 7, 13 })
                {
                    station.dayCounter = day;
                    var schedule = new StoreClientManager();
                    schedule.HandleScalperClient();
                    Require(schedule.clientStack.Count(client => client.identifier == "scalper") == (day % 7 == 6 ? 1 : 0),
                        "the partner sentinel did not retain the original weekly supplier schedule");
                }
            }
            finally { station.dayCounter = originalDay; }
        }
        Record("PASS partner sentinel supply", "one shared injector, original weekly supplier, personal health effect");
    }

    private void CheckPairedCareerSchedule()
    {
        var station = StoreStation.Instance;
        var queueProbe = new StoreClientManager();
        int originalDay = station.dayCounter;
        try
        {
            queueProbe.HandleCartelClient();
            Require(queueProbe.clientStack.Count(row => row.identifier == "cartelIntro") == 1,
                "the partner chemist's day-one introduction was missing or repeated");
            queueProbe.clientStack.Clear();
            station.dayCounter = 3;
            queueProbe.HandleCartelClient();
            queueProbe.HandleContentUnlockClient();
            foreach (string expected in new[] { "street_dealer", "shadyFarmer", "retired_water_merchant" })
                Require(queueProbe.clientStack.Count(row => row.identifier == expected) == 1,
                    $"both career schedules should keep exactly one copy of {expected}");
            queueProbe.clientStack.Clear();
            station.dayCounter = 4;
            queueProbe.HandleCartelClient();
            Require(queueProbe.clientStack.Count(row => row.identifier == "cartelHandlerCheckin") == 1,
                "the partner chemist's first recurring obligation was missing or repeated");
            Record("PASS paired-career-schedule", "native day 1 introduction, day 3 chemist and water merchant in the same queue, and day 4 check-in");
        }
        finally { station.dayCounter = originalDay; }
    }

    private void CheckFenceGunsmithSchedule()
    {
        var station = StoreStation.Instance;
        var queueProbe = new StoreClientManager();
        int originalDay = station.dayCounter;
        try
        {
            queueProbe.HandleSkipClient();
            foreach (string expected in new[] { "janitorUEIntroductionFence", "ll_gun_buyer" })
                Require(queueProbe.clientStack.Count(row => row.identifier == expected) == 1,
                    $"the combined day-one opening schedule should have exactly one {expected}");
            queueProbe.clientStack.Clear();
            station.dayCounter = 2;
            queueProbe.HandleSkipClient();
            Require(queueProbe.clientStack.Count(row => row.identifier == "ll_gun_seller") == 1,
                "the partner gunsmith's day-two seller did not follow the day-one buyer");
            Record("PASS fence-gunsmith-schedule", "original underground exchange introduction and gun buyer on day 1, then gun seller on day 2");
        }
        finally { station.dayCounter = originalDay; }
    }

    private bool TickLabelLocaleQa()
    {
        if (labelLocaleChecked) return true;
        const string category = "CATEGORY_WATER_PURITY";
        var entry = EmporiumEntry.Instance;
        Require(LocHelper.GetCurrentLocaleCode() == (host ? "en" : "zh"), "test profiles need different languages");
        if (host)
        {
            if (labelLocaleWater == null)
            {
                foreach (string id in new[] { "labeler", "labeler_blue", "small_bottled_water" })
                {
                    var item = DirectoryMaster.Item(id);
                    GeneralHelper.SetItemOwned(item, true);
                    if (id == "small_bottled_water")
                    {
                        WaterHelper.FillWithPureWater(item);
                        GeneralHelper.SetCustomName(item, "H2O cold ★");
                        labelLocaleWater = item;
                        var original = item.FindItemFeatureByCategory(category).GetCurrentItemCondition();
                        labelLocaleSelected = ItemConditionList.conditionLookup[category].First(option => option.identifier != original.identifier).identifier;
                    }
                    Require(TryPlaceInEmptyCell(item, entry.backInvinvElement) ||
                        TryPlaceInEmptyCell(item, entry.invElement), "test label inventory full: " + id);
                }
                Record("PASS label-locale-host", "English host seeded native printers, a standard label and a custom name");
                return false;
            }
            if (labelLocaleWater.FindItemFeatureByCategory(category).fakeCondition?.identifier != labelLocaleSelected)
                return false;
            Record("PASS label-locale-edit-host", "guest dropdown changed the native label condition using the same identifier");
        }
        else
        {
            var items = entry.backInvinvElement.childItems.Concat(entry.invElement.childItems).ToArray();
            var water = items.FirstOrDefault(item => GeneralHelper.GetCustomName(item) == "H2O cold ★");
            if (water == null) return false;
            foreach (string id in new[] { "labeler", "labeler_blue" })
            {
                var printers = items.OfType<GameItemElement>().Where(item => item.identifier == id).ToArray();
                string localName = LocHelper.GetLocalizedItem("item_" + id + "_name");
                Require(printers.Length > 0 && printers.All(printer => printer.name == localName &&
                    printer.GetTooltip().nakedText.Contains(localName) && printer.GetTooltipAdvanced().nakedText.Contains(localName)),
                    "remote labeler text did not use the local language: " + id);
            }
            var ui = LabelerUIManager.Instance;
            if (!labelLocaleOpened)
            {
                if (labelLocaleTool == null)
                {
                    labelLocaleTool = items.First(item => item.identifier == "labeler");
                    ItemSelectHandler.current.TrySelectItem(labelLocaleTool);
                    return false;
                }
                if (!labelLocaleTool.MayTarget(water) || !labelLocaleTool.CanTarget(water)) return false;
                labelLocaleTool.Target(water);
                ItemSelectHandler.current.EndSelect();
                Require(ui.panel.activeSelf, "original guest targeting did not open the label editor");
                labelLocaleOpened = true;
                return false;
            }
            var condition = water.FindItemFeatureByCategory(category).GetCurrentItemCondition();
            if (labelLocaleSelected != null && labelLocaleSelected != condition.identifier) return false;
            var native = ItemConditionList.conditionLookup[category].Single(entry => entry.identifier == condition.identifier);
            var row = ui.verticalLayoutGroup.GetComponentsInChildren<LabelerElement>().Single(entry => entry.GetCategory() == category);
            Require(condition.modValue == native.modValue && row.currentFeatureDisplay.text.Contains(native.display) &&
                native.display.Any(c => c is >= '\u4e00' and <= '\u9fff') && water.GetDisplayName(preferCustomName: true) == "H2O cold ★",
                "standard label, numeric condition or custom name did not match");
            if (labelLocaleSelected == null)
            {
                NativeOffscreenCapture.Capture("qa-cross-language-label");
                var choices = ItemConditionList.conditionLookup[category];
                int next = choices.FindIndex(option => option.identifier != condition.identifier);
                Require(next >= 0, "fixture needs another label condition");
                labelLocaleSelected = choices[next].identifier;
                row.dropdown.SetValueWithoutNotify(next);
                row.OnDropdownValueChanged();
                return false;
            }
            ui.CloseUI();
            Record("PASS label-locale-guest", "Chinese original label row and printer names; guest edit in Chinese confirmed against English authority; custom name unchanged");
        }
        labelLocaleChecked = true;
        return true;
    }

    private void TickOpeningRentQa(double now)
    {
        var store = PlayerStore.Instance;
        if (rentStep == 0)
        {
            Require(store.startType == NewGameData.StartType.GUNSMITH && NativeOpening.UsesCareer(store, NewGameData.StartType.AQUAFARMER),
                "the native opening did not apply both costly careers");
            Require(store.playerCash == 0 && OpeningRent.Describe(store) == (30, 300), "default four-week opening debt is not 30/60/90/120");
            Record("PASS opening-rent-initial", "both careers initialized; no negative cash; first rent surcharge 30 of 300");
            if (!host)
            {
                rentStep = 1;
                return;
            }
            Require(CoopRun.VerifyCheckpoint(store, 1), "debt was not saved in the day-one co-op checkpoint");
            securityAt = now + 3;
            rentStep = 1;
        }
        if (!host)
        {
            if (OpeningRent.Describe(store).Balance != 0 || !store.IsPropertyPaid) return;
            Finish("PASS guest paired opening and security");
            return;
        }
        if (rentStep != 1 || now < securityAt) return;
        store.playerCash = 1000000;
        int cash = store.playerCash;
        store.OnRentNotPaid();
        Require(store.playerCash == cash && OpeningRent.Describe(store) == (60, 300),
            "rent grace changed the total or lost this week's debt");
        int rent = store.GetRentValue();
        store.OnRentPaid();
        Require(store.GetRentValue() == rent && store.playerCash == cash, "duplicate same-day rent callback charged again");
        StoreStation.Instance.dayCounter += 7;
        store.OnRentPaid();
        Require(store.playerCash == cash - rent && OpeningRent.Describe(store) == (90, 210),
            "next rent did not settle the two previous instalments exactly once");
        int quote = store.GetStoreBuyPrice();
        cash = store.playerCash;
        store.BuyOutStore();
        Require(store.IsPropertyPaid && store.playerCash == cash - quote && OpeningRent.Describe(store) == (0, 0),
            "native buyout did not retire the remaining debt");
        Record("PASS opening-rent-grace-payment-buyout", "native grace, duplicate callback, next payment, and buyout all matched the ledger");
        Record("PASS host paired opening and security", "opening rent branch");
        rentStep = 2;
    }

    private void CheckRegisteredOpening()
    {
        var store = PlayerStore.Instance;
        var stock = EmporiumEntry.Instance.invElement.childItems.Concat(EmporiumEntry.Instance.backInvinvElement.childItems).ToArray();
        Require(ModHelper.GetStartingScenarioId() == PairedHostCareerId &&
            ModHelper.IsStartingScenario("water_merchant") && ModHelper.IsStartingScenario(ModCareerId),
            "the native membership query did not retain both selected careers");
        Require(stock.Count(item => GeneralHelper.GetCustomName(item) == ModCareerItem) == 1 &&
            stock.Any(item => item.identifier == "water_jug"), "one custom and one native starting package must be present");
        Require(EmporiumEntry.IsWaterTapUnlocked() && store.networkUpgrade["WATER_TRADER"].state == 2 &&
            store.playerCash == 0 && OpeningRent.Describe(store) == (3, 30),
            "the shared utility, native technology, cash and career installments do not match");
        Require(host ? CoopRun.VerifyCheckpoint(store, 1) : store.saveSlotId == -1,
            "the opening did not save exactly one host checkpoint");
        Require(ModHelper.IsStartingPerkActive(ModPersonalPerk) == !host &&
            ModHelper.IsStartingPerkActive(ModSharedPerk),
            "custom perk queries did not distinguish personal and shared choices");
        var calendar = AdvCalendarUIManager.Instance;
        NativeWorld.RefreshingCalendar = true;
        try
        {
            calendar.OpenUI();
            calendar.OnStatusButtonClicked();
            var shown = calendar.activePerksContainer.GetComponentsInChildren<StartingPerkElement>();
            Require(shown.Count(row => row.id == ModSharedPerk) == 1 &&
                shown.Count(row => row.id == "working_pipes") == 1 &&
                shown.Count(row => row.id == ModPersonalPerk) == (host ? 0 : 1),
                "the original status panel omitted a shared perk or showed another player's personal perk");
            NativeOffscreenCapture.Capture("qa-paired-opening-active-perks");
            Record("PASS paired-opening status perks", "built-in and Mod shop perks each visible once; personal Mod perk stayed with the guest");
        }
        finally
        {
            calendar.CloseUI();
            NativeWorld.RefreshingCalendar = false;
        }
        if (host)
        {
            var session = NativeActorSession.Current;
            var providers = NativeRoomModManifest.ReadSavedOpeningMods(session.VerifiedCheckpoint.OpeningMods);
            Require(providers?.ContainsKey("probably_coop_extension_probe") == true,
                "the opening provider is not present in its verified checkpoint");
            Require(session.Run.Partner.startingPerks.Any(perk => perk.id == ModPersonalPerk) &&
                !session.Run.Partner.startingPerks.Any(perk => perk.id == ModSharedPerk) &&
                NativeActorPartnerSetup.IsComplete(store.modData[NativeActorPartnerSetup.SaveKey], session.Run.Run),
                "the saved companion selection does not distinguish personal and shared perks");
        }
        Finish("PASS registered career and perk paired opening");
    }

    private static void ChooseNativePerk(string id)
    {
        var type = typeof(NewGamePerksUI);
        var perks = (List<StartingPerk>)AccessTools.Field(type, "sorted").GetValue(null);
        int index = perks.FindIndex(perk => perk.id == id);
        Require(index >= 0, "perk not present in native picker: " + id);
        AccessTools.Field(type, "page").SetValue(null, index / 36);
        AccessTools.Method(type, "Rebuild").Invoke(null, null);
        AccessTools.Method(type, "OnSlotClicked").Invoke(null, new object[] { id });
        Require(NewGameData.Instance.startingPerks.Any(perk => perk.id == id), "native perk selection ignored: " + id);
    }

    private bool TickPairedTravel()
    {
        var map = MapUIManager.Instance;
        var store = PlayerStore.Instance;
        var world = host ? NativeWorld.Host : NativeWorld.Guest;
        const string destination = AfterhourLocation.DUMPING_GROUND;
        var location = AfterhourLocation.Get(destination);
        if (host)
        {
            if (pairedTravelStep == 0)
            {
                StoreUIManager.Instance.CloseAllUI();
                store.storeState = PlayerStore.StoreState.CLOSED;
                store.isAfterhoursIntroduced = true;
                store.visitLeftTonight = 1;
                map.Travel(location);
                Require(world.TripReady == 1, "host did not record its departure intent");
                pairedTravelStep = 1;
            }
            if (pairedTravelStep == 1 && map.Current?.Id == destination)
            {
                Require(store.GetAfterhourState(destination).visits == 1, "native location initialized more than once");
                var ground = EmporiumEntry.Instance.afterhourInventory;
                Require(TryPlaceInEmptyCell(DirectoryMaster.Item("processed_meat"), ground), "cannot seed an empty test ground");
                if (SharedFieldAudioQa && NativeActorSession.Current is { } session)
                {
                    using var scope = session.Enter(NativeActorRun.Slot.Partner);
                    NativeActorFieldAccess.Set_dumpingGroundMinorWoundImmunitySearchesLeft(store, 1);
                    NativeActorFieldAccess.Set_dumpingGroundMajorWoundImmunitySearchesLeft(store, 1);
                }
                pairedTravelStep = 2;
            }
            if (pairedTravelStep == 2 && world.ProposedTrip == "return" && world.TripReady == 2)
            {
                map.Return();
                pairedTravelStep = 3;
            }
            if (pairedTravelStep == 3 && !map.IsOutside && !map.IsInTransit)
            {
                Require(EmporiumEntry.Instance.afterhourInventory.childItems.Count == 0, "native return did not clear the shared ground");
                store.storeState = PlayerStore.StoreState.MORNING;
                pairedTravelStep = 4;
                Record("PASS host-paired-ground", "both travel confirmations accepted, original location ran once and cleared after return");
            }
        }
        else
        {
            if (pairedTravelStep == 0 && world.ProposedTrip == destination && world.TripReady == 1)
            {
                map.Travel(location);
                pairedTravelStep = 1;
            }
            if (pairedTravelStep == 1 && map.Current?.Id == destination &&
                EmporiumEntry.Instance.afterhourInventory.childItems.Any(item => item.identifier == "processed_meat"))
            {
                if (SharedFieldAudioQa)
                {
                    var sound = StoreSoundPlayer.Instance;
                    var siteAmbient = sound.GetAmbient(location.AmbienceId);
                    Require(siteAmbient != null && sound.ambianceAudioSource.clip == siteAmbient,
                        "guest kept its shop ambience at the dumping ground");
                    Require(guestDoorSounds == 0, "guest played a return cue before leaving the dumping ground");
                    if (!pairedScavengeSent)
                    {
                        if (NativeActorFieldAccess.Get_dumpingGroundMinorWoundImmunitySearchesLeft(store) != 1 ||
                            !ScavHelper.CanScavenge()) return false;
                        pairedScavengeBefore = ScavHelper.GetScavTimeLeft();
                        ScavHelper.ScavengeDumpingGrounds();
                        pairedScavengeSent = true;
                        return false;
                    }
                    if (ScavHelper.GetScavTimeLeft() != pairedScavengeBefore - 1 || world.QaScavengeCues == 0) return false;
                    Require(world.QaScavengeCues == 1, "the original scavenging sound repeated");
                    Record("PASS guest native scavenging audio", "original partner request consumed one attempt and played the local sound once");
                }
                NativeOffscreenCapture.Capture("qa-paired-ground");
                map.Return();
                pairedTravelStep = 2;
            }
            if (pairedTravelStep == 2 && !map.IsOutside)
            {
                Require(EmporiumEntry.Instance.afterhourInventory.childItems.Count == 0, "guest still has cleared ground items after return");
                if (SharedFieldAudioQa)
                {
                    var sound = StoreSoundPlayer.Instance;
                    Require(sound.storeAmbient != null && sound.ambianceAudioSource.clip == sound.storeAmbient,
                        "guest did not restore the original shop ambience on return");
                    Require(guestDoorSounds == 1, "guest did not play the original return door sound exactly once");
                    Record("PASS guest native travel audio", "local dumping ground ambience, shop ambience restored, one return door cue");
                }
                pairedTravelStep = 4;
                Record("PASS guest-paired-ground", "partner projected the host's real ground at the original location and returned to the store");
            }
        }
        return pairedTravelStep == 4;
    }

    [HarmonyPatch(typeof(AudioManager), nameof(AudioManager.Play2))]
    private static class QaGuestTravelDoor
    {
        private static bool Prepare() => SharedFieldAudioQa;

        private static void Postfix(string identifier, bool __result)
        {
            if (NativeGuest.Active && __result && identifier == "close_door") guestDoorSounds++;
        }
    }
}
#endif
