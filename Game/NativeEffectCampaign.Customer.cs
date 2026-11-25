using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ProbablyCoop.Net;
using ProbablyCoop.Sync;
using UnityEngine;

namespace ProbablyCoop;

internal sealed partial class NativeEffectCampaign
{
    private static readonly EntityKey CommerceKey = EntityKey.Scalar("shop.customer");
    private const int CommerceLimit = 160 * 1024;
    private static readonly FieldInfo EndPending = AccessTools.Field(typeof(DialogUIManager), "isEndPending");
    private static readonly FieldInfo AutoPending = AccessTools.Field(typeof(DialogUIManager), "isAutoAdvancePending");

    private StoreClientInstance observedCustomer, replicaCustomer;
    private Dialogue observedDialogue, replicaDialogue;
    private ulong customerSequence, lineSequence, motionSequence, bellSequence, quoteSequence;
    private ulong currentCustomer, currentLine, appliedMotion, appliedBell, appliedQuote;
    private ulong budgetDraft;
    private byte[] publishedCommerce, previousQuote;
    private JObject commerceView;
    private NativeWorld.CustomerMotion motion, appliedMotionPhase;
    private string motionSprite = "";
    private bool commerceDirty = true, quoteDirty, pendingStart, tradeLocked, applyingCommerce;
    private bool dialogueCanAdvance;
    private int readyChoices;
    private int shutterQueue;
    internal bool ShutterAvailable { get; private set; }
    private readonly Dictionary<GameItem, int> prices = new Dictionary<GameItem, int>();

    internal bool ApplyingCommerce => applyingCommerce;
    internal ulong CustomerNumber => currentCustomer;
    internal ulong QuoteNumber => appliedQuote;
    internal ulong DialogueNumber => currentLine;
    internal int QueuedCustomers => shutterQueue;
    internal bool ClientDialogue => currentLine != 0 || pendingStart;
    internal bool TryCounterPrice(GameItem item, out int price) => prices.TryGetValue(item, out price);

    internal void CommerceChanged() => commerceDirty = true;
    internal void StockChanged() { commerceDirty = true; quoteDirty = true; }
    internal void BellRang() { bellSequence++; CommerceChanged(); }

    private void RefreshStockForCommerce()
    {
        if (!quoteDirty) return;
        quoteDirty = false;
        if (PlayerStore.Instance.currentClientInstance == null || MapUIManager.Instance.IsOutside) return;
        PlayerStore.Instance.RefreshCounterItem();
    }

    internal void CustomerMotion(NativeWorld.CustomerMotion phase)
    {
        if (!host) return;
        motion = phase;
        motionSequence++;
        if (phase == NativeWorld.CustomerMotion.Approaching)
        {
            var client = PlayerStore.Instance.currentClientInstance?.GetClientBlueprint();
            var actual = StoreClientMono.Instance?.image.sprite;
            motionSprite = SpriteDict.Instance.spriteDictionary.FirstOrDefault(pair => pair.Value == actual).Key ??
                client?.spriteName ?? "";
        }
        CommerceChanged();
    }

    private void ObserveCommerce()
    {
        var store = PlayerStore.Instance;
        ObserveShot();
        ObserveSecurity();
        var ui = DialogUIManager.Instance;
        var customer = store.currentClientInstance;
        var line = ui.currentDialog;
        bool waiting = line != null && !ui.IsChoice() && !ui.IsLineStillDisplaying(line) &&
            (ui.isWaitingForManualAdvance || (bool)EndPending.GetValue(ui) || (bool)AutoPending.GetValue(ui));
        int choiceMask = 0;
        if (line != null)
            for (int i = 0; i < line.choices.Count && i < ui.dialogChoiceBubbles.Count && i < 16; i++)
                if (ui.dialogChoiceBubbles[i].gameObject.activeSelf && ui.dialogChoiceBubbles[i].IsInteractable())
                    choiceMask |= 1 << i;
        bool locked = NegociationUIManager.IsTradeLocked();
        bool starting = StoreUIManager.Instance.isAboutToStartMainDialog;
        int queued = store.storeClientManager.clientStack.Count;
        bool shutter = MapUIManager.Instance?.IsOutside == false && store.lockShutterButtonFlag <= 0 &&
            !store.isClientBeingArrested && !store.isHandlingShootout &&
            (store.storeState == PlayerStore.StoreState.MORNING && store.CanStartDay() ||
             store.storeState == PlayerStore.StoreState.OPEN && (store.CanEndDay() || store.devMode));
        if (customer != observedCustomer)
        {
            observedCustomer = customer;
            currentCustomer = customer == null ? 0 : ++customerSequence;
            commerceDirty = true;
        }
        if (line != observedDialogue)
        {
            observedDialogue = line;
            currentLine = line == null ? 0 : ++lineSequence;
            commerceDirty = true;
        }
        if (waiting != dialogueCanAdvance || locked != tradeLocked || starting != pendingStart || choiceMask != readyChoices ||
            queued != shutterQueue || shutter != ShutterAvailable)
            commerceDirty = true;
        dialogueCanAdvance = waiting;
        readyChoices = choiceMask;
        tradeLocked = locked;
        pendingStart = starting;
        shutterQueue = queued;
        ShutterAvailable = shutter;
    }

    private byte[] CaptureCommerce()
    {
        ObserveCommerce();
        var store = PlayerStore.Instance;
        var client = store.currentClientInstance?.GetClientBlueprint();
        var rows = new JArray();
        JObject bargain = null;
        using (NativeActorSession.Current.Enter(NativeActorRun.Slot.Partner))
        {
            if (client != null)
            {
                bargain = NativeWorld.CaptureBargain();
                foreach (GameItem item in EmporiumEntry.Instance.backInvinvElement.childItems.OrderBy(item => item.uniqueId))
                {
                    if (!inventory.Tracks(item)) continue;
                    int mask = (client.IsClientBuyingThisItem(item, false) ? 1 : 0) |
                        (client.IsClientBuyingThisItem(item, true) ? 2 : 0) |
                        (client.IsClientRefusingItem(item, false) ? 4 : 0) |
                        (client.IsClientRefusingItem(item, true) ? 8 : 0) |
                        (client.willAcceptItem?.Invoke(item) == true ? 16 : 0) |
                        (client.CanClientExposeAnyFeature(item) ? 32 : 0) |
                        (client.CanClientExposeAnyFeatureDeep(item) ? 64 : 0);
                    rows.Add(new JArray(item.uniqueId, item.unitCount, GeneralHelper.IsItemOwned(item),
                        checked((int)item.GetNegociatedValue()), mask));
                }
            }
        }
        var offer = new JObject
        {
            ["client"] = NativeWorld.CaptureClient(client), ["customer"] = currentCustomer,
            ["bargain"] = bargain, ["rows"] = rows, ["cash"] = store.playerCash,
            ["available"] = client != null && store.isClientArrived && !tradeLocked &&
                !pendingStart && observedDialogue?.isMainDialog != true && !client.isBusinessComplete,
            ["preference"] = client?.willAcceptItem != null,
            ["wholesale"] = client?.isOfferWholesale == true ? store.GetWholesalePrice() : 0,
        };
        if (client?.barterOffer != null)
            offer["barterGoods"] = new JArray(EmporiumEntry.Instance.frontInvinvElement.childItems
                .Where(item => !GeneralHelper.IsItemOwned(item)).OrderBy(item => item.uniqueId)
                .Select(item => new JArray(item.uniqueId, item.unitCount)));
        byte[] quote = Utf8.GetBytes(offer.ToString(Formatting.None));
        if (!quote.AsSpan().SequenceEqual(previousQuote))
        {
            previousQuote = quote;
            quoteSequence++;
        }
        var dialogue = observedDialogue;
        offer["revision"] = quoteSequence;
        offer["arrived"] = store.isClientArrived;
        offer["locked"] = tradeLocked;
        offer["bellLocked"] = store.lockNextBellFlag > 0;
        offer["shutterQueue"] = shutterQueue;
        offer["shutterAvailable"] = ShutterAvailable;
        offer["pending"] = pendingStart;
        offer["canBargain"] = store.isBargainIntroduced;
        offer["bell"] = bellSequence;
        offer["motion"] = (int)motion;
        offer["motionSequence"] = motionSequence;
        offer["sprite"] = motionSprite;
        offer["line"] = currentLine;
        offer["canAdvance"] = dialogueCanAdvance;
        offer["title"] = dialogue?.title ?? "";
        offer["text"] = dialogue?.plainText ?? "";
        offer["main"] = dialogue?.isMainDialog == true;
        var choices = new JArray();
        if (dialogue != null)
            foreach (var choice in dialogue.choices)
                choices.Add(choice.Item2?.plainText ?? "");
        offer["choices"] = choices;
        offer["readyChoices"] = readyChoices;
        offer["shooting"] = CaptureShot();
        offer["security"] = CaptureSecurity();
        byte[] encoded = Utf8.GetBytes(offer.ToString(Formatting.None));
        if (encoded.Length > CommerceLimit) throw new InvalidDataException("The native customer state is too large.");
        commerceDirty = false;
        return encoded;
    }

    private void AppendCommerce(List<SyncOp> ops)
    {
        ObserveCommerce();
        if (!commerceDirty) return;
        var data = CaptureCommerce();
        if (data.AsSpan().SequenceEqual(publishedCommerce)) return;
        publishedCommerce = data;
        ops.Add(SyncOp.Put(CommerceKey, data));
    }

    private void PublishCommerce()
    {
        if (Session.Host?.Closed != false || !Session.Host.IsAttached(NetSession.GuestPeer)) return;
        ObserveCommerce();
        if (!commerceDirty) return;
        var ops = new List<SyncOp>();
        AppendCommerce(ops);
        if (ops.Count > 0) Session.Host.Local(ops);
    }

    private static bool TryReadCommerce(byte[] data, out JObject result)
    {
        result = null;
        if (data == null || data.Length == 0 || data.Length > CommerceLimit) return false;
        try
        {
            var value = JObject.Parse(Utf8.GetString(data), new JsonLoadSettings
                { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if (value["rows"] is not JArray rows || rows.Count > 768 ||
                value["client"]?.Type is not (JTokenType.Object or JTokenType.Null) ||
                value["bargain"]?.Type is not (JTokenType.Object or JTokenType.Null)) return false;
            foreach (string key in new[] { "arrived", "locked", "bellLocked", "pending", "available",
                "canBargain", "preference", "canAdvance", "main", "shutterAvailable" })
                if (value[key]?.Type != JTokenType.Boolean) return false;
            foreach (string key in new[] { "customer", "revision", "bell", "motionSequence", "line" })
                if (value[key]?.Type != JTokenType.Integer || !ulong.TryParse(value[key].ToString(), out _)) return false;
            foreach (string key in new[] { "motion", "readyChoices", "cash", "wholesale", "shutterQueue" })
                if (value[key]?.Type != JTokenType.Integer || !int.TryParse(value[key].ToString(), out _)) return false;
            if ((int)value["motion"] is < 0 or > (int)NativeWorld.CustomerMotion.LeavingRight ||
                (int)value["shutterQueue"] < 0 ||
                value["choices"] is not JArray choices || choices.Count > 16 ||
                choices.Any(choice => choice.Type != JTokenType.String || ((string)choice).Length > 2048) ||
                (int)value["readyChoices"] < 0 || (int)value["readyChoices"] >= 1 << choices.Count ||
                ((ulong)value["customer"] == 0) != (value["client"].Type == JTokenType.Null)) return false;
            if (!ReadText(value, "sprite", 160) || !ReadText(value, "title", 512) || !ReadText(value, "text", 16000))
                return false;
            if (!ValidShot(value["shooting"] as JObject) || !ValidSecurity(value["security"] as JObject)) return false;
            int previous = 0;
            foreach (var row in rows)
            {
                if (row is not JArray entry || entry.Count != 5 || entry[2].Type != JTokenType.Boolean ||
                    entry.Where((_, index) => index != 2).Any(token => token.Type != JTokenType.Integer ||
                        !int.TryParse(token.ToString(), out _))) return false;
                int id = (int)entry[0];
                if (id <= previous || (int)entry[1] is < 1 or > InventoryEntry.MaxUnits ||
                    (int)entry[4] is < 0 or > 127) return false;
                previous = id;
            }
            result = value;
            return true;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or OverflowException) { return false; }
    }

    private static bool ReadText(JObject value, string key, int limit) =>
        value[key]?.Type == JTokenType.String && ((string)value[key]).Length <= limit;

    private void ApplyCommerce(JObject value, bool restoring)
    {
        applyingCommerce = true;
        try
        {
            var store = PlayerStore.Instance;
            ulong id = (ulong)value["customer"];
            if (id != currentCustomer)
            {
                ReleaseCustomer();
                if (id != 0)
                {
                    var data = (JObject)value["client"];
                    var client = NativeWorld.RestoreClient(data);
                    replicaCustomer = StoreClientInstance.CreateClientInstance(client);
                }
                currentCustomer = id;
                budgetDraft = 0;
                if (id == 0) NegociationUIManager.Instance.CloseUI();
            }
            else if (replicaCustomer != null && !JToken.DeepEquals(commerceView?["client"], value["client"]))
                NativeWorld.RestoreClient((JObject)value["client"], existing: replicaCustomer.GetClientBlueprint());
            store.currentClientInstance = replicaCustomer;
            store.isClientArrived = (bool)value["arrived"];
            store.lockNextBellFlag = (bool)value["bellLocked"] ? 1 : 0;
            shutterQueue = (int)value["shutterQueue"];
            ShutterAvailable = (bool)value["shutterAvailable"];
            store.isBargainIntroduced = (bool)value["canBargain"];
            StoreUIManager.Instance.isAboutToStartMainDialog = pendingStart = (bool)value["pending"];
            commerceView = value;
            appliedQuote = (ulong)value["revision"];
            tradeLocked = (bool)value["locked"];
            prices.Clear();
            var masks = new Dictionary<GameItem, byte>();
            foreach (JArray row in (JArray)value["rows"])
            {
                // A newer local placement may already be in flight. Keep its
                // native item, but only quote objects with the confirmed row.
                if (!inventory.TryFindAccessible((int)row[0], NetSession.GuestPeer, out var item) ||
                    item.unitCount != (int)row[1] || GeneralHelper.IsItemOwned(item) != (bool)row[2]) continue;
                prices[item] = (int)row[3];
                masks[item] = (byte)row[4];
            }
            if (replicaCustomer != null)
            {
                NativeClientItemQueries.Bind(replicaCustomer.GetClientBlueprint(), masks, (bool)value["preference"]);
                if (value["bargain"] is JObject bargain) NativeWorld.ApplyBargain(bargain);
            }
            ApplyCustomerMotion(value, restoring);
            ApplyDialogue(value, restoring);
            ApplyShot((JObject)value["shooting"], restoring);
            ApplySecurity((JObject)value["security"], restoring);
            NegociationUIManager.Instance.RefreshUI();
        }
        finally { applyingCommerce = false; }
    }

    private void ApplyCustomerMotion(JObject value, bool restoring)
    {
        ulong bell = (ulong)value["bell"];
        if (!restoring && bell > appliedBell)
            UnityEngine.Object.FindFirstObjectByType<StoreBell>()?.audioSource?.Play();
        appliedBell = bell;
        ulong sequence = (ulong)value["motionSequence"];
        var phase = (NativeWorld.CustomerMotion)(int)value["motion"];
        if (!restoring && sequence == appliedMotion) return;
        var actor = StoreClientMono.Instance;
        if (actor == null) return;
        bool finishingWalk = appliedMotionPhase == NativeWorld.CustomerMotion.Approaching;
        bool leaving = appliedMotionPhase is NativeWorld.CustomerMotion.Leaving or NativeWorld.CustomerMotion.LeavingRight;
        appliedMotion = sequence;
        appliedMotionPhase = phase;
        if (phase == NativeWorld.CustomerMotion.Hidden)
        {
            if (restoring || !leaving || !actor.IsMoving()) StoreUIManager.Instance.HideClient();
            return;
        }
        if (SpriteDict.Instance.spriteDictionary.TryGetValue((string)value["sprite"], out var sprite))
            actor.image.sprite = sprite;
        StoreUIManager.Instance.DisplayClient();
        switch (phase)
        {
            case NativeWorld.CustomerMotion.Approaching: actor.StartApprochingStore(); break;
            case NativeWorld.CustomerMotion.AtCounter:
                if (restoring || !finishingWalk) actor.SetImmediatlyArrive();
                break;
            case NativeWorld.CustomerMotion.Leaving: actor.StartLeavingStore(); break;
            case NativeWorld.CustomerMotion.LeavingRight: actor.StartLeavingStoreRight(); break;
        }
    }

    private void ApplyDialogue(JObject value, bool restoring)
    {
        ulong line = (ulong)value["line"];
        readyChoices = (int)value["readyChoices"];
        if (!restoring && currentLine == line) return;
        currentLine = line;
        var ui = DialogUIManager.Instance;
        ui.InterruptDialog();
        replicaDialogue = null;
        if (line == 0) return;
        var dialogue = new Dialogue { isMainDialog = (bool)value["main"] };
        dialogue.SetText((string)value["title"], (string)value["text"]);
        var choices = (JArray)value["choices"];
        for (int index = 0; index < choices.Count && index < ui.dialogChoiceBubbles.Count; index++)
        {
            int mask = 1 << index;
            var option = new Dialogue().SetText("PLAYER", (string)choices[index]);
            option.isDisabledFunc = _ => (readyChoices & mask) == 0;
            dialogue.choices.Add((null, option, null));
        }
        replicaDialogue = dialogue;
        ui.StartDialogue(dialogue, forcedStart: true);
    }

    private void ReleaseCustomer()
    {
        if (replicaCustomer == null) return;
        if (PlayerStore.HasInstance && PlayerStore.Instance.currentClientInstance == replicaCustomer)
            PlayerStore.Instance.currentClientInstance = null;
        var view = NativeWorld.CustomerInventoryView(replicaCustomer);
        if (view != null) UnityEngine.Object.Destroy(view);
        replicaCustomer = null;
    }
}
