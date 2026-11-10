using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;

namespace ProbablyCoop;

// A real menu-view probe for the disposable guest only. The returned draft is
// submitted by NativePartnerJoin through the ordinary authenticated wire path.
internal static class NativePartnerSetupQa
{
    private static readonly FieldInfo Frame = AccessTools.Field(typeof(NewGamePerksUI), "frame");
    private static readonly FieldInfo StoreInstance = AccessTools.Field(typeof(PlayerStore), "instance");
    private static NewGameData data;
    private static List<StartingPerk> original;
    private static StartingPerk[] originalItems;
    private static object originalStore;
    private static string originalFields;
    private static int phase, frameAt, sceneHandle, thread;
    private static float began;
    private static bool watching, cancelled;
    private static string violation;
    private static string[] accepted;
    private static NativeActorPartnerSetup.DraftHandle pickerOwner;
    internal static string[] Evidence { get; private set; } = Array.Empty<string>();

    // False while the original Canvas has not yet had a frame to render. True
    // only after the native slot and Continue button paths restored menu data.
    internal static bool Tick(out string[] draft)
    {
        draft = null;
        if (!NativeBackgroundQa.Enabled || !NativeQaDisplay.IsIsolatedQa || !NativeBackgroundQa.IsGuestProduct)
            throw new InvalidOperationException("Partner picker QA requires the isolated QA guest.");
        if (phase == 0) Open();
        CheckThread();
        Require(violation == null, "Forbidden native side effect: " + violation);
        Require(!cancelled, "The QA partner picker was cancelled.");
        Require(ReferenceEquals(NewGameData.Instance, data) && data != null && data.isInMainMenu,
            "The picker left its original main-menu data instance.");
        Require(SceneManager.GetActiveScene().handle == sceneHandle, "The picker changed the native scene.");
        if (phase == 3) { draft = (string[])accepted.Clone(); return true; }
        Require(Time.realtimeSinceStartup - began < 20f, "The native partner picker timed out.");
        if (Time.frameCount - frameAt < 2) return false;
        Require(NewGamePerksUI.IsOpen && Frame.GetValue(null) is NewGamePerksFrame, "Native partner picker unexpectedly closed.");
        var frame = (NewGamePerksFrame)Frame.GetValue(null);
        Require(frame.isActiveAndEnabled && frame.gameObject.activeInHierarchy, "Native perk frame is not visible.");
        if (phase == 1)
        {
            Require(EventSystem.current != null, "Native menu EventSystem is missing.");
            var slots = frame.availableSlots.GetComponentsInChildren<WildNetworkSlot>(false);
            Require(slots.Length == NativeActorPartnerSetup.Catalog().Count, "Original picker candidate slots do not match the supported personal catalog.");
            var lucky = slots.SingleOrDefault(slot => slot.id == "lucky");
            Require(lucky != null && lucky.isActiveAndEnabled && lucky.onClick != null, "Native lucky perk slot is not interactive.");
            NativeOffscreenCapture.Capture("qa-partner-perks-native-open");
            lucky.OnPointerClick(new PointerEventData(EventSystem.current) { button = PointerEventData.InputButton.Left });
            Require(data.startingPerks.Count == 1 && data.startingPerks[0].id == "lucky", "Original slot click did not update the draft.");
            phase = 2; frameAt = Time.frameCount;
            return false;
        }
        Require(frame.selectedSlots.GetComponentsInChildren<WildNetworkSlot>(false).Any(slot => slot.id == "lucky"),
            "Original selected-perk row did not render lucky.");
        Require(frame.continueButton != null && frame.continueButton.isActiveAndEnabled && frame.continueButton.interactable,
            "Original Continue button is unavailable.");
        NativeOffscreenCapture.Capture("qa-partner-perks-native-selected");
        frame.continueButton.onClick.Invoke();
        Require(violation == null, "Forbidden native side effect: " + violation);
        Require(accepted != null && accepted.SequenceEqual(new[] { "lucky" }), "Original Continue did not produce the personal wire draft.");
        Require(!NewGamePerksUI.IsOpen, "Original picker did not close after accepting the draft.");
        VerifyRestored();
        watching = false; phase = 3;
        Evidence = new[] {
            "original NewGamePerksUI Canvas rendered across frames with the personal catalog",
            "original WildNetworkSlot.OnPointerClick selected lucky; original Continue button produced draft [lucky]",
            "NewGameData instance, scalar fields, original perk list and entries restored; PlayerStore singleton and scene unchanged",
            "no native OnPerksContinue, new-game, InitialSave, SaveGame or WriteSlotFile entry was invoked during picker activity"
        };
        draft = (string[])accepted.Clone();
        return true;
    }

    private static void Open()
    {
        Require(SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null,
            "Native picker visual QA requires a graphics device.");
        Require(!NewGamePerksUI.IsOpen, "Another native perk window is already open.");
        thread = Environment.CurrentManagedThreadId;
        data = NewGameData.Instance;
        Require(data != null && data.isInMainMenu, "Partner picker QA must begin in the original main menu.");
        original = data.startingPerks;
        Require(original != null, "Original menu perk list is missing.");
        originalItems = original.ToArray(); originalFields = Fields(data);
        originalStore = StoreInstance.GetValue(null); sceneHandle = SceneManager.GetActiveScene().handle;
        began = Time.realtimeSinceStartup; watching = true;
        try
        {
            if (!NativeActorPartnerSetup.TryOpenDraft(ids => accepted = ids, () => cancelled = true, out string reason, out pickerOwner))
                throw new InvalidOperationException("Native partner picker failed to open: " + reason);
            Require(!ReferenceEquals(original, data.startingPerks), "Partner draft was not isolated from the original menu loadout.");
            phase = 1; frameAt = Time.frameCount;
        }
        catch { Reset(); throw; }
    }

    private static string Fields(NewGameData value) => new JObject {
        ["startType"] = (int)value.startType, ["startScenarioId"] = value.startScenarioId,
        ["skipIntro"] = value.skipIntro, ["endlessMode"] = value.endlessMode, ["difficultyId"] = value.difficultyId,
        ["hardMode"] = value.hardMode, ["disableTutorialHint"] = value.disableTutorialHint,
        ["saveSlot"] = value.saveSlot, ["saveFile"] = value.saveFile, ["isInMainMenu"] = value.isInMainMenu,
        ["isShowcase"] = value.isShowcase, ["isLoadFromMainMenu"] = value.isLoadFromMainMenu
    }.ToString(Newtonsoft.Json.Formatting.None);

    private static void VerifyRestored()
    {
        Require(ReferenceEquals(data, NewGameData.Instance) && ReferenceEquals(data.startingPerks, original), "Native menu loadout reference was not restored.");
        Require(original.Count == originalItems.Length && original.Select((value, index) => ReferenceEquals(value, originalItems[index])).All(value => value),
            "Original menu perk entries were changed.");
        Require(Fields(data) == originalFields, "Native new-game configuration was changed by partner selection.");
        Require(ReferenceEquals(StoreInstance.GetValue(null), originalStore), "Native PlayerStore singleton was created or replaced by picker activity.");
        Require(SceneManager.GetActiveScene().handle == sceneHandle, "Partner selection entered a gameplay scene.");
    }

    internal static void Reset()
    {
        if (phase != 0 || watching) CheckThread();
        try
        {
            NativeActorPartnerSetup.CancelDraft(pickerOwner);
            if (watching && data != null) VerifyRestored();
        }
        finally
        {
            watching = cancelled = false; phase = frameAt = thread = sceneHandle = 0;
            data = null; original = null; originalItems = null; originalStore = null;
            originalFields = violation = null; accepted = null; pickerOwner = null; Evidence = Array.Empty<string>();
        }
    }

    internal static void RejectSideEffect(MethodBase method)
    {
        if (!watching) return;
        violation = method.DeclaringType.Name + "." + method.Name;
        throw new InvalidOperationException("Native partner picker attempted a forbidden side effect: " + violation);
    }
    private static void CheckThread()
    { if (thread != Environment.CurrentManagedThreadId) throw new InvalidOperationException("Partner picker QA crossed the game thread."); }
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}

[HarmonyPatch]
internal static class NativePartnerSetupQaSideEffectGuard
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(MainMenuUIController), nameof(MainMenuUIController.OnPerksContinue));
        yield return AccessTools.Method(typeof(MainMenuUIController), nameof(MainMenuUIController.StartNewGameClick));
        yield return AccessTools.Method(typeof(PlayerStore), nameof(PlayerStore.StartNewGame));
        yield return AccessTools.Method(typeof(PlayerStore), nameof(PlayerStore.InitialSave));
        yield return AccessTools.Method(typeof(PlayerStore), nameof(PlayerStore.SaveGame));
        yield return AccessTools.Method(typeof(PlayerStore), "WriteSlotFile");
    }
    [HarmonyPriority(Priority.First)]
    private static void Prefix(MethodBase __originalMethod) => NativePartnerSetupQa.RejectSideEffect(__originalMethod);
}
