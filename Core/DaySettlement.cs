namespace ProbablyCoop.Core;

// A timeout changes presentation, not the identity of the native night already
// in flight. Only its two receipts may finish it; nothing here retries a night.
internal sealed class DaySettlement
{
    private readonly object store;
    private readonly object session;
    private readonly string run;
    private readonly int slot;
    private readonly double deadline;
    internal int PreviousDay { get; }
    internal int ExpectedDay => PreviousDay + 1;
    internal bool Pending { get; private set; } = true;
    internal bool TimedOut { get; private set; }
    internal bool Failed { get; private set; }
    internal bool SaveFailed { get; private set; }
    internal bool Saved { get; private set; }
    internal bool SaveVerified { get; private set; }
    internal bool MorningCompleted { get; private set; }

    internal DaySettlement(object store, string run, int slot, object session, int previousDay, double deadline)
    {
        this.store = store;
        this.run = run;
        this.slot = slot;
        this.session = session;
        PreviousDay = previousDay;
        this.deadline = deadline;
    }

    internal bool Matches(object currentStore, string currentRun, int currentSlot, object currentSession) =>
        ReferenceEquals(store, currentStore) && run == currentRun && slot == currentSlot &&
        ReferenceEquals(session, currentSession);

    internal bool AcceptsDay(int day) => day == PreviousDay || day == ExpectedDay;

    internal void ConfirmSave(bool verified)
    {
        if (!Pending) return;
        if (!verified) { Fail(saveFailed: true); return; }
        SaveVerified = true;
    }

    internal void ConfirmMorning(bool ranOriginal)
    {
        if (!Pending) return;
        if (!ranOriginal) { Fail(); return; }
        MorningCompleted = true;
    }

    internal bool Expire(double now)
    {
        if (!Pending || TimedOut || now < deadline) return false;
        TimedOut = true;
        return true;
    }

    // Called from the next game tick, after native postfixes/finalizers finish.
    internal bool TryComplete()
    {
        if (!Pending || !SaveVerified || !MorningCompleted) return false;
        Pending = false;
        Saved = true;
        return true;
    }

    internal void Fail(bool saveFailed = false)
    {
        if (!Pending) return;
        SaveFailed = saveFailed;
        Pending = false;
        Failed = true;
        // Keep receipts for recovery messaging. Failed still prevents completion
        // and late callbacks cannot change the outcome of this attempt.
    }
}
