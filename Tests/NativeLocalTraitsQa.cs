#if PROBABLY_COOP_GRID_QA
using System;
using System.Linq;
using HarmonyLib;
using ProbablyCoop.Core;
using UnityEngine;

namespace ProbablyCoop;

internal sealed partial class NativeBackgroundQa
{
    private readonly bool localTraitsQa =
        Array.IndexOf(Environment.GetCommandLineArgs(), "--probably-coop-qa-local-traits") >= 0;
    private string[] traitsTracks;
    private float traitsMusicVolume;
    private double traitsFinishAt;

    private void TickLocalTraits(RoomSelector room, NativeShop shop, bool inMenu, double now)
    {
        Require(!Application.isBatchMode && now < started + 110, "local traits QA needs a rendered isolated pair");
        if (host && !loaded && inMenu && room.IsHost)
        {
            loaded = true;
            NewGameData.HardReset();
            CoopRun.Armed = true;
            var data = NewGameData.Instance;
            data.startType = NewGameData.StartType.WATER_MERCHANT;
            data.skipIntro = true;
            data.startingPerks.AddRange(NativeActorPartnerSetup.ValidateSelection(
                new[] { "lucky", "gambling_addiction", "nicotine_addiction" }));
            MainMenuUIController.Instance.StartNewGameClick();
            return;
        }
        if (host)
        {
            if (shop == null) return;
            TestHostTraits(room, now);
        }
        else if (room.Session?.State == LinkState.Connected && NativeWorld.Guest?.Ready == true)
            TestGuestTraits();
    }

    private void TestHostTraits(RoomSelector room, double now)
    {
        var store = PlayerStore.Instance;
        var session = NativeActorSession.EnsureForExisting(store);
        var deck = EmporiumEntry.Instance.cassettePlayer;
        if (stage == 0)
        {
            Require(CoopRun.VerifyCheckpoint(store, 1) && store.healthData.luck == 15 &&
                store.healthData.daysSinceNicotineConsumption == 3, "the original host setup applied +15 and withdrawal");
            var draft = NativeActorPartnerSetup.CreateDraft(session.Run.Run,
                new[] { "unlucky", "gambling_addiction", "alcohol_addiction" });
            store.modData[NativeActorPartnerSetup.SaveKey] =
                NativeActorPartnerSetup.Prepare(session.Run, draft, null).Commit();
            var tapes = NativeLocalCassette.PlayingInventory(deck);
            foreach (string id in new[] { "cassette1", "cassette2" })
                Require(GraphUtils.TryAcceptAll(tapes, DirectoryMaster.Item(id)) == 0, "place original " + id);
            Require(GraphUtils.TryAcceptAll(EmporiumEntry.Instance.invElement, DirectoryMaster.Item("black_injector")) == 0,
                "place original addiction-removal consumable");
            Require(EmporiumEntry.Instance.GetAllOwnedItems().Count(item => item.IsID("gamblers_coin")) == 1,
                "both gambling perks share one original starting gift");
            traitsTracks = MachineCassettePlayer.GetTrackListFromCassette(tapes).ToArray();
            Require(traitsTracks.Length >= 2 && traitsTracks[0] != traitsTracks[1] &&
                traitsTracks.Take(2).All(AudioManager.Instance.CanPlay), "two native tracks are available");
            traitsMusicVolume = EmporiumOption.Instance.musicVolume;
            PressCassette(deck, NativeLocalCassette.Control.Play);
            PressCassette(deck, NativeLocalCassette.Control.Next);
            AssertCassette(deck, 1, traitsTracks[1]);
            Debug.Log("[probably_coop QA] TRAITS ROOM " + room.Code);
            stage++;
            return;
        }
        if (stage == 1)
        {
            if (ModHelper.ReadModDataFileText("probably_coop", "qa-local-traits-advance.txt") != "advance") return;
            AssertCassette(deck, 1, traitsTracks[1]);
            Require(Mathf.Approximately(traitsMusicVolume, EmporiumOption.Instance.musicVolume),
                "guest controls did not change the host volume");
            for (int night = 1; night <= 17; night++)
            {
                int day = StoreStation.Instance.dayCounter;
                var result = session.Night.ExecuteForDay(day, store.healthData, session.Run.Partner.healthData,
                    slot => session.Enter((NativeActorRun.Slot)slot), () => StoreStation.Instance.dayCounter == day, () => session.Terminal);
                Require(result == NativeActorNight.Result.Completed, "original cooperative health night completed");
                foreach (var actor in new[] { NativeActorRun.Slot.Host, NativeActorRun.Slot.Partner })
                    using (session.Enter(actor))
                    {
                        var health = NativeActorFieldAccess.Get_healthData(store);
                        Require(HealthData.IsGamblingAddicted() && health.GetTotalAddictionPenalty() == (night < 17 ? 8 : 0),
                            "gambling persists and only substance withdrawal affects the native price penalty");
                        Require((actor == NativeActorRun.Slot.Host ? HealthData.IsNicotineAddicted() : HealthData.IsAlcoholAddicted()) == (night < 17),
                            "original substance addiction clears on the seventeenth night");
                    }
                StoreStation.Instance.dayCounter++;
            }
            Record("PASS traits original nights", "17 native personal nights; both gambling traits persist and only substance addictions cleared");
            stage++;
            return;
        }
        if (stage == 2 && session.Run.Partner.startingPerks.All(perk => perk.id != "gambling_addiction"))
        {
            AssertCassette(deck, 1, traitsTracks[1]);
            Require(HealthData.IsGamblingAddicted(), "partner cure leaves the host gambling perk");
            store.SaveGame();
            Require(CoopRun.VerifyCheckpoint(store, 18) && session.VerifiedCheckpoint.Personal != null &&
                NativeActorPartnerSetup.IsComplete(session.VerifiedCheckpoint.PartnerSetup, session.Run.Run),
                "original save verifies both actors and the original setup receipt after the cure");
            Record("PASS traits host isolation", "host kept its track, volume and gambling trait after the guest used Synaprest");
            stage++;
            traitsFinishAt = now + 4;
        }
        if (stage == 3 && now >= traitsFinishAt) Finish("PASS host runtime");
    }

    private void TestGuestTraits()
    {
        var world = NativeWorld.Guest;
        var store = PlayerStore.Instance;
        var deck = EmporiumEntry.Instance.cassettePlayer;
        if (stage == 0)
        {
            if (StoreUIManager.Instance.blackscreen.gameObject.activeInHierarchy) return;
            Require(store.healthData.luck == -15 && HealthData.IsGamblingAddicted() && HealthData.IsAlcoholAddicted() &&
                store.healthData.GetTotalAddictionPenalty() == 8, "guest original perk queries and Unlucky match the partner");
            var gamble = StartingPerk.GetPerkById("gambling_addiction");
            Require(gamble.GetLocalizedDescription().Contains("Synaprest", StringComparison.OrdinalIgnoreCase) ||
                gamble.GetLocalizedDescription().Contains(LocHelper.GetLocalizedItem("item_black_injector_name")),
                "the original perk tooltip includes the treatment note");
            traitsTracks = MachineCassettePlayer.GetTrackListFromCassette(NativeLocalCassette.PlayingInventory(deck)).ToArray();
            Require(traitsTracks.Length >= 2, "guest received the shared tapes");
            AssertCassette(deck, 0);
            ulong before = world.LastSubmittedUiRequest;
            PressCassette(deck, NativeLocalCassette.Control.Play);
            AssertCassette(deck, 1, traitsTracks[0]);
            PressCassette(deck, NativeLocalCassette.Control.Play);
            AssertCassette(deck, 2, traitsTracks[0]);
            PressCassette(deck, NativeLocalCassette.Control.Play);
            PressCassette(deck, NativeLocalCassette.Control.Next);
            AssertCassette(deck, 1, traitsTracks[1]);
            PressCassette(deck, NativeLocalCassette.Control.Previous);
            AssertCassette(deck, 1, traitsTracks[0]);
            traitsMusicVolume = EmporiumOption.Instance.musicVolume;
            PressCassette(deck, traitsMusicVolume < 0.95f ? NativeLocalCassette.Control.RaiseVolume : NativeLocalCassette.Control.LowerVolume);
            Require(!Mathf.Approximately(EmporiumOption.Instance.musicVolume, traitsMusicVolume) && world.LastSubmittedUiRequest == before,
                "original guest cassette controls play and change volume without a room request");
            Record("PASS traits guest local audio", "original playback, pause, previous, next and volume on the guest");
            var runtime = UnityEngine.Object.FindFirstObjectByType<CoopRuntime>();
            var panel = (CoopPanel)AccessTools.Field(typeof(CoopRuntime), "panel").GetValue(runtime);
            panel.QaVerifyLauncherOption();
            stage++;
            return;
        }
        if (stage == 1)
        {
            if (StoreStation.Instance.dayCounter != 18) return;
            AssertCassette(deck, 1, traitsTracks[0]);
            Require(HealthData.IsGamblingAddicted() && !HealthData.IsAlcoholAddicted() &&
                store.healthData.GetTotalAddictionPenalty() == 0, "guest observes original health progress and keeps its own track");
            PressCassette(deck, NativeLocalCassette.Control.Stop);
            AssertCassette(deck, 0);
            var cure = EmporiumEntry.Instance.invElement.childItems.Single(item => item.IsID("black_injector"));
            Require(cure.CanActivateSlotItem(), "original guest Synaprest is usable for gambling addiction");
            cure.ActivateSlotItem();
            stage++;
            return;
        }
        if (stage == 2 && !HealthData.IsGamblingAddicted())
        {
            Require(StartingPerk.GetPerkById("gambling_addiction") == null &&
                EmporiumEntry.Instance.invElement.childItems.All(item => !item.IsID("black_injector")),
                "original guest perk list and inventory both reflect the cure");
            Record("PASS traits guest cure", "original activation removed only the guest gambling trait and consumed the injector");
            stage++;
        }
    }

    private static void PressCassette(GameItem deck, NativeLocalCassette.Control control)
    {
        var button = NativeMachineControls.Read(deck).OfType<MachineryButton>()
            .Single(candidate => NativeLocalCassette.ReadControl(deck, candidate) == control);
        AccessTools.Method(typeof(MachineryButton), "OnSelect").Invoke(button, null);
    }

    private static void AssertCassette(GameItem deck, int state, string track = null)
    {
        var source = AudioManager.Instance.audioSourceMusic;
        Require(deck.GetTagReadonly(MachineCassettePlayer.CASETTE_PLAYER_STATE_INT).GetInt() == state &&
            (state == 1 ? source.isPlaying : !source.isPlaying) &&
            (track == null || deck.GetTagReadonly(MachineCassettePlayer.CURRENT_PLAYING_TRACK_ID_TAG).GetString() == track),
            "local cassette state, track and audio source");
    }
}
#endif
