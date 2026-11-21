#if PROBABLY_COOP_GRID_QA
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Core;

namespace ProbablyCoop;

internal sealed partial class NativeBackgroundQa
{
    private readonly bool soloImportQa = Array.IndexOf(Environment.GetCommandLineArgs(), "--probably-coop-qa-solo-import") >= 0;
    private CoopRun.Checkpoint soloSource, soloCopy;
    private JObject soloHost;
    private bool soloPostSave;

    private void TickSoloImport(RoomSelector room, CoopPanel panel, bool inMenu)
    {
        if (host && room.IsHost)
        {
            if (inMenu && !loaded)
            {
                var args = Environment.GetCommandLineArgs();
                int index = Array.IndexOf(args, "--probably-coop-qa-solo-import");
                int sourceSlot = int.Parse(args[index + 1], System.Globalization.CultureInfo.InvariantCulture);
                int reload = Array.IndexOf(args, "--probably-coop-qa-verify-solo-copy");
                if (reload >= 0)
                {
                    int slot = int.Parse(args[reload + 1], System.Globalization.CultureInfo.InvariantCulture);
                    soloCopy = CoopRun.ListCheckpoints(onlySlot: slot).Single();
                    Require(CoopRun.PrepareResume(soloCopy), "converted save cannot resume after restart");
                    loaded = true;
                    MainMenuUIController.Instance.StartNewGameClick();
                    return;
                }
                var before = SoloQaSlots();
                soloSource = CoopRun.ListSoloCheckpoints().Single(value => value.Slot == sourceSlot);
                string path = SaveFiles.AbsolutePath(soloSource.File);
                using (var reader = ES3Reader.Create(NativeActorPersistence.ReadFileImage(path), new ES3Settings(path)))
                    soloHost = NativeActorCheckpoint.ReadReceiptEntries(NativeActorCheckpoint.ReadNativeEntries(reader), fromSolo: true).NativeHost;
                Require(!soloSource.HasCoopData && panel.QaSelectSoloImport(sourceSlot), "source missing from original menu");
                var confirm = MainMenuUIController.Instance.confirmPanelUIManager;
                confirm.OnNoClick();
                Require(before.SetEquals(SoloQaSlots()), "cancel wrote a native save");
                var expected = soloSource.ImageDigest;
                soloSource.ImageDigest = new byte[expected.Length];
                Require(CoopRun.ImportSolo(soloSource, out _) == null && before.SetEquals(SoloQaSlots()), "stale preview wrote a save");
                soloSource.ImageDigest = expected;
                Record("PASS solo-import-cancel", "native cancellation and stale selection leave all save files unchanged");
                Require(panel.QaSelectSoloImport(sourceSlot), "source lost after cancelling");
                confirm.OnYesClick();
                if (confirm.panel.activeSelf) confirm.OnYesClick();
                soloCopy = CoopRun.ListCheckpoints().Single(value => !value.Historical && !before.Contains(value.File));
                Require(soloCopy.Slot != soloSource.Slot && soloCopy.Run != soloSource.Run && soloCopy.HasActors &&
                    !soloCopy.NeedsRecovery && soloCopy.Day == soloSource.Day && soloCopy.Cash == soloSource.Cash,
                    "copy must add a different registered slot/run with the same shop state");
                var choices = CoopRun.ListSoloCheckpoints();
                Require(choices.Any(value => value.Slot == sourceSlot) && choices.All(value => value.Slot != soloCopy.Slot),
                    "solo list mixes the source and copy");
                AssertSoloSourceUnchanged();
                loaded = true;
                Record("PASS solo-import-created", $"slot={soloCopy.Slot} day={soloCopy.Day} cash={soloCopy.Cash}");
                return;
            }
            var actors = NativeActorSession.Current;
            if (inMenu || actors == null || actors.Hydrating || soloCopy == null || soloPostSave) return;
            Require(actors.Run.Run == soloCopy.Run && actors.NativeSaveSlot == soloCopy.Slot &&
                actors.Store.playerCash == soloCopy.Cash && StoreStation.Instance.dayCounter == soloCopy.Day,
                "native hydration did not restore the selected shop");
            if (soloSource == null)
            {
                Require(actors.VerifiedCheckpoint?.PartnerSetup != null && actors.Run.Partner.startingPerks.Any(perk => perk.id == "lucky"),
                    "independent partner configuration did not survive process restart");
                Finish("PASS solo-import-restart");
                return;
            }
            Require(JToken.DeepEquals(soloHost, actors.VerifiedCheckpoint?.Personal["actors"][0]), "host state changed in the initial copy");
            if (room.Session?.State != LinkState.Connected || NativeWorld.Host?.PartnerSetupRequiredForQa != false || !actors.CanCommit)
                return;
            Require(actors.Run.Partner.startingPerks.Any(perk => perk.id == "lucky"), "guest's original perk selection not applied");
            actors.Store.SaveGame();
            Require(actors.VerifiedCheckpoint?.PartnerSetup != null && CoopRun.VerifyCheckpoint(actors.Store, soloCopy.Day),
                "imported store did not persist its native cooperative checkpoint");
            AssertSoloSourceUnchanged();
            soloPostSave = true;
            Record("PASS solo-import-host", $"slot={soloCopy.Slot} original shop, independent guest setup and native save verified");
        }
        else if (!host && !inMenu && NativeWorld.Guest?.Ready == true && room.Session?.State == LinkState.Connected && !soloPostSave)
        {
            Require(room.Session.Shop.View.Active && EmporiumEntry.Instance != null &&
                StoreStation.Instance.dayCounter == room.Session.Shop.View.Day, "converted shop not visible on guest");
            soloPostSave = true;
            Record("PASS solo-import-guest", "original character picker completed and native shop entered");
        }
    }

    private static HashSet<string> SoloQaSlots() => new HashSet<string>(System.IO.Directory
        .EnumerateFiles(UnityEngine.Application.persistentDataPath, "save_*.es3").Select(Path.GetFileName), StringComparer.Ordinal);

    private void AssertSoloSourceUnchanged()
    {
        byte[] source = NativeActorPersistence.ReadFileImage(SaveFiles.AbsolutePath(soloSource.File));
        Require(CoopRun.Digest(source).AsSpan().SequenceEqual(soloSource.ImageDigest), "solo source changed during live co-op");
    }
}
#endif
