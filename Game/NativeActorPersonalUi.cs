using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ProbablyCoop;

// Host-owned personal presentation journal. It contains text only, never a
// native action to execute on the replica. Shared/automatic notifications keep
// their original Host presentation; an active Partner operation is addressed.
internal sealed class NativeActorPersonalUi : IDisposable
{
    internal const int NotificationLimit = 256, MessageLimit = 8192, ColorLimit = 64;
    private readonly NativeActorRun run;
    private readonly Guid stream = Guid.NewGuid();
    private readonly int thread = Environment.CurrentManagedThreadId;
    private readonly Queue<Notice> notices = new Queue<Notice>();
    private ulong sequence;
    private bool disposed;
    internal NativeActorPersonalUi(NativeActorRun run) => this.run = run ?? throw new ArgumentNullException(nameof(run));

    internal void NotifyPartner(string message, string color)
    {
        CheckThread();
        if (disposed) throw new ObjectDisposedException(nameof(NativeActorPersonalUi));
        message ??= ""; color ??= "default";
        if (message.Length > MessageLimit || color.Length > ColorLimit)
            throw new InvalidOperationException("Native personal notification exceeded its bounded presentation contract.");
        var notice = new Notice(checked(++sequence), message, color);
        if (notices.Count == NotificationLimit) notices.Dequeue();
        notices.Enqueue(notice);
    }
    internal JObject CaptureNotifications()
    {
        CheckThread();
        if (disposed) throw new ObjectDisposedException(nameof(NativeActorPersonalUi));
        var rows = new JArray();
        foreach (var notice in notices) rows.Add(new JObject { ["seq"] = notice.Sequence, ["text"] = notice.Text, ["color"] = notice.Color });
        return new JObject { ["run"] = run.Run.ToString("D"), ["stream"] = stream.ToString("D"),
            ["through"] = sequence, ["first"] = notices.Count == 0 ? 0UL : notices.Peek().Sequence, ["messages"] = rows };
    }
    public void Dispose() { CheckThread(); disposed = true; notices.Clear(); }
    private void CheckThread()
    { if (Environment.CurrentManagedThreadId != thread) throw new InvalidOperationException("Personal presentation belongs to the game thread."); }

    internal static T ReadForPartner<T>(Func<T> query)
    {
        if (query == null) throw new ArgumentNullException(nameof(query));
        var session = NativeActorSession.Current;
        if (session == null) return query();
        using (session.Enter(NativeActorRun.Slot.Partner)) return query();
    }

    internal static string StatusProof(PlayerStore localStore, string sharedProof) =>
        (sharedProof ?? "") + "\n" + NativeActorState.Capture(localStore).Serialize().ToString(Formatting.None);

    // Called after the complete replica graph commits its facts. Never opens a
    // panel the player closed, switches tabs, or changes their window position.
    internal static void RefreshOpenStatus(PlayerStore localStore, ref string previousProof)
    {
        var ui = AdvCalendarUIManager.Instance;
        if (ui?.UIPanel?.activeSelf != true || !ui.statusSection.activeSelf) { previousProof = null; return; }
        string shared = localStore.playerCash + ":" + localStore.GetTotalEstimatedValue() + ":" + localStore.commissaryData.redeemedCredit;
        string proof = StatusProof(localStore, shared);
        if (proof == previousProof) return;
        ui.OnStatusButtonClicked();
        previousProof = proof;
    }

    internal readonly struct Notice
    {
        internal readonly ulong Sequence;
        internal readonly string Text, Color;
        internal Notice(ulong sequence, string text, string color) { Sequence = sequence; Text = text; Color = color; }
    }
    internal sealed class Notifications
    {
        internal readonly Guid Run, Stream;
        internal readonly ulong Through, First;
        internal readonly Notice[] Rows;
        private Notifications(Guid run, Guid stream, ulong through, ulong first, Notice[] rows)
        { Run = run; Stream = stream; Through = through; First = first; Rows = rows; }
        internal static Notifications Read(JToken value, Guid expectedRun)
        {
            if (expectedRun == Guid.Empty || value is not JObject data || data.Count != 5 ||
                !GuidValue(data["run"], out var run) || run != expectedRun || !GuidValue(data["stream"], out var stream) ||
                data["messages"] is not JArray messages || messages.Count > NotificationLimit)
                throw new InvalidDataException("Invalid personal notification envelope.");
            ulong through = Number(data["through"]), first = Number(data["first"]);
            if ((through == 0) != (messages.Count == 0) || (messages.Count == 0 ? first != 0 : first == 0 || through < first || through - first != (ulong)messages.Count - 1))
                throw new InvalidDataException("Invalid personal notification range.");
            var rows = new Notice[messages.Count];
            for (int i = 0; i < rows.Length; i++)
            {
                if (messages[i] is not JObject row || row.Count != 3 || Number(row["seq"]) != first + (ulong)i ||
                    row["text"]?.Type != JTokenType.String || row["color"]?.Type != JTokenType.String ||
                    ((string)row["text"]).Length > MessageLimit || ((string)row["color"]).Length > ColorLimit)
                    throw new InvalidDataException("Invalid personal notification row.");
                rows[i] = new Notice(first + (ulong)i, (string)row["text"], (string)row["color"]);
            }
            return new Notifications(run, stream, through, first, rows);
        }
        private static bool GuidValue(JToken value, out Guid id)
        { id = Guid.Empty; return value?.Type == JTokenType.String && Guid.TryParse((string)value, out id) && id != Guid.Empty; }
        private static ulong Number(JToken value)
        {
            if (value?.Type != JTokenType.Integer || !ulong.TryParse(value.ToString(Formatting.None), NumberStyles.None, CultureInfo.InvariantCulture, out ulong number))
                throw new InvalidDataException("Invalid personal notification sequence.");
            return number;
        }
    }
    internal sealed class Receiver
    {
        private readonly int thread = Environment.CurrentManagedThreadId;
        private Guid run, stream;
        internal ulong Through { get; private set; }
        internal ulong Missed { get; private set; }
        internal void Apply(Notifications batch, Action<string, string> present)
        {
            if (Environment.CurrentManagedThreadId != thread) throw new InvalidOperationException("Notification application belongs to the game thread.");
            if (batch == null || present == null) throw new ArgumentNullException();
            if (stream != Guid.Empty && (run != batch.Run || stream != batch.Stream))
                throw new InvalidDataException("Personal notification stream changed without a session reset.");
            run = batch.Run; stream = batch.Stream;
            if (batch.Through <= Through) return;
            if (batch.First > Through && batch.First - Through > 1) Missed = checked(Missed + batch.First - Through - 1);
            foreach (var notice in batch.Rows)
            {
                if (notice.Sequence <= Through) continue;
                // Presentation failure must not repeat an already displayed
                // toast on snapshot retry. This is at-most-once UI, no economy.
                Through = notice.Sequence;
                present(notice.Text, notice.Color);
            }
        }
    }
}

[HarmonyPatch(typeof(StoreUIManager), nameof(StoreUIManager.Notify))]
internal static class NativeActorPersonalNotification
{
    private static bool Prefix(string message, string color)
    {
        var session = NativeActorSession.Current;
        if (session == null || session.Run.CurrentActor != NativeActorRun.Slot.Partner) return true;
        session.PersonalUi.NotifyPartner(message, color);
        return false;
    }
}
