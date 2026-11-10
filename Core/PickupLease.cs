using System;
using System.Collections.Generic;

namespace ProbablyCoop.Core;

// One guest hand per authenticated connection. The owning adapter validates
// entity proofs/permissions and resets this ledger only on an epoch change.
// Request retries are deduplicated by ReplicaLink; the monotonic hand serial
// additionally prevents a later command from reviving a finished old drag.
internal sealed class PickupLease
{
    internal const int MaxItems=32;
    internal const double LifetimeSeconds=15;
    private uint[] items=Array.Empty<uint>();
    private IReadOnlyList<uint> view=Array.Empty<uint>();
    private ulong highestSerial;
    internal ulong ActiveSerial { get; private set; }
    internal IReadOnlyList<uint> Items=>view;
    internal double ExpiresAt { get; private set; }

    internal ShopResult Acquire(ulong serial,uint[] requested,double now,Func<uint,bool> approve)
    {
        ValidateTime(now); Tick(now);
        if(approve==null) throw new ArgumentNullException(nameof(approve));
        if(serial==0 || serial<=highestSerial) return ShopResult.Stale;
        highestSerial=serial;
        if(ActiveSerial!=0) return ShopResult.Busy;
        if(!ValidItems(requested)) return ShopResult.Unavailable;
        // Validate the complete group before reserving anything. No partial
        // claims if one member is stale, held by the host or no longer movable.
        foreach(uint id in requested) if(!approve(id)) return ShopResult.Unavailable;
        items=(uint[])requested.Clone(); view=Array.AsReadOnly(items);
        ActiveSerial=serial; ExpiresAt=now+LifetimeSeconds;
        return ShopResult.Applied;
    }

    internal ShopResult Renew(ulong serial,double now,Func<uint,bool> stillValid)
    {
        ValidateTime(now); Tick(now);
        if(stillValid==null) throw new ArgumentNullException(nameof(stillValid));
        if(serial==0 || ActiveSerial!=serial) return ShopResult.Stale;
        foreach(uint id in items) if(!stillValid(id)) { Clear(); return ShopResult.Stale; }
        ExpiresAt=now+LifetimeSeconds;
        return ShopResult.Applied;
    }

    internal ShopResult Release(ulong serial,double now)
    {
        ValidateTime(now); Tick(now);
        if(serial==0 || serial>highestSerial) return ShopResult.Stale;
        if(ActiveSerial==serial) { Clear(); return ShopResult.Applied; }
        // Releasing a finished hand is harmless, but it must never release the
        // next hand. Report stale while a different active hand exists.
        return ActiveSerial==0 ? ShopResult.Applied : ShopResult.Stale;
    }

    internal ShopResult Commit(ulong serial,uint[] requested,double now,Func<ShopResult> execute)
    {
        ValidateTime(now); Tick(now);
        if(execute==null) throw new ArgumentNullException(nameof(execute));
        if(serial==0 || ActiveSerial!=serial) return ShopResult.Stale;
        if(!ValidItems(requested) || requested.Length!=items.Length) return ShopResult.Stale;
        var requestedSet=new HashSet<uint>(requested);
        foreach(uint id in items) if(!requestedSet.Contains(id)) return ShopResult.Stale;
        // The adapter rechecks the original source proofs and actual destination
        // before executing. Even a rejected drop or exception ends this hand.
        try { return execute(); }
        finally { Clear(); }
    }

    internal bool Tick(double now)
    {
        ValidateTime(now);
        if(ActiveSerial==0 || now<ExpiresAt) return false;
        Clear(); return true;
    }
    internal void Reset() { Clear(); highestSerial=0; }
    private void Clear() { ActiveSerial=0; items=Array.Empty<uint>(); view=Array.Empty<uint>(); ExpiresAt=0; }
    private static bool ValidItems(uint[] requested)
    {
        if(requested==null || requested.Length==0 || requested.Length>MaxItems) return false;
        var distinct=new HashSet<uint>();
        foreach(uint id in requested) if(id==0 || !distinct.Add(id)) return false;
        return true;
    }
    private static void ValidateTime(double now)
    { if(double.IsNaN(now) || double.IsInfinity(now) || now<0) throw new ArgumentOutOfRangeException(nameof(now)); }
}
