using System;
using System.Collections.Generic;
using System.Threading;

namespace ProbablyCoop.Core;

/// <summary>Identity of a live authority activity. Actor identity is separate from Steam identity.
/// Replacing an activity or restarting a session invalidates every previously captured callback.</summary>
internal readonly struct ActivityToken : IEquatable<ActivityToken>
{
    internal readonly Guid Session;
    internal readonly ulong Epoch;
    internal readonly uint Activity, Generation;
    internal ActivityToken(Guid session, ulong epoch, uint activity, uint generation)
    { Session = session; Epoch = epoch; Activity = activity; Generation = generation; }
    public bool Equals(ActivityToken other) => Session == other.Session && Epoch == other.Epoch && Activity == other.Activity && Generation == other.Generation;
    public override bool Equals(object obj) => obj is ActivityToken other && Equals(other);
    public override int GetHashCode() => Session.GetHashCode() ^ Epoch.GetHashCode() ^ (int)Activity ^ (int)Generation;
}

/// <summary>Unity-thread scoped execution primitive. The binding adapter owns the exact native
/// fields it redirects and must restore them on Dispose. This class does not magically isolate
/// arbitrary vanilla globals, wrap native coroutines or implement a rollback transaction.</summary>
internal sealed class ActivityScope<T> where T : class
{
    private sealed class Entry
    {
        internal ActivityToken Token;
        internal T State;
    }
    private readonly int ownerThread = Thread.CurrentThread.ManagedThreadId;
    private readonly Dictionary<uint, Entry> activities = new Dictionary<uint, Entry>();
    private readonly Dictionary<uint, uint> generations = new Dictionary<uint, uint>();
    private readonly Dictionary<uint, int> executing = new Dictionary<uint, int>();
    private readonly Func<T, IDisposable> bind;
    private Guid session = Guid.NewGuid();
    private ulong epoch = 1;
    internal ActivityToken? Current { get; private set; }
    internal ActivityScope(Func<T, IDisposable> bind) { this.bind = bind ?? throw new ArgumentNullException(nameof(bind)); }

    internal ActivityToken Register(uint id, T state)
    {
        CheckThread();
        if (id == 0 || state == null) throw new ArgumentException("Activity identity and state required");
        if (executing.ContainsKey(id)) throw new InvalidOperationException("Cannot replace an executing native activity");
        uint generation = checked((generations.TryGetValue(id, out uint old) ? old : 0) + 1);
        var token = new ActivityToken(session, epoch, id, generation);
        generations[id] = generation;
        activities[id] = new Entry { Token = token, State = state };
        return token;
    }
    internal void Close(ActivityToken token)
    {
        CheckThread();
        if (activities.TryGetValue(token.Activity, out var entry) && entry.Token.Equals(token))
        {
            if (executing.ContainsKey(token.Activity)) throw new InvalidOperationException("Cannot close an executing native activity");
            activities.Remove(token.Activity);
        }
    }
    internal bool Execute(ActivityToken token, Action<T> action)
    {
        CheckThread();
        if (action == null) throw new ArgumentNullException(nameof(action));
        if (!activities.TryGetValue(token.Activity, out var entry) || !entry.Token.Equals(token)) return false;
        var previous = Current;
        executing[token.Activity] = executing.TryGetValue(token.Activity, out int depth) ? depth + 1 : 1;
        try
        {
            using (bind(entry.State))
            {
                Current = token;
                action(entry.State);
            }
        }
        finally
        {
            Current = previous;
            if (executing[token.Activity] == 1) executing.Remove(token.Activity);
            else executing[token.Activity]--;
        }
        return true;
    }
    internal Func<bool> Capture(ActivityToken token, Action<T> action) => () => Execute(token, action);
    internal void NewEpoch()
    {
        CheckThread();
        if (executing.Count != 0) throw new InvalidOperationException("Cannot reset executing native scope");
        epoch = checked(epoch + 1); activities.Clear(); generations.Clear();
    }
    private void CheckThread()
    {
        if (Thread.CurrentThread.ManagedThreadId != ownerThread)
            throw new InvalidOperationException("Native activities must execute on their owning Unity thread");
    }
}
