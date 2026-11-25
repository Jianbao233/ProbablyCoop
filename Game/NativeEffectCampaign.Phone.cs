using System;
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
    private enum PhonePhase : byte { Ready, Ringing, Connecting, Conversation, Unanswered }
    private static readonly EntityKey PhoneKey = EntityKey.Scalar("shop.phone");
    private const int PhoneLimit = 96 * 1024;
    private const byte PhoneHost = 2;
    private static readonly FieldInfo DialNumberField = AccessTools.Field(typeof(PhoneUIManager), "currentNumber");
    private static readonly FieldInfo DialTimerField = AccessTools.Field(typeof(PhoneUIManager), "clickTimer");
    private static readonly FieldInfo PhoneSequence = AccessTools.Field(typeof(PhoneUIManager), "sequenceTimer");
    private static readonly FieldInfo PhoneRinging = AccessTools.Field(typeof(PhoneUIManager), "isCalling");
    private static readonly FieldInfo PhoneTalking = AccessTools.Field(typeof(PhoneUIManager), "isTalking");
    private static readonly FieldInfo PhoneEndPending = AccessTools.Field(typeof(DialoguePhoneUIManager), "isEndPending");
    private static readonly FieldInfo PhoneAutoPending = AccessTools.Field(typeof(DialoguePhoneUIManager), "isAutoAdvancePending");
    internal static long DialNumber(PhoneUIManager phone) => (long)DialNumberField.GetValue(phone);
    private static float DialTimer(PhoneUIManager phone) => (float)DialTimerField.GetValue(phone);
    private byte[] publishedPhone;
    private JObject replicaPhone;
    private byte phoneOwner;
    private ulong callSequence, phoneLine, visiblePhoneLine;
    private Dialogue displayedPhoneDialogue;
    private bool phoneDirty = true, sourceCanAdvance, sourcePhoneChoices;
    private int sourcePhoneChoiceMask;
    private PhonePhase sourcePhonePhase;
    private bool queuedDial, queuedHangup;
    private ulong lastHangupProof;
    private double dialTimeout;
    private CanvasGroup hiddenPhone;
    private (float alpha, bool interactable, bool raycasts) hiddenPhoneOriginal;
    private bool createdPhoneGroup;
    private (AudioSource source, bool mute)[] mutedPhoneSources;
    private sealed class PhoneEnd { internal ulong Generation; }
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Dialogue, PhoneEnd> phoneEnds = new();
    private ulong phoneEndGeneration;
    internal bool CancellingPhone { get; private set; }

    internal bool RemotePhoneExecution => host && phoneOwner == NetSession.GuestPeer;
    internal bool HostPhoneHidden => host && hiddenPhone != null;
    internal bool HasPhoneView => PhoneUIManager.Instance?.phonePanel.activeInHierarchy == true &&
        (host ? phoneOwner == PhoneHost && hiddenPhone == null : (byte?)replicaPhone?["owner"] == NetSession.GuestPeer);
    internal bool ReplicaPhoneBusy => (byte?)replicaPhone?["owner"] != 0 || queuedDial;

    private static PhonePhase CurrentPhonePhase(PhoneUIManager phone)
    {
        if ((bool)PhoneTalking.GetValue(phone)) return PhonePhase.Conversation;
        if ((bool)PhoneRinging.GetValue(phone)) return PhonePhase.Ringing;
        if ((float)PhoneSequence.GetValue(phone) > 0) return PhonePhase.Connecting;
        if (phone.statusDisplay.text == LocHelper.GetLocalizedUI("ui_phone_no_answer")) return PhonePhase.Unanswered;
        return PhonePhase.Ready;
    }

    internal void ObserveLocalDial()
    {
        if (RemotePhoneExecution) return;
        if (phoneOwner != PhoneHost || sourcePhonePhase is PhonePhase.Ready or PhonePhase.Unanswered)
            callSequence++;
        phoneOwner = PhoneHost;
        phoneDirty = true;
    }

    internal void MarkPhoneDirty() => phoneDirty = true;

    internal void TrackPhoneEnd(Dialogue dialogue)
    {
        if (dialogue?.endAction == null) return;
        phoneEnds.Remove(dialogue);
        phoneEnds.Add(dialogue, new PhoneEnd { Generation = phoneEndGeneration });
    }

    internal void InterruptPhoneEnds() => phoneEndGeneration++;

    internal bool AllowPhoneEnd(Dialogue dialogue) =>
        !phoneEnds.TryGetValue(dialogue, out var end) || end.Generation == phoneEndGeneration;

    internal void ObservePhoneLine(Dialogue line)
    {
        displayedPhoneDialogue = line;
        phoneLine++;
        phoneDirty = true;
    }

    private void ObservePhone()
    {
        var phone = PhoneUIManager.Instance;
        var ui = DialoguePhoneUIManager.Instance;
        var phase = CurrentPhonePhase(phone);
        if (phase is PhonePhase.Ready or PhonePhase.Unanswered && DialTimer(phone) <= 0)
        {
            if (phoneOwner != 0)
            {
                phoneOwner = 0;
                phoneDirty = true;
                RestoreHostPhone();
            }
        }
        var dialog = ui.currentDialog;
        bool displayed = dialog != null && ReferenceEquals(dialog, displayedPhoneDialogue);
        if (dialog == null && displayedPhoneDialogue != null)
        {
            displayedPhoneDialogue = null;
            phoneDirty = true;
        }
        int mask = 0;
        bool choiceView = false;
        if (displayed)
            for (int i = 0; i < dialog.choices.Count && i < ui.dialogChoiceBubbles.Count && i < 16; i++)
            {
                var choice = ui.dialogChoiceBubbles[i];
                if (!choice.gameObject.activeSelf) continue;
                choiceView = true;
                if (choice.IsInteractable()) mask |= 1 << i;
            }
        bool advance = displayed && dialog.choices.Count == 0 &&
            (ui.isWaitingForManualAdvance || (bool)PhoneEndPending.GetValue(ui) || (bool)PhoneAutoPending.GetValue(ui));
        if (phase != sourcePhonePhase || advance != sourceCanAdvance || mask != sourcePhoneChoiceMask ||
            choiceView != sourcePhoneChoices)
            phoneDirty = true;
        sourcePhonePhase = phase;
        sourceCanAdvance = advance;
        sourcePhoneChoiceMask = mask;
        sourcePhoneChoices = choiceView;
    }

    private byte[] CapturePhone()
    {
        ObservePhone();
        var phone = PhoneUIManager.Instance;
        var ui = DialoguePhoneUIManager.Instance;
        var line = displayedPhoneDialogue;
        var facts = new JObject
        {
            ["call"] = callSequence, ["owner"] = phoneOwner, ["phase"] = (byte)sourcePhonePhase,
            ["number"] = phoneOwner == 0 ? 0 : DialNumber(phone),
            ["line"] = line == null ? 0 : phoneLine, ["text"] = line?.plainText ?? "",
            ["player"] = line?.title == "PLAYER", ["advance"] = sourceCanAdvance,
            ["name"] = phone.currentPhoneClient?.locID ?? "", ["mask"] = sourcePhoneChoiceMask,
            ["choices"] = sourcePhoneChoices ? new JArray(ui.dialogChoiceBubbles.Take(16)
                .Select(bubble => bubble.gameObject.activeSelf ? (bubble.TMP.text ?? "") : null)) : new JArray(),
            ["contacts"] = JObject.FromObject(PlayerStore.Instance.PhoneClientDict),
        };
        var bytes = Utf8.GetBytes(facts.ToString(Formatting.None));
        if (bytes.Length > PhoneLimit) throw new System.IO.InvalidDataException("Shared phone state exceeds the effect limit.");
        phoneDirty = false;
        return bytes;
    }

    private static bool TryReadPhone(byte[] bytes, out JObject facts, out System.Collections.Generic.Dictionary<long, StorePhoneClient> contacts)
    {
        facts = null;
        contacts = null;
        if (bytes == null || bytes.Length == 0 || bytes.Length > PhoneLimit) return false;
        try
        {
            var view = JObject.Parse(Utf8.GetString(bytes));
            if (!Integer(view["owner"], 0, PhoneHost) || !Integer(view["phase"], 0, (int)PhonePhase.Unanswered) ||
                !Integer(view["number"], 0, 999999999) || !Integer(view["mask"], 0, 65535) ||
                view["call"]?.Type != JTokenType.Integer || (ulong)view["call"] > long.MaxValue ||
                view["line"]?.Type != JTokenType.Integer || (ulong)view["line"] > long.MaxValue ||
                !ReadText(view, "text", 32768) || !ReadText(view, "name", 256) ||
                view["player"]?.Type != JTokenType.Boolean || view["advance"]?.Type != JTokenType.Boolean ||
                view["choices"] is not JArray choices || choices.Count > 16 ||
                choices.Any(choice => choice.Type != JTokenType.Null &&
                    (choice.Type != JTokenType.String || ((string)choice).Length > 4096)) ||
                view["contacts"] is not JObject book || book.Count > 192) return false;
            var decoded = book.ToObject<System.Collections.Generic.Dictionary<long, StorePhoneClient>>();
            if (decoded == null || decoded.Any(pair => pair.Key < 1 || pair.Key > 999999999 ||
                pair.Value == null || !Enum.IsDefined(typeof(StorePhoneClient.PhoneState), pair.Value.phoneState) ||
                pair.Value.alias == null || pair.Value.alias.Count > 128)) return false;
            facts = view;
            contacts = decoded;
            return true;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or OverflowException or InvalidCastException)
        {
            return false;
        }
    }

    private void RunGuestPhone(ActivityIntent intent, NativeActorSession actors)
    {
        if (MapUIManager.Instance.IsOutside || intent.Proof != ActivityProof(publishedPhone)) return;
        var phone = PhoneUIManager.Instance;
        var ui = DialoguePhoneUIManager.Instance;
        var active = ui.currentDialog;
        if (intent.Action is ActivityAction.Dial or ActivityAction.QuickDial)
        {
            if (phoneOwner != 0 || DialTimer(phone) > 0 || CurrentPhonePhase(phone) is
                PhonePhase.Ringing or PhonePhase.Connecting or PhonePhase.Conversation) return;
        }
        else if (!RemotePhoneExecution) return;
        if (!BeginAuthorityAction()) return;
        bool completed = false;
        try
        {
            using (actors.Enter(NativeActorRun.Slot.Partner))
            {
                switch (intent.Action)
                {
                    case ActivityAction.Dial:
                    case ActivityAction.QuickDial:
                        phoneOwner = NetSession.GuestPeer;
                        callSequence++;
                        HideHostPhone(phone);
                        if (intent.Action == ActivityAction.QuickDial) phone.AutoCall(intent.Value);
                        else
                        {
                            DialNumberField.SetValue(phone, intent.Value);
                            phone.StartCalling();
                        }
                        break;
                    case ActivityAction.HangUp:
                        phone.StopCall();
                        break;
                    case ActivityAction.PhoneAdvance:
                        if (active != null && ReferenceEquals(active, displayedPhoneDialogue) && sourceCanAdvance)
                            ui.OnAdvancePressed();
                        break;
                    case ActivityAction.PhoneChoice:
                        int index = (int)intent.Value;
                        if (active != null && ReferenceEquals(active, displayedPhoneDialogue) && index < active.choices.Count &&
                            index < ui.dialogChoiceBubbles.Count && ui.dialogChoiceBubbles[index].gameObject.activeSelf &&
                            active.IsChoiceEnabledAt(index))
                            ui.SelectChoice(index);
                        break;
                }
            }
            completed = true;
        }
        finally { EndAuthorityAction(completed); }
    }

    internal void RequestDial(bool quick, long number)
    {
        if (queuedDial || (byte?)replicaPhone?["owner"] != 0) return;
        queuedDial = RequestActivity(quick ? ActivityAction.QuickDial : ActivityAction.Dial, value: number);
        if (queuedDial) dialTimeout = Time.realtimeSinceStartupAsDouble + 2;
    }

    internal void RequestHangup()
    {
        if (!presentingActivity && (queuedDial || (byte?)replicaPhone?["owner"] == NetSession.GuestPeer))
        {
            if (!queuedHangup) lastHangupProof = 0;
            queuedHangup = true;
        }
    }

    private void UpdateReplicaPhone()
    {
        if (queuedDial && Time.realtimeSinceStartupAsDouble > dialTimeout)
        {
            queuedDial = queuedHangup = false;
            if ((byte?)replicaPhone?["owner"] == 0)
                PhoneUIManager.Instance.statusDisplay.text = LocHelper.GetLocalizedUI("ui_phone_ready");
        }
        ulong proof = ActivityProof(publishedPhone);
        if (queuedHangup && (byte?)replicaPhone?["owner"] == NetSession.GuestPeer && lastHangupProof != proof &&
            RequestActivity(ActivityAction.HangUp)) lastHangupProof = proof;
    }

    private void ApplyPhone(byte[] bytes, JObject facts, System.Collections.Generic.Dictionary<long, StorePhoneClient> contacts)
    {
        var phone = PhoneUIManager.Instance;
        var oldBook = replicaPhone?["contacts"];
        var oldPhase = (PhonePhase)((byte?)replicaPhone?["phase"] ?? 0);
        byte oldOwner = (byte?)replicaPhone?["owner"] ?? 0;
        publishedPhone = bytes;
        replicaPhone = facts;
        var store = PlayerStore.Instance;
        if (!JToken.DeepEquals(oldBook, facts["contacts"]))
        {
            store.PhoneClientDict = contacts;
            if (phone.phonePanel.activeSelf && phone.IsPhoneBookShown()) phone.phoneBookUIManager.OpenUI();
        }
        byte owner = (byte)facts["owner"];
        if (owner != 0) queuedDial = false;
        else if (oldOwner != 0)
        {
            queuedDial = queuedHangup = false;
            DialNumberField.SetValue(phone, 0L);
        }
        if (!phone.phonePanel.activeSelf) return;
        var phase = (PhonePhase)(byte)facts["phase"];
        if (owner != 0 || oldOwner != 0)
        {
            DialTimerField.SetValue(phone, -1f);
            DialNumberField.SetValue(phone, owner == NetSession.GuestPeer ? (long)facts["number"] : 0L);
        }
        if (owner != 0 || DialTimer(phone) <= 0)
            phone.statusDisplay.text = LocHelper.GetLocalizedUI(phase switch
            {
                PhonePhase.Ringing => "ui_phone_calling",
                PhonePhase.Connecting or PhonePhase.Conversation => "ui_phone_in_call",
                PhonePhase.Unanswered => "ui_phone_no_answer",
                _ => "ui_phone_ready",
            });
        if (oldPhase != phase && (owner == NetSession.GuestPeer || oldOwner == NetSession.GuestPeer))
        {
            if (phase == PhonePhase.Ringing) phone.phoneCalling.Play();
            else phone.phoneCalling.Stop();
            if (phase == PhonePhase.Unanswered) phone.phoneNoNumber.Play();
            if (phase == PhonePhase.Connecting || oldPhase == PhonePhase.Ready && phase == PhonePhase.Conversation)
                phone.phoneClientPickup.Play();
            if (oldPhase == PhonePhase.Conversation && phase == PhonePhase.Ready) phone.phoneClientDrop.Play();
        }
        RenderPhoneLine(facts, owner);
    }

    internal void ReopenReplicaPhone()
    {
        if (replicaPhone == null) return;
        visiblePhoneLine = 0;
        ApplyPhone(publishedPhone, replicaPhone, PlayerStore.Instance.PhoneClientDict);
    }

    private void RenderPhoneLine(JObject facts, byte owner)
    {
        var ui = DialoguePhoneUIManager.Instance;
        ulong line = owner == NetSession.GuestPeer ? (ulong)facts["line"] : 0;
        if (line != visiblePhoneLine)
        {
            ui.InterruptDialog();
            visiblePhoneLine = line;
            if (line != 0)
            {
                bool player = (bool)facts["player"];
                var bubble = player ? ui.playerBubble : ui.clientBubble;
                bubble.gameObject.SetActive(true);
                if (!player)
                {
                    string name = (string)facts["name"];
                    ui.clientNameBubble.SetActive(name.Length != 0);
                    ui.clientNameTMP.text = name.Length == 0 ? "" : "<i>" + LocHelper.GetLocalizedClientName(name) + "</i>";
                    ui.SetClientBubbleColor();
                }
                bubble.typewriter.hideAppearancesOnSkip = false;
                bubble.typewriter.ShowText((string)facts["text"]);
                if (!EmporiumOption.Instance.enableTypewriterEffect) bubble.typewriter.SkipTypewriter();
            }
        }
        var choices = (JArray)facts["choices"];
        for (int i = 0; i < ui.dialogChoiceBubbles.Count; i++)
        {
            var choice = ui.dialogChoiceBubbles[i];
            bool visible = line != 0 && i < choices.Count && choices[i].Type == JTokenType.String;
            choice.gameObject.SetActive(visible);
            if (!visible) continue;
            choice.TMP.text = (string)choices[i];
            choice.SetInteractable(((int)facts["mask"] & (1 << i)) != 0);
        }
    }

    internal void AdvanceReplicaPhone()
    {
        if (!HasPhoneView || visiblePhoneLine == 0) return;
        var ui = DialoguePhoneUIManager.Instance;
        bool typing = false;
        foreach (var bubble in new[] { ui.playerBubble, ui.clientBubble })
            if (bubble.gameObject.activeInHierarchy && bubble.typewriter.isShowingText)
            {
                bubble.typewriter.SkipTypewriter();
                typing = true;
            }
        if (!typing && (bool)replicaPhone["advance"]) RequestActivity(ActivityAction.PhoneAdvance);
    }

    private void HideHostPhone(PhoneUIManager phone)
    {
        if (hiddenPhone != null) return;
        // Activating the original panel first lets UIDragHandler.Awake create
        // its CanvasGroup. Reuse that group for this call.
        phone.phonePanel.SetActive(true);
        hiddenPhone = phone.phonePanel.GetComponent<CanvasGroup>();
        createdPhoneGroup = hiddenPhone == null;
        if (createdPhoneGroup) hiddenPhone = phone.phonePanel.AddComponent<CanvasGroup>();
        hiddenPhoneOriginal = (hiddenPhone.alpha, hiddenPhone.interactable, hiddenPhone.blocksRaycasts);
        hiddenPhone.alpha = 0;
        hiddenPhone.interactable = hiddenPhone.blocksRaycasts = false;
        mutedPhoneSources = new[] { phone.phonePickup, phone.phoneDrop, phone.phoneCalling, phone.phoneNoNumber,
            phone.phoneClientDrop, phone.phoneClientPickup }.Where(source => source != null).Distinct()
            .Select(source => (source, source.mute)).ToArray();
        foreach (var entry in mutedPhoneSources) entry.source.mute = true;
    }

    private void RestoreHostPhone()
    {
        if (hiddenPhone == null) return;
        var phone = PhoneUIManager.Instance;
        phone.phoneBookUIManager.CloseUI();
        phone.phonePanel.SetActive(false);
        foreach (var entry in mutedPhoneSources)
        {
            if (entry.source == null) continue;
            entry.source.Stop();
            entry.source.mute = entry.mute;
        }
        mutedPhoneSources = null;
        (hiddenPhone.alpha, hiddenPhone.interactable, hiddenPhone.blocksRaycasts) = hiddenPhoneOriginal;
        if (createdPhoneGroup) UnityEngine.Object.Destroy(hiddenPhone);
        hiddenPhone = null;
    }

    internal void CloseDisconnectedPhone()
    {
        if (!RemotePhoneExecution || PhoneUIManager.Instance == null) return;
        CancellingPhone = true;
        try { PhoneUIManager.Instance.StopCall(); }
        finally
        {
            CancellingPhone = false;
            phoneOwner = 0;
            phoneDirty = true;
            RestoreHostPhone();
        }
    }

    private void TickHostPhone()
    {
        if (Session.Host?.Closed != false || !Session.Host.IsAttached(NetSession.GuestPeer)) return;
        ObservePhone();
        if (!phoneDirty) return;
        var changes = new System.Collections.Generic.List<SyncOp>(1);
        AppendPhone(changes);
        if (changes.Count != 0) Session.Host.Local(changes);
    }

    private void AppendPhone(System.Collections.Generic.List<SyncOp> changes)
    {
        byte[] facts = CapturePhone();
        if (facts.AsSpan().SequenceEqual(publishedPhone)) return;
        publishedPhone = facts;
        changes.Add(SyncOp.Put(PhoneKey, facts));
    }
}
