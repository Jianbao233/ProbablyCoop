#if PROBABLY_COOP_GRID_QA
using System;
using System.Linq;
using ProbablyCoop.Core;

namespace ProbablyCoop;

internal sealed partial class NativeBackgroundQa
{
    private readonly bool keycardAudioQa =
        Array.IndexOf(Environment.GetCommandLineArgs(), "--probably-coop-qa-keycard-audio") >= 0;
    private const string KeycardBoxName = "QA card box";
    private const string WrongKeycardName = "QA wrong card";
    private const string CorrectKeycardName = "QA correct card";
    private GameItem keycardBox, wrongKeycard, correctKeycard;
    private int keycardStage;
    private bool keycardSelected;

    private void TickKeycardAudioHost(RoomSelector room, double now)
    {
        if (room.Session?.State != LinkState.Connected || room.Session.Native.FullRevision == 0 ||
            NativeWorld.Host?.PartnerSetupRequiredForQa != false) return;
        if (stage == 1)
        {
            AdvCalendarUIManager.Instance?.CloseUI();
            keycardBox = DirectoryMaster.Item("med_box");
            var content = DirectoryMaster.Item("small_bottled_water");
            GeneralHelper.SetItemOwned(content, true);
            Require(GraphUtils.TryAcceptAll((GameInventory)keycardBox.contentWindow.child, content) == 0, "could not stock the QA crate");
            LockHelper.LockUpContainer(keycardBox);
            wrongKeycard = DirectoryMaster.Item("sec_keycard");
            correctKeycard = DirectoryMaster.Item("med_keycard");
            PlaceReloadFixture(keycardBox, KeycardBoxName + " preparing");
            PlaceReloadFixture(wrongKeycard, WrongKeycardName);
            PlaceReloadFixture(correctKeycard, CorrectKeycardName);
            next = now + 5;
            stage = 2;
        }
        if (stage == 2 && now >= next)
        {
            Require(wrongKeycard.CanTarget(keycardBox), "native wrong card cannot reach the crate");
            wrongKeycard.Target(keycardBox);
            Require(LockHelper.IsCurrentlyLocked(keycardBox) && wrongKeycard.parentInventory != null,
                "host wrong card altered the locked crate");
            GeneralHelper.SetCustomName(keycardBox, KeycardBoxName);
            stage = 3;
        }
        if (stage == 3 && !LockHelper.IsCurrentlyLocked(keycardBox))
        {
            Require(wrongKeycard.parentInventory != null && correctKeycard.parentInventory == null,
                "card usage was incorrect on the authority");
            Record("PASS host native keycard", "wrong card retained; correct card consumed and real crate opened");
            done = true;
        }
    }

    private void TickKeycardAudioGuest(double now)
    {
        Require(now - started < 85, "guest keycard audio did not complete");
        var world = NativeWorld.Guest;
        if (world?.Ready != true) return;
        var stock = world.Objects.Values.OfType<GameItem>().ToArray();
        var box = stock.FirstOrDefault(item => GeneralHelper.GetCustomName(item) == KeycardBoxName);
        if (box == null) return;
        var wrong = stock.FirstOrDefault(item => GeneralHelper.GetCustomName(item) == WrongKeycardName);
        var correct = stock.FirstOrDefault(item => GeneralHelper.GetCustomName(item) == CorrectKeycardName);
        if (keycardStage == 2)
        {
            if (correct != null || NativeContainerAccess.IsSealed(box) || world.QaKeycardAccepted == 0) return;
            box.contentWindow.Show();
            Require(wrong != null && world.QaKeycardAccepted == 1 && world.QaKeycardRejected == 1 &&
                box.contentWindow.IsVisible() &&
                ((GameInventory)box.contentWindow.child).childItems.Count > 0,
                "guest did not get one open sound, one rejection and the unlocked contents");
            Finish("PASS guest native keycard audio");
            return;
        }
        if (keycardStage == 0)
            Require(world.QaKeycardRejected == 0, "the guest heard the host player's rejected card");
        else if (world.QaKeycardRejected == 0) return;
        Require(NativeContainerAccess.IsSealed(box) && wrong != null && correct != null,
            "a wrong card or projection unsealed the crate or consumed a card");
        var card = keycardStage == 0 ? wrong : correct;
        if (!keycardSelected)
        {
            box.contentWindow.Show();
            Require(!box.contentWindow.IsVisible(), "guest opened the sealed crate with no matching card");
            ItemSelectHandler.current.TrySelectItem(card);
            keycardSelected = true;
            return;
        }
        if (!card.MayTarget(box) || !card.CanTarget(box)) return;
        card.Target(box);
        ItemSelectHandler.current.EndSelect();
        keycardSelected = false;
        keycardStage++;
    }
}
#endif
