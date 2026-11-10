using System;
using System.Collections.Generic;
using System.Linq;

namespace ProbablyCoop.Core;

// A local action can finish visually before its authoritative predecessor.
// Only dependent actions wait to acquire fresh source proofs. Native objects
// and transport buffers stay with their respective adapters.
internal sealed class PendingActions
{
    internal const int Capacity = 16;
    internal enum Status { Queued, Sent, Accepted, Committed, Rejected, Cancelled }

    internal sealed class Entry
    {
        internal ulong Id { get; }
        internal IReadOnlyList<uint> Sources { get; }
        internal ulong Request { get; private set; }
        internal Status State { get; private set; }
        private readonly HashSet<Entry> predecessors;
        internal bool CanSubmit => State == Status.Queued && predecessors.All(p => p.State == Status.Committed);
        internal bool Finished => State is Status.Committed or Status.Rejected or Status.Cancelled;

        internal Entry(ulong id, uint[] sources, IEnumerable<Entry> dependencies)
        {
            Id = id;
            Sources = Array.AsReadOnly((uint[])sources.Clone());
            predecessors = new HashSet<Entry>(dependencies);
        }

        internal void AddDependency(Entry predecessor)
        {
            if (State != Status.Queued || predecessor.Id >= Id)
                throw new InvalidOperationException("A queued action may depend only on an earlier action");
            predecessors.Add(predecessor);
        }

        internal bool DependsOnFailure => predecessors.Any(p => p.State is Status.Rejected or Status.Cancelled);
        internal void SetStatus(Status value) => State = value;
        internal void SetRequest(ulong request)
        {
            Request = request;
            State = Status.Sent;
            // All dependencies are committed before dispatch. Do not retain an
            // unbounded chain of finished actions during a long play session.
            predecessors.Clear();
        }
    }

    private readonly List<Entry> active = new List<Entry>();
    private readonly Dictionary<uint, Entry> latest = new Dictionary<uint, Entry>();
    private readonly Dictionary<ulong, Entry> requests = new Dictionary<ulong, Entry>();
    private ulong lastId, projectedThrough;
    internal int Count => active.Count;

    internal Entry Reserve(ulong id, uint[] sources)
    {
        if (id == 0 || id <= lastId || sources == null || sources.Length is < 1 or > 32 ||
            sources.Any(source => source == 0) || sources.Distinct().Count() != sources.Length)
            throw new ArgumentException("Invalid local action identity or source set");
        if (active.Count >= Capacity) return null;
        var entry = new Entry(id, sources, sources.Where(latest.ContainsKey).Select(source => latest[source]));
        lastId = id;
        active.Add(entry);
        foreach (uint source in sources) latest[source] = entry;
        return entry;
    }

    internal void Sent(Entry entry, ulong request)
    {
        if (!active.Contains(entry) || !entry.CanSubmit || request == 0 || requests.ContainsKey(request))
            throw new InvalidOperationException("Local action is not ready for authoritative execution");
        entry.SetRequest(request);
        requests.Add(request, entry);
    }

    internal void Result(Entry entry, bool applied)
    {
        if (!active.Contains(entry) || entry.State != Status.Sent) return;
        if (!applied) { Reject(entry); return; }
        entry.SetStatus(Status.Accepted);
        Projected(projectedThrough);
    }

    internal void Projected(ulong through)
    {
        projectedThrough = Math.Max(projectedThrough, through);
        foreach (var entry in active.ToArray())
            if (entry.State == Status.Accepted && entry.Request <= projectedThrough)
            {
                entry.SetStatus(Status.Committed);
                Retire(entry);
            }
    }

    internal void Reject(Entry entry) => FinishFailed(entry, Status.Rejected);
    internal void Cancel(Entry entry) => FinishFailed(entry, Status.Cancelled);

    private void FinishFailed(Entry entry, Status state)
    {
        if (!active.Contains(entry) || entry.Finished) return;
        entry.SetStatus(state);
        foreach (var candidate in active.ToArray())
        {
            if (candidate.DependsOnFailure) candidate.SetStatus(state);
            if (candidate.Finished) Retire(candidate);
        }
    }

    private void Retire(Entry entry)
    {
        active.Remove(entry);
        if (entry.Request != 0) requests.Remove(entry.Request);
        foreach (uint source in entry.Sources)
            if (latest.TryGetValue(source, out var current) && ReferenceEquals(current, entry))
            {
                var predecessor = active.LastOrDefault(candidate => candidate.Sources.Contains(source));
                if (predecessor == null) latest.Remove(source);
                else latest[source] = predecessor;
            }
    }

    internal void Clear()
    {
        foreach (var entry in active) entry.SetStatus(Status.Cancelled);
        active.Clear(); latest.Clear(); requests.Clear();
        lastId = projectedThrough = 0;
    }
}
