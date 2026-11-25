using System;
using System.Collections.Generic;

namespace ProbablyCoop.Core;

// Authority for actor locations and the lifetime of a shared destination.
// Scene presentation and native storage cleanup are committed by the caller.
internal sealed class TravelSiteLedger<TSite> where TSite : class
{
    internal readonly struct Position
    {
        internal readonly string Site;
        internal readonly ulong Generation;
        internal Position(string site, ulong generation) { Site = site; Generation = generation; }
        internal bool Matches(Position other) => Site == other.Site && Generation == other.Generation;
    }

    internal readonly struct Leave
    {
        internal readonly Position From;
        internal readonly bool LastVisitor;
        internal readonly TSite State;
        internal Leave(Position from, bool lastVisitor, TSite state)
        { From = from; LastVisitor = lastVisitor; State = state; }
    }
    private sealed class Site
    {
        internal readonly TSite State;
        internal byte Occupants;
        internal Site(TSite state) => State = state;
    }

    private readonly Dictionary<string, Site> sites = new Dictionary<string, Site>(StringComparer.Ordinal);
    private readonly Position[] positions = { new Position("", 1), new Position("", 1) };
    private readonly bool[] spent = new bool[2];
    private int night = -1;
    private ulong nextGeneration = 1;
    internal bool EveryoneHome => positions[0].Site.Length == 0 && positions[1].Site.Length == 0;

    internal bool BeginNight(int day)
    {
        if (day < night || day < 0) return false;
        if (day == night) return true;
        if (!EveryoneHome || sites.Count != 0) return false;
        night = day;
        Array.Clear(spent, 0, spent.Length);
        for (int i = 0; i < positions.Length; i++) Move(i, "");
        return true;
    }

    internal Position Where(int actor) => positions[actor];
    internal int VisitsLeft(int actor) => spent[actor] ? 0 : 1;
    internal TSite At(string destination) => sites.TryGetValue(destination, out var site) ? site.State : null;

    internal bool TryEnter(int actor, Position expected, string destination, Func<TSite> create, out TSite state)
    {
        state = null;
        if (night < 0 || string.IsNullOrEmpty(destination) || !expected.Matches(positions[actor]) ||
            expected.Site.Length != 0 || spent[actor]) return false;
        var site = GetOrCreate(destination, create);
        site.Occupants |= (byte)(1 << actor);
        Move(actor, destination);
        spent[actor] = true;
        state = site.State;
        return true;
    }

    internal bool TryPrepareLeave(int actor, Position expected, out Leave leave)
    {
        leave = default;
        if (!expected.Matches(positions[actor]) || !sites.TryGetValue(expected.Site, out var site)) return false;
        leave = new Leave(expected, site.Occupants == (1 << actor), site.State);
        return true;
    }

    internal bool TryLeave(int actor, Leave leave, bool confirmed)
    {
        if (!TryPrepareLeave(actor, leave.From, out var current) ||
            current.LastVisitor != leave.LastVisitor || !ReferenceEquals(current.State, leave.State) ||
            current.LastVisitor && !confirmed) return false;
        LeaveSite(actor);
        return true;
    }

    // Existing paired travel uses the vanilla shared itinerary. It updates the
    // same positions when the native transition has actually arrived.
    internal TSite ObservePaired(int day, string destination, Func<TSite> create)
    {
        destination ??= "";
        if (destination.Length == 0)
        {
            for (int i = 0; i < positions.Length; i++)
                if (positions[i].Site.Length != 0) LeaveSite(i);
            BeginNight(day);
            return null;
        }
        if (EveryoneHome) BeginNight(day);
        var result = GetOrCreate(destination, create);
        for (int i = 0; i < positions.Length; i++)
        {
            if (positions[i].Site != destination)
            {
                if (positions[i].Site.Length != 0) LeaveSite(i);
                Move(i, destination);
            }
            result.Occupants |= (byte)(1 << i);
            spent[i] = true;
        }
        return result.State;
    }

    private Site GetOrCreate(string destination, Func<TSite> create)
    {
        if (!sites.TryGetValue(destination, out var result))
        {
            TSite state = create() ?? throw new InvalidOperationException("A destination needs a shared state.");
            sites.Add(destination, result = new Site(state));
        }
        return result;
    }

    private void LeaveSite(int actor)
    {
        string location = positions[actor].Site;
        var site = sites[location];
        site.Occupants &= (byte)~(1 << actor);
        if (site.Occupants == 0) sites.Remove(location);
        Move(actor, "");
    }

    private void Move(int actor, string destination) => positions[actor] = new Position(destination, ++nextGeneration);
}
