using System;
using System.Reflection;
using HarmonyLib;

namespace ProbablyCoop;

// The native night report stays one shared list. Only text emitted within an
// explicit personal phase receives an actor label; shop services and lottery
// are outside these scopes. No localization key or business state is changed.
internal static class NativeActorNightReport
{
    [ThreadStatic] private static Scope current;

    private sealed class Scope : IDisposable
    {
        internal readonly NativeActorSession Session;
        internal readonly NativeActorRun.Slot Actor;
        internal readonly string Name;
        private readonly Scope previous;
        private bool disposed;
        internal Scope(NativeActorSession session, NativeActorRun.Slot actor, string name)
        { Session = session; Actor = actor; Name = name; previous = current; current = this; }
        public void Dispose()
        {
            if (disposed) return;
            if (!ReferenceEquals(current, this)) throw new InvalidOperationException("Personal night report scopes must close in order.");
            current = previous; disposed = true;
        }
    }

    internal static IDisposable EnterPersonal(NativeActorSession session, NativeActorRun.Slot actor)
    {
        if (session == null || !ReferenceEquals(session, NativeActorSession.Current) || !session.NightActive || session.Run.CurrentActor != actor)
            throw new InvalidOperationException("Personal night reporting requires the active actor night scope.");
        string name = actor == NativeActorRun.Slot.Host ? session.Store.playerName : session.Run.Partner.playerName;
        return new Scope(session, actor, name);
    }

    internal static string PrefixForActor(NativeActorRun.Slot actor, string name, string text)
    {
        string role = actor switch
        {
            NativeActorRun.Slot.Host => "房主",
            NativeActorRun.Slot.Partner => "合伙人",
            _ => throw new ArgumentOutOfRangeException(nameof(actor))
        };
        name = name?.Trim();
        if (!string.IsNullOrEmpty(name))
        {
            if (name.Length > 80) name = name.Substring(0, 80);
            name = name.Replace("<", "＜").Replace(">", "＞").Replace("\r", " ").Replace("\n", " ");
            role += " · " + name;
        }
        return "[" + role + "] " + text;
    }

    [HarmonyPatch(typeof(HealthData), nameof(HealthData.OnSleep))]
    private static class PersonalSleepScope
    {
        private static void Prefix(HealthData __instance, out IDisposable __state)
        {
            __state = null;
            var session = NativeActorSession.Current;
            if (session == null || !session.NightActive || !session.InPersonalNight) return;
            var actor = session.Run.CurrentActor;
            var expected = actor == NativeActorRun.Slot.Host ? session.Store.healthData : session.Run.Partner.healthData;
            if (!ReferenceEquals(__instance, expected)) throw new InvalidOperationException("Personal night report health owner mismatch.");
            __state = EnterPersonal(session, actor);
        }
        private static Exception Finalizer(Exception __exception, IDisposable __state)
        { __state?.Dispose(); return __exception; }
    }

    [HarmonyPatch]
    private static class PersonalLogPrefix
    {
        private static System.Collections.Generic.IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(PlayerStore), nameof(PlayerStore.AddNightLog), new[] { typeof(string) });
            yield return AccessTools.Method(typeof(PlayerStore), nameof(PlayerStore.AddNightLog), new[] { typeof(string), typeof(string) });
        }
        private static void Prefix(PlayerStore __instance, ref string log)
        {
            var scope = current;
            if (scope == null || !ReferenceEquals(scope.Session, NativeActorSession.Current) ||
                !ReferenceEquals(scope.Session.Store, __instance)) return;
            log = PrefixForActor(scope.Actor, scope.Name, log);
        }
    }
}
