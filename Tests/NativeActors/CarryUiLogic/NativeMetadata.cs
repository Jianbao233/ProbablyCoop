using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Cryptography;

// Separate execution mode: original game classes allocated without constructors
// test the real CLR type/field contract, never Unity rendering or gameplay.
internal static class NativeMetadata
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static int checks;
    private static void Check(bool pass, string name) { checks++; if (!pass) throw new Exception(name); }
    private static object Raw(Type type) => RuntimeHelpers.GetUninitializedObject(type);
    private static void Set(object instance, string field, object value) =>
        (instance.GetType().GetField(field, Fields) ?? throw new MissingFieldException(instance.GetType().FullName, field)).SetValue(instance, value);
    internal static int Run(string[] args)
    {
        if (args.Length != 1 || !File.Exists(args[0])) throw new ArgumentException("Pass independently built ProbablyCoop.dll for native metadata mode.");
        string managed = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "native-managed-path.txt")).Trim();
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            string candidate = Path.Combine(managed, name.Name + ".dll");
            return File.Exists(candidate) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate) : null;
        };
        try
        {
            var mod = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(args[0]));
            var game = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(managed, "Assembly-CSharp.dll"));
            Check(game.ManifestModule.ModuleVersionId == new Guid("911508a6-e0eb-4cc0-91e8-e14b16ea28d7"), "game MVID drift");
            using (var stream = File.OpenRead(game.Location))
            using (var sha = SHA256.Create())
                Check(Convert.ToHexString(sha.ComputeHash(stream)) == "725CB999D019DFF1FF31BED353C7F9BB8D81A7605A15DE1F80F5A3A6CF7B1D05", "game binary drift");
            var ui = mod.GetType("ProbablyCoop.NativeActorCarryUi", true);
            var predicate = ui.GetMethod("IsNativePanel", BindingFlags.NonPublic | BindingFlags.Static);
            var ready = ui.GetMethod("LayoutReady", BindingFlags.NonPublic | BindingFlags.Static);
            Type Panel = game.GetType("MachineryPanel", true), Intake = game.GetType("MachineryIntake", true);
            var inventories = new[] { Raw(game.GetType("GameGridInventory", true)), Raw(game.GetType("GameSlotInventory", true)),
                Raw(game.GetType("GameSlotInventory", true)), Raw(game.GetType("GameSlotInventory", true)) };
            var panels = new object[4]; var intakes = new object[4];
            var roles = new[] { "ground", "pocket1", "backpack", "pocket2" };
            for (int index = 0; index < 4; index++)
            {
                intakes[index] = Raw(Intake); Set(intakes[index], "<content>k__BackingField", inventories[index]);
                panels[index] = Raw(Panel); Set(panels[index], "role", roles[index]);
                Set(panels[index], "<content>k__BackingField", intakes[index]);
                Check((bool)predicate.Invoke(null, new[] { panels[index], inventories[index] }), "real native wrapped inventory rejected " + roles[index]);
            }
            Check(!(bool)predicate.Invoke(null, new[] { panels[1], inventories[3] }), "real foreign pocket accepted");
            Set(panels[0], "<content>k__BackingField", inventories[0]);
            Check(!(bool)predicate.Invoke(null, new[] { panels[0], inventories[0] }), "unwrapped direct inventory accepted");
            Set(panels[0], "<content>k__BackingField", intakes[0]);
            var extra = Raw(Intake); Set(extra, "<content>k__BackingField", inventories[0]);
            Set(intakes[0], "<content>k__BackingField", extra);
            Check(!(bool)predicate.Invoke(null, new[] { panels[0], inventories[0] }), "nested ambiguous intake accepted");
            Set(intakes[0], "<content>k__BackingField", inventories[0]);
            Check(!(bool)predicate.Invoke(null, new[] { panels[0], null }), "missing inventory accepted");
            Check(!(bool)predicate.Invoke(null, new[] { null, inventories[0] }), "missing panel accepted");
            var row = Raw(game.GetType("MachineryPanelRow", true));
            var list = (IList)Activator.CreateInstance(typeof(System.Collections.Generic.List<>).MakeGenericType(Panel));
            foreach (var panel in panels) list.Add(panel);
            Set(row, "panels", list);
            var window = Raw(game.GetType("PixelWindow", true)); Set(window, "<childElement>k__BackingField", row);
            Set(window, "<titleString>k__BackingField", "Ground");
            var entry = Raw(game.GetType("EmporiumEntry", true)); Set(entry, "afterhourWindow", window);
            Set(entry, "afterhourInventory", inventories[0]); Set(entry, "afterhourPocketSlotInvLeft", inventories[1]);
            Set(entry, "afterhourPocketSlotInvBackpack", inventories[2]); Set(entry, "afterhourPocketSlotInvRight", inventories[3]);
            var carry = Raw(mod.GetType("ProbablyCoop.NativeActorCarry", true)); Set(carry, "source", entry);
            var ctor = ui.GetNestedType("Binding", BindingFlags.NonPublic).GetConstructor(Fields, null, new[] { carry.GetType() }, null);
            Check(ctor.Invoke(new[] { carry }) != null, "real native panel binding failed");
            Check((bool)ready.Invoke(null, new[] { carry }), "attached native layout not ready");
            Set(window, "<childElement>k__BackingField", null);
            Check(!(bool)ready.Invoke(null, new[] { carry }), "unattached native layout considered ready");
            Set(window, "<childElement>k__BackingField", row);
            Set(intakes[1], "<content>k__BackingField", inventories[3]);
            try { ctor.Invoke(new[] { carry }); throw new Exception("wrong native pocket binding accepted"); }
            catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException) { checks++; }
            Console.WriteLine("ActorCarryUi REAL METADATA / RAW OBJECT PASS checks=" + checks + "; no native constructors, Unity or gameplay executed");
            return 0;
        }
        catch (Exception error) { Console.WriteLine(error); return 1; }
    }
}
