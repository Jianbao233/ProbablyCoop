#if PROBABLY_COOP_GRID_QA
using System;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Core;
using UnityEngine;

namespace ProbablyCoop;

internal sealed partial class NativeBackgroundQa
{
    private enum StoryFixture { None, Bomb, BombLate, Cartel, Serpico }

    private static readonly StoryFixture StoryQa = Array.Find(Environment.GetCommandLineArgs(),
        arg => arg.StartsWith("--probably-coop-qa-story-", StringComparison.Ordinal)) switch
    {
        "--probably-coop-qa-story-bomb" => StoryFixture.Bomb,
        "--probably-coop-qa-story-bomb-late" => StoryFixture.BombLate,
        "--probably-coop-qa-story-cartel" => StoryFixture.Cartel,
        "--probably-coop-qa-story-serpico" => StoryFixture.Serpico,
        _ => StoryFixture.None,
    };
    private static readonly bool StoryAfterRejoin =
        Array.IndexOf(Environment.GetCommandLineArgs(), "--probably-coop-qa-story-after-rejoin") >= 0;

    private static int storyCountdowns, storyExplosions, storyCartelShots, storySerpicoShots;
    private static double storyCountdownAt, storyExplosionAt;
    private static bool withheldCountdown;
    private bool serpicoHidden, storyDialogueStarted;

    private static bool IsBombFixture => StoryQa is StoryFixture.Bomb or StoryFixture.BombLate;

    private void TickStoryHost(RoomSelector room, double now)
    {
        if (room.Session?.State != LinkState.Connected || room.Session.Native.FullRevision == 0 ||
            NativeWorld.Host?.PartnerSetupRequiredForQa != false) return;
        var store = PlayerStore.Instance;
        var ui = StoreUIManager.Instance;
        var dialog = DialogUIManager.Instance;
        if (stage == 1)
        {
            AdvCalendarUIManager.Instance.CloseUI();
            EmporiumOption.Instance.autoNextDialog = false;
            if (!IsBombFixture)
            {
                Require(store.CanStartDay(), "story fixture expected a ready shop");
                StoreShutterButton.Instance.OnClick();
            }
            stage = 2;
            next = now + 5;
        }
        if (now < next) return;
        if (stage == 2)
        {
            if (IsBombFixture) store.SetupRevBombing();
            else
            {
                if (store.lockNextBellFlag != 0) return;
                var visitor = StoryQa == StoryFixture.Cartel ? StoreClientListCartel.CreateCartelHandlerWeekly() : StoreClientUniqueList.Serpico6();
                // Exercise the original arrest conversation using its original callbacks.
                if (StoryQa == StoryFixture.Serpico) visitor.mainDialogue = null;
                store.storeClientManager.clientStack.Clear();
                store.storeClientManager.AddNextClient(visitor);
                store.TryCallNextClient();
            }
            stage = 3;
        }
        if (StoryQa == StoryFixture.Serpico && stage == 3 && store.isClientArrived && !ui.isAboutToStartMainDialog)
        {
            StoreSecButton.Instance.OnClick();
            stage = 4;
        }
        if (dialog.currentDialog != null)
        {
            storyDialogueStarted = true;
            if (dialog.IsChoice()) dialog.SelectChoice(0);
            else dialog.OnAdvancePressed();
            next = now + 1.3;
        }
        bool complete = StoryQa == StoryFixture.Serpico
            ? storySerpicoShots == 1 && storyDialogueStarted && !ui.clientGameObject.gameObject.activeSelf &&
                !ui.secGameObject.gameObject.activeSelf && !ui.secGameObject2.gameObject.activeSelf
            : NativeWorld.Host.EndingActive;
        if (!complete) return;
        Record("PASS host story presentation", "the original story completed; keep the host alive for the guest presentation");
        done = true;
    }

    private void TickStoryGuest(RoomSelector room, double now)
    {
        var world = NativeWorld.Guest;
        if (world?.Ready != true || room.Session?.State != LinkState.Connected) return;
        if (StoryAfterRejoin)
        {
            if (!world.EndingActive) return;
            Require(GameOverUIManager.Instance.uiPanel.activeInHierarchy && storyCountdowns == 0 && storyExplosions == 0 &&
                PlayerStore.Instance.saveSlotId == -1, "rejoining replayed the past bombing or missed the original ending");
            Finish("PASS guest restored story ending after rejoin");
            return;
        }
        var ui = StoreUIManager.Instance;
        if (StoryQa == StoryFixture.Serpico)
        {
            if (storySerpicoShots > 0 && !ui.clientGameObject.gameObject.activeSelf && world.HasDialogue)
            {
                if (!serpicoHidden) NativeOffscreenCapture.Capture("qa-serpico-customer-hidden");
                serpicoHidden = true;
            }
            if (storySerpicoShots > 0 && serpicoHidden)
                Require(!ui.clientGameObject.gameObject.activeSelf, "Serpico reappeared when the officers left");
            if (ui.secGameObject.gameObject.activeSelf || ui.secGameObject2.gameObject.activeSelf || world.HasDialogue) return;
            if (storySerpicoShots == 0) return;
            Require(storySerpicoShots == 1 && serpicoHidden, "missing Serpico disappearance or duplicated original gunfire");
        }
        else
        {
            if (!world.EndingActive) return;
            if (StoryQa == StoryFixture.Cartel)
                Require(storyCartelShots == 1, "Cartel ending displayed without exactly one original gunshot");
            else if (StoryQa == StoryFixture.BombLate)
                Require(withheldCountdown && storyCountdowns == 0 && storyExplosions == 1,
                    "a coalesced terminal frame replayed the obsolete countdown or lost the explosion");
            else
                Require(storyCountdowns == 1 && storyExplosions == 1 && storyExplosionAt - storyCountdownAt > 2,
                    "bombing did not play the original countdown before the original explosion");
            NativeOffscreenCapture.Capture("qa-story-terminal-ending");
        }
        Require(PlayerStore.Instance.saveSlotId == -1, "story granted a local save slot to the guest");
        Record("PASS original story audio", $"{StoryQa} countdown={storyCountdowns}, explosion={storyExplosions}, cartel={storyCartelShots}, Serpico={storySerpicoShots}");
        Finish("PASS guest story presentation");
    }

    [HarmonyPatch(typeof(NativeWorld), "ApplyAudio")]
    private static class WithholdIntermediateCountdown
    {
        private static bool Prepare() => StoryQa == StoryFixture.BombLate;

        private static bool Prefix(NativeWorld __instance, JToken payload)
        {
            if (!NativeGuest.Active || __instance.EndingActive || payload is not JArray rows) return true;
            foreach (var token in rows)
            {
                if (token is not JArray row || row.Count != 2 || (int)row[1] != (int)NativeWorld.AudioCue.BombCountdown) continue;
                withheldCountdown = true;
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(AudioManager), nameof(AudioManager.Play2))]
    private static class CountOriginalStorySounds
    {
        private static bool Prepare() => StoryQa != StoryFixture.None;

        private static void Postfix(string identifier, bool __result)
        {
            if (!__result) return;
            switch (identifier)
            {
                case "countdown": storyCountdowns++; storyCountdownAt = Time.realtimeSinceStartupAsDouble; break;
                case "explosionDebris": storyExplosions++; storyExplosionAt = Time.realtimeSinceStartupAsDouble; break;
                case "gunshot": storyCartelShots++; break;
                case "pistol_burst": storySerpicoShots++; break;
            }
        }
    }
}
#endif
